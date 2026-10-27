using Flash.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flash.Orders;

public enum MarkOutcome { Marked, Skipped, Duplicate }

/// <summary>Consumes the events that move an order forward. Each step runs only from the status it expects.</summary>
public class OrdersHandler(OrdersDbContext db, IOptions<OrderOptions> options) : IMessageHandler
{
    public const string Name = "orders";

    public Task HandleAsync(Delivery d, CancellationToken ct) => d.RoutingKey switch
    {
        Topology.HeldKey => MarkHeldAsync(d.MessageId, Json.Parse<PiecesHeld>(d.Body), ct),
        Topology.ExpiredKey => MarkExpiredAsync(d.MessageId, Json.Parse<PiecesExpired>(d.Body), ct),
        Topology.ShortKey => MarkShortAsync(d.MessageId, Json.Parse<PiecesShort>(d.Body), ct),
        Topology.IssuedKey => MarkSoldAsync(d.MessageId, Json.Parse<ReceiptIssued>(d.Body), ct),
        Topology.RejectedKey => MarkRejectedAsync(d.MessageId, Json.Parse<ReceiptRejected>(d.Body), ct),
        Topology.ReleasedKey => MarkReleasedAsync(d.MessageId, Json.Parse<PiecesReleased>(d.Body), ct),
        _ => throw new InvalidOperationException($"Orders does not handle '{d.RoutingKey}'."),
    };

    public Task<MarkOutcome> MarkHeldAsync(Guid messageId, PiecesHeld m, CancellationToken ct = default) =>
        MarkHeldAsync(messageId, m.OrderId, m.ExpiresAt, ct);

    /// <summary>Held marks arrive twice for a pending hold: first with the expiry, again (no expiry) once it is confirmed. Only the first changes the status.</summary>
    public Task<MarkOutcome> MarkHeldAsync(Guid messageId, Guid orderId, DateTimeOffset? expiresAt = null, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Orders
            .Where(b => b.Id == orderId && b.Status == OrderStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, OrderStatus.Held)
                .SetProperty(b => b.HoldExpiresAt, expiresAt)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    /// <summary>The sweeper took the pieces back. Only a Held order expires; one that was refunded or sold stays as it is.</summary>
    public Task<MarkOutcome> MarkExpiredAsync(Guid messageId, PiecesExpired m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Orders
            .Where(b => b.Id == m.OrderId && b.Status == OrderStatus.Held)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, OrderStatus.Expired)
                .SetProperty(b => b.Reason, "The hold expired before it was confirmed.")
                .SetProperty(b => b.PiecesReleasedAt, DateTimeOffset.UtcNow)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct),
            orderIdForOrderCheck: m.OrderId);

    public Task<MarkOutcome> MarkShortAsync(Guid messageId, PiecesShort m, CancellationToken ct = default)
    {
        var reason = m.Reason == "cap"
            ? $"Cap: at most {m.PerShopperLimit} pieces per shopper in this deal."
            : $"Short: {m.RequestedPieces} pieces wanted, {m.PiecesLeft} left.";
        return StepAsync(messageId, ct, () => db.Orders
            .Where(b => b.Id == m.OrderId && b.Status == OrderStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, OrderStatus.Rejected)
                .SetProperty(b => b.Reason, reason)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));
    }

    /// <summary>The final mark. If a refund got there first the row is no longer Held and nothing changes.</summary>
    public async Task<MarkOutcome> MarkSoldAsync(Guid messageId, ReceiptIssued m, CancellationToken ct = default)
    {
        if (options.Value.FinalMarkDelayMs > 0)
            await Task.Delay(options.Value.FinalMarkDelayMs, ct);

        return await StepAsync(messageId, ct, () => db.Orders
            .Where(b => b.Id == m.OrderId && b.Status == OrderStatus.Held)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, OrderStatus.Sold)
                .SetProperty(b => b.ReceiptNumber, m.Number)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct),
            orderIdForOrderCheck: m.OrderId);
    }

    public Task<MarkOutcome> MarkRejectedAsync(Guid messageId, ReceiptRejected m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Orders
            .Where(b => b.Id == m.OrderId && (b.Status == OrderStatus.Held || b.Status == OrderStatus.Requested))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, OrderStatus.Rejected)
                .SetProperty(b => b.Reason, m.Reason)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    public Task<MarkOutcome> MarkReleasedAsync(Guid messageId, PiecesReleased m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Orders
            .Where(b => b.Id == m.OrderId && b.PiecesReleasedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.PiecesReleasedAt, DateTimeOffset.UtcNow), ct));

    private async Task<MarkOutcome> StepAsync(Guid messageId, CancellationToken ct, Func<Task<int>> update, Guid? orderIdForOrderCheck = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return MarkOutcome.Duplicate;
        }

        var changed = await update();
        if (changed == 0 && orderIdForOrderCheck is { } id)
        {
            // Skipped for a reason we can name? Requested means the "Held" mark has not landed yet: retry, do not deal.
            var status = await db.Orders.AsNoTracking().Where(b => b.Id == id).Select(b => b.Status).FirstOrDefaultAsync(ct);
            if (status == OrderStatus.Requested)
                throw new OutOfOrderException("Final mark arrived before the Held mark.");
        }

        await tx.CommitAsync(ct);
        return changed == 1 ? MarkOutcome.Marked : MarkOutcome.Skipped;
    }
}
