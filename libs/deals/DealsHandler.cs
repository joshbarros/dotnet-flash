using Flash.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flash.Deals;

public enum ReserveOutcome { Held, Short, CapReached, Duplicate }

/// <summary>
/// The only writer of Deal.PiecesLeft and of piece holds.
/// Two atomic steps in one transaction: a per-shopper cap upsert that only moves while the new total fits, then one
/// conditional UPDATE that matches only while the remaining Pieces still covers the request.
/// </summary>
public class DealsHandler(DealsDbContext db, IOptions<DealOptions>? options = null) : IMessageHandler
{
    public const string Name = "deals";

    public Task HandleAsync(Delivery delivery, CancellationToken ct) => delivery.RoutingKey switch
    {
        Topology.ReserveKey => ReserveAsync(delivery.MessageId, Json.Parse<ReservePieces>(delivery.Body), ct),
        Topology.ConfirmKey => ConfirmAsync(delivery.MessageId, Json.Parse<ConfirmPieces>(delivery.Body), ct),
        Topology.ReleaseKey => ReleaseAsync(delivery.MessageId, Json.Parse<ReleasePieces>(delivery.Body), ct),
        _ => throw new InvalidOperationException($"Deals does not handle '{delivery.RoutingKey}'."),
    };

    public async Task<ReserveOutcome> ReserveAsync(Guid messageId, ReservePieces command, CancellationToken ct = default)
    {
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            if (!await db.ClaimAsync(Name, messageId, ct))
            {
                await tx.CommitAsync(ct);
                return ReserveOutcome.Duplicate;
            }

            // 1. the per-shopper cap: one atomic upsert that only moves while the new total still fits
            var withinCap = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO deals."DealShoppers" ("DealId", "OwnerId", "Pieces")
                SELECT d."Id", {command.OwnerId}, {command.Pieces} FROM deals."Deals" d
                WHERE d."Id" = {command.DealId} AND d."MaxPerShopper" >= {command.Pieces}
                ON CONFLICT ("DealId", "OwnerId") DO UPDATE SET "Pieces" = deals."DealShoppers"."Pieces" + {command.Pieces}
                WHERE deals."DealShoppers"."Pieces" + {command.Pieces} <=
                      (SELECT "MaxPerShopper" FROM deals."Deals" WHERE "Id" = {command.DealId})
                """, ct);

            // 2. the remaining pieces: one conditional UPDATE, the only writer of PiecesLeft
            var updated = withinCap == 1
                ? await db.Deals
                    .Where(s => s.Id == command.DealId && s.PiecesLeft >= command.Pieces)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.PiecesLeft, x => x.PiecesLeft - command.Pieces), ct)
                : 0;

            if (withinCap == 1 && updated == 1)
            {
                var now = DateTimeOffset.UtcNow;
                DateTimeOffset? expiresAt = command.Hold ? now.AddSeconds(options?.Value.HoldSeconds ?? 600) : null;
                db.Holds.Add(new PieceHold
                {
                    OrderId = command.OrderId,
                    OwnerId = command.OwnerId,
                    DealId = command.DealId,
                    Pieces = command.Pieces,
                    Status = HoldStatus.Held,
                    CreatedAt = now,
                    ExpiresAt = expiresAt,
                    Reference = command.Reference,
                    Confirmed = !command.Hold,
                });
                db.Enqueue("deals", Topology.HeldKey, command.OrderId,
                    new PiecesHeld(command.OrderId, command.OwnerId, command.DealId, command.Pieces, command.Reference, expiresAt));
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return ReserveOutcome.Held;
            }

            // Either the cap or the stock said no. Roll back, so a cap increment never outlives a failed reserve.
            await tx.RollbackAsync(ct);
        }

        // The refusal is recorded in a fresh transaction (the claim was rolled back with the attempt).
        db.ChangeTracker.Clear();
        await using var refusal = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await refusal.CommitAsync(ct);
            return ReserveOutcome.Duplicate;
        }

        var deal = await db.Deals.AsNoTracking().Where(s => s.Id == command.DealId)
            .Select(s => new { s.PiecesLeft, s.MaxPerShopper }).FirstOrDefaultAsync(ct);
        var held = await db.Shoppers.AsNoTracking()
            .Where(c => c.DealId == command.DealId && c.OwnerId == command.OwnerId)
            .Select(c => c.Pieces).FirstOrDefaultAsync(ct);
        var overCap = deal is not null && held + command.Pieces > deal.MaxPerShopper;
        db.Enqueue("deals", Topology.ShortKey, command.OrderId,
            new PiecesShort(command.OrderId, command.Reference, command.Pieces, deal?.PiecesLeft ?? 0,
                overCap ? "cap" : "stock", deal?.MaxPerShopper ?? 0));
        await db.SaveChangesAsync(ct);
        await refusal.CommitAsync(ct);
        return overCap ? ReserveOutcome.CapReached : ReserveOutcome.Short;
    }

    /// <summary>Gives held pieces back exactly once. A second release for the same order changes nothing.</summary>
    public async Task<bool> ReleaseAsync(Guid messageId, ReleasePieces command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return false;
        }

        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.OrderId == command.OrderId, ct);
        var released = false;
        if (hold is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var flipped = await db.Holds
                .Where(h => h.OrderId == command.OrderId && h.Status == HoldStatus.Held)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.Status, HoldStatus.Released)
                    .SetProperty(h => h.ReleasedAt, now)
                    .SetProperty(h => h.ReleaseReason, command.Reason), ct);
            if (flipped == 1)
            {
                await db.Deals
                    .Where(s => s.Id == hold.DealId && s.PiecesLeft + hold.Pieces <= s.PiecesTotal)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.PiecesLeft, x => x.PiecesLeft + hold.Pieces), ct);
                await db.Shoppers
                    .Where(c => c.DealId == hold.DealId && c.OwnerId == hold.OwnerId && c.Pieces >= hold.Pieces)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Pieces, x => x.Pieces - hold.Pieces), ct);
                db.Enqueue("deals", Topology.ReleasedKey, command.OrderId,
                    new PiecesReleased(command.OrderId, hold.Pieces, command.Reason));
                released = true;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return released;
    }

    /// <summary>
    /// The shopper pays: pins a pending hold. One conditional UPDATE, so it races the sweeper safely: whichever
    /// reaches the row first wins. Pinning re-announces the hold without an expiry, which is what issues the receipt.
    /// </summary>
    public async Task<bool> ConfirmAsync(Guid messageId, ConfirmPieces command, CancellationToken ct = default, DateTimeOffset? now = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return false;
        }

        var at = now ?? DateTimeOffset.UtcNow;
        var pinned = await db.Holds
            .Where(h => h.OrderId == command.OrderId && h.Status == HoldStatus.Held && !h.Confirmed && h.ExpiresAt > at)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.Confirmed, true), ct);
        if (pinned == 1)
        {
            var hold = await db.Holds.AsNoTracking().FirstAsync(h => h.OrderId == command.OrderId, ct);
            db.Enqueue("deals", Topology.HeldKey, command.OrderId,
                new PiecesHeld(hold.OrderId, hold.OwnerId, hold.DealId, hold.Pieces, hold.Reference));
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return pinned == 1;
    }

    /// <summary>
    /// Takes back the pieces of holds nobody confirmed in time. Each hold is claimed by a conditional UPDATE ... RETURNING
    /// inside one transaction, so two instances (or a confirm) can never both act on the same hold.
    /// Returns how many holds expired.
    /// </summary>
    public async Task<int> SweepAsync(DateTimeOffset now, int batch = 50, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var expired = await db.Database.SqlQuery<ExpiredHold>($"""
            UPDATE deals."PieceHolds" SET "Status" = 'Released', "ReleasedAt" = {now}, "ReleaseReason" = 'expired'
            WHERE "OrderId" IN (
                SELECT "OrderId" FROM deals."PieceHolds"
                WHERE "Status" = 'Held' AND NOT "Confirmed" AND "ExpiresAt" <= {now}
                ORDER BY "ExpiresAt" LIMIT {batch} FOR UPDATE SKIP LOCKED)
            RETURNING "OrderId", "DealId", "OwnerId", "Pieces"
            """).ToListAsync(ct);

        foreach (var hold in expired)
        {
            await db.Deals
                .Where(s => s.Id == hold.DealId && s.PiecesLeft + hold.Pieces <= s.PiecesTotal)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.PiecesLeft, x => x.PiecesLeft + hold.Pieces), ct);
            await db.Shoppers
                .Where(c => c.DealId == hold.DealId && c.OwnerId == hold.OwnerId && c.Pieces >= hold.Pieces)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Pieces, x => x.Pieces - hold.Pieces), ct);
            db.Enqueue("deals", Topology.ExpiredKey, hold.OrderId, new PiecesExpired(hold.OrderId, hold.Pieces));
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return expired.Count;
    }

    public sealed class ExpiredHold
    {
        public Guid OrderId { get; set; }
        public Guid DealId { get; set; }
        public Guid OwnerId { get; set; }
        public int Pieces { get; set; }
    }
}
