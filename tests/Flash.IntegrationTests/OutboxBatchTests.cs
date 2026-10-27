using System.Collections.Concurrent;
using Flash.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flash.IntegrationTests;

public class OutboxBatchTests
{
    private static async Task<int> SeedAsync(int rows)
    {
        await TestDatabase.OpenAsync();
        await using var db = TestDatabase.Orders();
        for (var i = 0; i < rows; i++)
            db.Enqueue("orders", "test.event", Guid.NewGuid(), new { n = i });
        await db.SaveChangesAsync();
        return rows;
    }

    [Fact]
    public async Task TwoRelays_DrainTheSameOutbox_WithoutPublishingAnyRowTwice()
    {
        var total = await SeedAsync(40);
        var published = new ConcurrentBag<long>();

        async Task Relay()
        {
            while (true)
            {
                await using var db = TestDatabase.Orders();
                var n = await OutboxBatch.PublishPendingAsync(db, async row =>
                {
                    await Task.Delay(30); // a slow broker keeps the batch open, so the other relay really does overlap
                    published.Add(row.Id);
                }, 5, default);
                if (n == 0)
                    break;
            }
        }

        await Task.WhenAll(Relay(), Relay(), Relay());

        Assert.Equal(total, published.Count);
        Assert.Equal(total, published.Distinct().Count());
        await using var check = TestDatabase.Orders();
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.SentAt == null));
    }

    [Fact]
    public async Task ARelayThatFailsHalfWay_LeavesTheRowsPendingForTheNextOne()
    {
        await SeedAsync(6);
        var seen = 0;
        await using (var db = TestDatabase.Orders())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => OutboxBatch.PublishPendingAsync(db, _ =>
                ++seen == 3 ? throw new InvalidOperationException("broker down") : Task.CompletedTask, 10, default));
        }

        await using var check = TestDatabase.Orders();
        Assert.Equal(6, await check.Outbox.CountAsync(o => o.SentAt == null)); // the batch rolled back as a whole
    }

    [Fact]
    public async Task Trim_RemovesOldSentRows_AndOldClaims_ButNotPendingOrRecentOnes()
    {
        await SeedAsync(3);
        await using (var db = TestDatabase.Orders())
        {
            var rows = await db.Outbox.OrderBy(o => o.Id).ToListAsync();
            rows[0].SentAt = DateTimeOffset.UtcNow.AddDays(-30); // sent long ago: goes
            rows[1].SentAt = DateTimeOffset.UtcNow;              // sent just now: stays
            // rows[2] is still pending: stays whatever its age
            db.Inbox.Add(new InboxMessage { Consumer = "x", MessageId = Guid.NewGuid(), ClaimedAt = DateTimeOffset.UtcNow.AddDays(-90) });
            db.Inbox.Add(new InboxMessage { Consumer = "x", MessageId = Guid.NewGuid(), ClaimedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        await using var trim = TestDatabase.Orders();
        var (outbox, inbox) = await OutboxBatch.TrimAsync(trim, TimeSpan.FromDays(7), TimeSpan.FromDays(30), default);

        Assert.Equal((1, 1), (outbox, inbox));
        await using var check = TestDatabase.Orders();
        Assert.Equal(2, await check.Outbox.CountAsync());
        Assert.Equal(1, await check.Inbox.CountAsync());
    }
}
