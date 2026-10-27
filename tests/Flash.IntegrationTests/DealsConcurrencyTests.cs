using System.Collections.Concurrent;
using Flash.Deals;
using Flash.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flash.IntegrationTests;

public class DealsConcurrencyTests
{
    [Fact]
    public async Task LastPiece_TwoConcurrentOrders_OneHeldOneShortNoHoldForTheLoser()
    {
        await TestDatabase.OpenAsync();
        var deal = await TestDatabase.AddDealAsync(piecesTotal: 1);

        var outcomes = new ConcurrentBag<(Guid Order, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var orderId = Guid.CreateVersion7();
            await using var db = TestDatabase.Deals();
            var outcome = await new DealsHandler(db).ReserveAsync(
                Guid.CreateVersion7(), new ReservePieces(orderId, Guid.NewGuid(), deal.Id, 1, $"SHP-{n}"));
            outcomes.Add((orderId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        await using var check = TestDatabase.Deals();
        var remaining = await check.Deals.Where(s => s.Id == deal.Id).Select(s => s.PiecesLeft).FirstAsync();
        var holds = await check.Holds.ToListAsync();
        var loser = outcomes.Single(o => o.Outcome == ReserveOutcome.Short).Order;

        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Held);
        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Short);
        Assert.Equal(0, remaining);
        Assert.Single(holds);
        Assert.DoesNotContain(holds, h => h.OrderId == loser);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.HeldKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ShortKey));
    }

    [Fact]
    public async Task Release_GivesTheUnitsBackExactlyOnce()
    {
        await TestDatabase.OpenAsync();
        var deal = await TestDatabase.AddDealAsync(piecesTotal: 4);
        var orderId = Guid.CreateVersion7();
        await using (var db = TestDatabase.Deals())
            await new DealsHandler(db).ReserveAsync(Guid.CreateVersion7(), new ReservePieces(orderId, Guid.NewGuid(), deal.Id, 3, "SHP-1"));

        // Two different commands (poison compensation and a refund) both ask for the same pieces back.
        await using var first = TestDatabase.Deals();
        await using var second = TestDatabase.Deals();
        var one = await new DealsHandler(first).ReleaseAsync(Guid.CreateVersion7(), new ReleasePieces(orderId, "poison"));
        var two = await new DealsHandler(second).ReleaseAsync(Guid.CreateVersion7(), new ReleasePieces(orderId, "refund"));

        await using var check = TestDatabase.Deals();
        Assert.True(one);
        Assert.False(two);
        Assert.Equal(4, await check.Deals.Where(s => s.Id == deal.Id).Select(s => s.PiecesLeft).FirstAsync());
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        await TestDatabase.OpenAsync();
        var deal = await TestDatabase.AddDealAsync(piecesTotal: 5);
        var messageId = Guid.CreateVersion7();
        var command = new ReservePieces(Guid.CreateVersion7(), Guid.NewGuid(), deal.Id, 2, "SHP-1");

        await using var a = TestDatabase.Deals();
        await using var b = TestDatabase.Deals();
        var first = await new DealsHandler(a).ReserveAsync(messageId, command);
        var second = await new DealsHandler(b).ReserveAsync(messageId, command);

        await using var check = TestDatabase.Deals();
        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(3, await check.Deals.Where(s => s.Id == deal.Id).Select(s => s.PiecesLeft).FirstAsync());
    }

    [Fact]
    public async Task TheDatabaseItself_RefusesANegativeRemainder()
    {
        await TestDatabase.OpenAsync();
        var deal = await TestDatabase.AddDealAsync(piecesTotal: 2);
        await using var db = TestDatabase.Deals();

        // A bug that skipped the WHERE clause would hit the check constraint instead of overselling.
        await Assert.ThrowsAnyAsync<Exception>(() => db.Deals.Where(s => s.Id == deal.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.PiecesLeft, x => x.PiecesLeft - 3)));
    }

    [Fact]
    public async Task Cap_ConcurrentOrdersByTheSameShopper_NeverPassTheCap()
    {
        await TestDatabase.OpenAsync();
        var deal = await TestDatabase.AddDealAsync(piecesTotal: 20, maxPerShopper: 2);
        var shopper = Guid.NewGuid();

        var outcomes = new ConcurrentBag<ReserveOutcome>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 6).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            await using var db = TestDatabase.Deals();
            outcomes.Add(await new DealsHandler(db).ReserveAsync(
                Guid.CreateVersion7(), new ReservePieces(Guid.CreateVersion7(), shopper, deal.Id, 1, $"SHP-{n}")));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        await using var check = TestDatabase.Deals();
        Assert.Equal(2, outcomes.Count(o => o == ReserveOutcome.Held));
        Assert.Equal(4, outcomes.Count(o => o == ReserveOutcome.CapReached));
        Assert.Equal(2, await check.Shoppers.Where(c => c.OwnerId == shopper).Select(c => c.Pieces).SingleAsync());
        Assert.Equal(18, await check.Deals.Where(s => s.Id == deal.Id).Select(s => s.PiecesLeft).FirstAsync());
        Assert.Equal(2, await check.Holds.CountAsync());
    }

    [Fact]
    public async Task Cap_AtWholeRequestLevel_RefusesTheOrderThatWouldCrossIt()
    {
        await TestDatabase.OpenAsync();
        var deal = await TestDatabase.AddDealAsync(piecesTotal: 10, maxPerShopper: 2);
        var shopper = Guid.NewGuid();
        await using var db = TestDatabase.Deals();
        var handler = new DealsHandler(db);

        Assert.Equal(ReserveOutcome.Held, await handler.ReserveAsync(Guid.CreateVersion7(), new ReservePieces(Guid.CreateVersion7(), shopper, deal.Id, 1, "SHP-1")));
        // 1 held + 2 asked = 3 > 2: nothing moves, neither the stock nor the shopper's count
        Assert.Equal(ReserveOutcome.CapReached, await handler.ReserveAsync(Guid.CreateVersion7(), new ReservePieces(Guid.CreateVersion7(), shopper, deal.Id, 2, "SHP-2")));
        // another shopper is unaffected
        Assert.Equal(ReserveOutcome.Held, await handler.ReserveAsync(Guid.CreateVersion7(), new ReservePieces(Guid.CreateVersion7(), Guid.NewGuid(), deal.Id, 2, "SHP-3")));

        await using var check = TestDatabase.Deals();
        Assert.Equal(1, await check.Shoppers.Where(c => c.OwnerId == shopper).Select(c => c.Pieces).SingleAsync());
        Assert.Equal(7, await check.Deals.Where(s => s.Id == deal.Id).Select(s => s.PiecesLeft).FirstAsync());
        var refusal = await check.Outbox.Where(o => o.RoutingKey == Topology.ShortKey).ToListAsync();
        Assert.Single(refusal);
        Assert.Contains("\"cap\"", refusal[0].Payload);
    }

    [Fact]
    public async Task Cap_StockShortAfterTheCapPassed_RollsTheCapBack()
    {
        await TestDatabase.OpenAsync();
        var deal = await TestDatabase.AddDealAsync(piecesTotal: 1, maxPerShopper: 2);
        var shopper = Guid.NewGuid();
        await using var db = TestDatabase.Deals();

        var outcome = await new DealsHandler(db).ReserveAsync(
            Guid.CreateVersion7(), new ReservePieces(Guid.CreateVersion7(), shopper, deal.Id, 2, "SHP-1"));

        await using var check = TestDatabase.Deals();
        Assert.Equal(ReserveOutcome.Short, outcome);
        Assert.False(await check.Shoppers.AnyAsync(c => c.OwnerId == shopper));
        Assert.Equal(1, await check.Deals.Where(s => s.Id == deal.Id).Select(s => s.PiecesLeft).FirstAsync());
    }

    [Fact]
    public async Task Cap_ReleaseGivesTheShopperTheirAllowanceBack()
    {
        await TestDatabase.OpenAsync();
        var deal = await TestDatabase.AddDealAsync(piecesTotal: 10, maxPerShopper: 2);
        var shopper = Guid.NewGuid();
        var first = Guid.CreateVersion7();
        await using var db = TestDatabase.Deals();
        var handler = new DealsHandler(db);
        await handler.ReserveAsync(Guid.CreateVersion7(), new ReservePieces(first, shopper, deal.Id, 2, "SHP-1"));
        Assert.Equal(ReserveOutcome.CapReached, await handler.ReserveAsync(Guid.CreateVersion7(), new ReservePieces(Guid.CreateVersion7(), shopper, deal.Id, 1, "SHP-2")));

        Assert.True(await handler.ReleaseAsync(Guid.CreateVersion7(), new ReleasePieces(first, "refund")));
        Assert.False(await handler.ReleaseAsync(Guid.CreateVersion7(), new ReleasePieces(first, "refund again")));

        Assert.Equal(ReserveOutcome.Held, await handler.ReserveAsync(Guid.CreateVersion7(), new ReservePieces(Guid.CreateVersion7(), shopper, deal.Id, 2, "SHP-3")));
        await using var check = TestDatabase.Deals();
        Assert.Equal(2, await check.Shoppers.Where(c => c.OwnerId == shopper).Select(c => c.Pieces).SingleAsync());
    }
}
