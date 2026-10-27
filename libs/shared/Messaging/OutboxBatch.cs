using Microsoft.EntityFrameworkCore;

namespace Flash.Shared.Messaging;

/// <summary>
/// The outbox as a queue several API instances can drain at once.
/// A batch is claimed with FOR UPDATE SKIP LOCKED inside a transaction, so two relays never publish the same row;
/// if one dies mid-batch its rows unlock and are published again, which the consumers' inbox absorbs.
/// </summary>
public static class OutboxBatch
{
    public static async Task<int> PublishPendingAsync(ModuleDbContext db, Func<OutboxMessage, Task> publish, int size, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // The schema is validated against ^[a-z_]{1,32}$ by ModuleDbContext and the size is an int, so neither can carry SQL.
#pragma warning disable EF1002
        var rows = await db.Outbox
            .FromSqlRaw($"SELECT * FROM \"{db.Schema}\".\"OutboxMessages\" WHERE \"SentAt\" IS NULL ORDER BY \"Id\" LIMIT {size} FOR UPDATE SKIP LOCKED")
            .ToListAsync(ct);
#pragma warning restore EF1002
        foreach (var row in rows)
        {
            await publish(row);
            row.SentAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return rows.Count;
    }

    /// <summary>
    /// Deletes outbox rows that were sent long ago, and inbox claims older than any redelivery could be.
    /// The inbox must outlive the broker's redelivery window, so it is kept much longer than the outbox.
    /// </summary>
    public static async Task<(int Outbox, int Inbox)> TrimAsync(ModuleDbContext db, TimeSpan outboxRetention, TimeSpan inboxRetention, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var sentBefore = now - outboxRetention;
        var claimedBefore = now - inboxRetention;
        var outbox = await db.Outbox.Where(o => o.SentAt != null && o.SentAt < sentBefore).ExecuteDeleteAsync(ct);
        var inbox = await db.Inbox.Where(i => i.ClaimedAt < claimedBefore).ExecuteDeleteAsync(ct);
        return (outbox, inbox);
    }
}
