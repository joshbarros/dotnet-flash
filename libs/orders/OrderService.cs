using Flash.Shared;
using Flash.Shared.Auth;
using Flash.Shared.Contracts;
using Flash.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Flash.Orders;

/// <summary>HTTP side of the saga: writes the order and its first outbox row in one local transaction.</summary>
public class OrderService(OrdersDbContext db, IDealCatalog deals, ICurrentUser user)
{
    public async Task<Order> CreateAsync(CreateOrderRequest request, CancellationToken ct)
    {
        var ownerId = user.Id ?? throw new DomainException(401, "Sign in to book.");
        if (string.IsNullOrWhiteSpace(request.DealCode))
            throw new DomainException(400, "Deal code is required.");
        var error = OrderRules.Validate(request.Reference, request.Pieces);
        if (error is not null)
            throw new DomainException(400, error);

        var deal = await deals.FindAsync(request.DealCode.Trim(), ct)
            ?? throw new DomainException(404, $"Deal '{request.DealCode}' does not exist.");

        var pieces = (int)request.Pieces;
        var reference = request.Reference!.Trim();
        var now = DateTimeOffset.UtcNow;
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId,
            DealId = deal.Id,
            DealCode = deal.Code,
            Reference = reference,
            Pieces = pieces,
            TotalCents = OrderRules.TotalCents(deal.PriceCents, pieces),
            Status = OrderStatus.Requested,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Orders.Add(order);
        db.Enqueue("orders", Topology.ReserveKey, order.Id, new ReservePieces(order.Id, ownerId, deal.Id, pieces, reference, request.Hold));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DomainException(409, $"Reference '{reference}' is already booked on {deal.Code}.");
        }
        await tx.CommitAsync(ct);
        return order;
    }

    /// <summary>The owner or staff only. Anyone else gets a 404, so ids cannot be probed.</summary>
    public async Task<Order> GetAsync(Guid id, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (order is null || !OwnerAccess.CanAccess(user, order.OwnerId))
            throw new DomainException(404, "Order not found.");
        return order;
    }

    public async Task<(List<Order> Items, int Total)> ListAsync(string? dealCode, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Orders.AsNoTracking();
        if (!user.IsStaff)
        {
            var me = user.Id ?? throw new DomainException(401, "Sign in to see orders.");
            query = query.Where(b => b.OwnerId == me);
        }
        if (!string.IsNullOrWhiteSpace(dealCode))
            query = query.Where(b => b.DealCode == dealCode);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(b => b.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    /// <summary>
    /// The shopper pays for a held order. Only a pending hold can be confirmed; the Deals module decides whether the hold
    /// is still alive (it races the expiry sweeper), so this only asks.
    /// </summary>
    public async Task<Order> ConfirmAsync(Guid id, CancellationToken ct)
    {
        var order = await GetAsync(id, ct); // 404 unless the caller may see it
        if (order.Status != OrderStatus.Held || order.HoldExpiresAt is null)
            throw new DomainException(409, order.Status == OrderStatus.Expired
                ? "The hold has expired; start again."
                : $"An order in status {order.Status} has no pending hold to confirm.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Enqueue("orders", Topology.ConfirmKey, id, new ConfirmPieces(id));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return order;
    }

    /// <summary>Moves Held or Sold to Refunded and asks Deals to give the pieces back. The receipt is not touched.</summary>
    public async Task<Order> RefundAsync(Guid id, CancellationToken ct)
    {
        await GetAsync(id, ct); // 404 unless the caller may see it

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var moved = await db.Orders
            .Where(b => b.Id == id && (b.Status == OrderStatus.Held || b.Status == OrderStatus.Sold))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, OrderStatus.Refunded)
                .SetProperty(b => b.Reason, "Refunded by request.")
                .SetProperty(b => b.UpdatedAt, now), ct);
        if (moved != 1)
        {
            var current = await db.Orders.AsNoTracking().FirstAsync(b => b.Id == id, ct);
            throw new DomainException(409, $"An order in status {current.Status} cannot be refunded.");
        }

        db.Enqueue("orders", Topology.ReleaseKey, id, new ReleasePieces(id, "refund"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await db.Orders.AsNoTracking().FirstAsync(b => b.Id == id, ct);
    }
}
