using Flash.Deals;
using Flash.Orders;
using Flash.Receipts;
using Flash.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Flash.IntegrationTests;

public class HoldExpiryTests
{
    private static DealsHandler Handler(DealsDbContext db) => new(db, Options.Create(new DealOptions { HoldSeconds = 60 }));

    private static async Task<Guid> HoldAsync(Deal deal, int pieces, Guid? owner = null, bool hold = true)
    {
        var order = Guid.CreateVersion7();
        await using var db = TestDatabase.Deals();
        await Handler(db).ReserveAsync(Guid.CreateVersion7(),
            new ReservePieces(order, owner ?? Guid.NewGuid(), deal.Id, pieces, $"SHP-{order:N}"[..12], hold));
        return order;
    }

    [Fact]
    public async Task APendingHold_KeepsItsPieces_UntilItsTimeRunsOut_ThenTheyComeBack()
    {
        await TestDatabase.OpenAsync();
        var deal = await TestDatabase.AddDealAsync(piecesTotal: 10, maxPerShopper: 4);
        var owner = Guid.NewGuid();
        var order = await HoldAsync(deal, 3, owner);

        await using var db = TestDatabase.Deals();
        var hold = await db.Holds.SingleAsync(h => h.OrderId == order);
        Assert.False(hold.Confirmed);
        Assert.NotNull(hold.ExpiresAt);
        var announced = await db.Outbox.SingleAsync(o => o.RoutingKey == Topology.HeldKey);
        Assert.Contains("expiresAt", announced.Payload); // the receipt waits for the confirmation

        Assert.Equal(0, await Handler(db).SweepAsync(hold.ExpiresAt!.Value.AddSeconds(-1)));
        Assert.Equal(1, await Handler(db).SweepAsync(hold.ExpiresAt!.Value.AddSeconds(1)));

        await using var check = TestDatabase.Deals();
        Assert.Equal(10, await check.Deals.Where(d => d.Id == deal.Id).Select(d => d.PiecesLeft).SingleAsync());
        Assert.Equal(0, await check.Shoppers.Where(c => c.OwnerId == owner).Select(c => c.Pieces).SingleAsync());
        Assert.Equal(HoldStatus.Released, (await check.Holds.SingleAsync(h => h.OrderId == order)).Status);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ExpiredKey));
        Assert.Equal(0, await Handler(check).SweepAsync(hold.ExpiresAt!.Value.AddHours(1))); // nothing left to expire
    }

    [Fact]
    public async Task ConfirmingBeforeTheDeadline_PinsTheHold_AndTheSweeperLeavesItAlone()
    {
        await TestDatabase.OpenAsync();
        var deal = await TestDatabase.AddDealAsync(piecesTotal: 10, maxPerShopper: 4);
        var order = await HoldAsync(deal, 2);

        await using var db = TestDatabase.Deals();
        var expires = (await db.Holds.SingleAsync(h => h.OrderId == order)).ExpiresAt!.Value;
        Assert.True(await Handler(db).ConfirmAsync(Guid.CreateVersion7(), new ConfirmPieces(order), default, expires.AddSeconds(-5)));
        Assert.False(await Handler(db).ConfirmAsync(Guid.CreateVersion7(), new ConfirmPieces(order), default, expires.AddSeconds(-4))); // twice changes nothing
        Assert.Equal(0, await Handler(db).SweepAsync(expires.AddHours(1)));

        await using var check = TestDatabase.Deals();
        Assert.Equal(8, await check.Deals.Where(d => d.Id == deal.Id).Select(d => d.PiecesLeft).SingleAsync());
        var held = await check.Outbox.Where(o => o.RoutingKey == Topology.HeldKey).OrderBy(o => o.Id).ToListAsync();
        Assert.Equal(2, held.Count); // the pending announcement, then the confirmed one
        Assert.DoesNotContain("expiresAt\":\"", held[1].Payload);
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ExpiredKey));
    }

    [Fact]
    public async Task ConfirmingAfterTheDeadline_ChangesNothing()
    {
        await TestDatabase.OpenAsync();
        var deal = await TestDatabase.AddDealAsync(piecesTotal: 5, maxPerShopper: 5);
        var order = await HoldAsync(deal, 2);

        await using var db = TestDatabase.Deals();
        var expires = (await db.Holds.SingleAsync(h => h.OrderId == order)).ExpiresAt!.Value;
        Assert.False(await Handler(db).ConfirmAsync(Guid.CreateVersion7(), new ConfirmPieces(order), default, expires.AddSeconds(1)));
        Assert.Equal(1, await Handler(db).SweepAsync(expires.AddSeconds(2)));
        Assert.False(await Handler(db).ConfirmAsync(Guid.CreateVersion7(), new ConfirmPieces(order), default, expires.AddSeconds(-30))); // already released

        await using var check = TestDatabase.Deals();
        Assert.Equal(5, await check.Deals.Where(d => d.Id == deal.Id).Select(d => d.PiecesLeft).SingleAsync());
    }

    [Fact]
    public async Task APlainReservation_IsNeverSwept()
    {
        await TestDatabase.OpenAsync();
        var deal = await TestDatabase.AddDealAsync(piecesTotal: 5, maxPerShopper: 5);
        await HoldAsync(deal, 2, hold: false);

        await using var db = TestDatabase.Deals();
        Assert.Equal(0, await Handler(db).SweepAsync(DateTimeOffset.UtcNow.AddYears(1)));
        Assert.Equal(3, await db.Deals.Where(d => d.Id == deal.Id).Select(d => d.PiecesLeft).SingleAsync());
    }

    [Fact]
    public async Task ConfirmAndSweepRacingAtTheDeadline_ExactlyOneWins_AndNothingIsLost()
    {
        for (var round = 0; round < 8; round++)
        {
            await TestDatabase.OpenAsync();
            var deal = await TestDatabase.AddDealAsync(piecesTotal: 6, maxPerShopper: 6);
            var order = await HoldAsync(deal, 3);
            DateTimeOffset expires;
            await using (var read = TestDatabase.Deals())
                expires = (await read.Holds.SingleAsync(h => h.OrderId == order)).ExpiresAt!.Value;

            var gate = new TaskCompletionSource();
            // Both act "at the deadline": a confirm that is still on time, and a sweep that sees the hold as expired.
            var confirm = Task.Run(async () =>
            {
                await gate.Task;
                await using var db = TestDatabase.Deals();
                return await Handler(db).ConfirmAsync(Guid.CreateVersion7(), new ConfirmPieces(order), default, expires.AddMilliseconds(-1));
            });
            var sweep = Task.Run(async () =>
            {
                await gate.Task;
                await using var db = TestDatabase.Deals();
                return await Handler(db).SweepAsync(expires.AddMilliseconds(1));
            });
            gate.SetResult();
            var pinned = await confirm;
            var swept = await sweep;

            await using var check = TestDatabase.Deals();
            var left = await check.Deals.Where(d => d.Id == deal.Id).Select(d => d.PiecesLeft).SingleAsync();
            var hold = await check.Holds.SingleAsync(h => h.OrderId == order);
            Assert.True(pinned ^ (swept == 1), $"round {round}: pinned={pinned}, swept={swept}");
            if (pinned)
            {
                Assert.Equal(3, left);
                Assert.Equal(HoldStatus.Held, hold.Status);
            }
            else
            {
                Assert.Equal(6, left);
                Assert.Equal(HoldStatus.Released, hold.Status);
            }
        }
    }

    [Fact]
    public async Task SeveralSweepersAtOnce_ExpireEachHoldExactlyOnce()
    {
        await TestDatabase.OpenAsync();
        var deal = await TestDatabase.AddDealAsync(piecesTotal: 100, maxPerShopper: 100);
        for (var i = 0; i < 20; i++)
            await HoldAsync(deal, 2);

        var gate = new TaskCompletionSource();
        var sweepers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await gate.Task;
            var total = 0;
            for (var pass = 0; pass < 3; pass++)
            {
                await using var db = TestDatabase.Deals();
                total += await Handler(db).SweepAsync(DateTimeOffset.UtcNow.AddHours(1), batch: 7);
            }
            return total;
        })).ToArray();
        gate.SetResult();
        var swept = (await Task.WhenAll(sweepers)).Sum();

        await using var check = TestDatabase.Deals();
        Assert.Equal(20, swept);
        Assert.Equal(100, await check.Deals.Where(d => d.Id == deal.Id).Select(d => d.PiecesLeft).SingleAsync());
        Assert.Equal(20, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ExpiredKey));
    }

    [Fact]
    public async Task ReceiptsWaitForTheConfirmation_AHoldWithAnExpiryIssuesNothing()
    {
        await TestDatabase.OpenAsync();
        var deal = await TestDatabase.AddDealAsync(5);
        var order = Guid.CreateVersion7();
        await using var db = TestDatabase.Receipts();
        var pending = new PiecesHeld(order, Guid.NewGuid(), deal.Id, 2, "SHP-OK", DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Equal(IssueOutcome.Pending, await new ReceiptsHandler(db).IssueAsync(Guid.CreateVersion7(), pending));
        Assert.Equal(IssueOutcome.Issued, await new ReceiptsHandler(db).IssueAsync(Guid.CreateVersion7(), pending with { ExpiresAt = null }));
        Assert.Equal(1, await db.Receipts.CountAsync(r => r.OrderId == order));
    }

    [Fact]
    public async Task OrdersExpireOnlyFromHeld_AndWaitForAHeldMarkThatIsStillOnItsWay()
    {
        await TestDatabase.OpenAsync();
        var deal = await TestDatabase.AddDealAsync(5);
        var held = await TestDatabase.AddOrderAsync(deal, "SHP-X-1", 2, OrderStatus.Held);
        var sold = await TestDatabase.AddOrderAsync(deal, "SHP-X-2", 2, OrderStatus.Sold);
        var requested = await TestDatabase.AddOrderAsync(deal, "SHP-X-3", 2, OrderStatus.Requested);
        OrdersHandler H(OrdersDbContext db) => new(db, Options.Create(new OrderOptions()));

        await using (var db = TestDatabase.Orders())
            Assert.Equal(MarkOutcome.Marked, await H(db).MarkExpiredAsync(Guid.CreateVersion7(), new PiecesExpired(held.Id, 2)));
        await using (var db = TestDatabase.Orders())
            Assert.Equal(MarkOutcome.Skipped, await H(db).MarkExpiredAsync(Guid.CreateVersion7(), new PiecesExpired(sold.Id, 2)));
        await using (var db = TestDatabase.Orders())
            await Assert.ThrowsAsync<OutOfOrderException>(() => H(db).MarkExpiredAsync(Guid.CreateVersion7(), new PiecesExpired(requested.Id, 2)));

        await using var check = TestDatabase.Orders();
        Assert.Equal(OrderStatus.Expired, (await check.Orders.SingleAsync(o => o.Id == held.Id)).Status);
        Assert.Equal(OrderStatus.Sold, (await check.Orders.SingleAsync(o => o.Id == sold.Id)).Status);
    }
}
