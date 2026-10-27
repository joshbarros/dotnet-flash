using Flash.Orders;
using Flash.Receipts;
using Flash.Shared;
using Flash.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Flash.IntegrationTests;

public class SagaTests
{
    private static OrdersHandler Handler(OrdersDbContext db) => new(db, Options.Create(new OrderOptions()));

    [Fact]
    public async Task PoisonReference_IsCommittedRejected_ThenCompensatingCommandIsQueued()
    {
        await TestDatabase.OpenAsync();
        var deal = await TestDatabase.AddDealAsync(5);
        var order = await TestDatabase.AddOrderAsync(deal, "SHP-POISON-1", 2, OrderStatus.Held);
        var held = new PiecesHeld(order.Id, order.OwnerId, deal.Id, 2, order.Reference);

        await using var docs = TestDatabase.Receipts();
        var ex = await Assert.ThrowsAsync<PoisonMessageException>(() =>
            new ReceiptsHandler(docs).HandleAsync(new Delivery(Guid.CreateVersion7(), Topology.HeldKey, Json.Serialize(held)), default));
        Assert.Contains("poison", ex.Message, StringComparison.OrdinalIgnoreCase);

        await using var check = TestDatabase.Receipts();
        var doc = await check.Receipts.SingleAsync(d => d.OrderId == order.Id);
        Assert.Equal(ReceiptStatus.Rejected, doc.Status); // committed before the dead-letter
        Assert.Equal(order.OwnerId, doc.OwnerId);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ReleaseKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.RejectedKey));
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.IssuedKey));
    }

    [Fact]
    public async Task Refund_ThenFinalMark_LeavesOrderRefundedAndReceiptIssued()
    {
        await TestDatabase.OpenAsync();
        var deal = await TestDatabase.AddDealAsync(5);
        var owner = Guid.CreateVersion7();
        var order = await TestDatabase.AddOrderAsync(deal, "SHP-1", 2, OrderStatus.Held, owner);

        // The receipt is issued; the final mark has not landed yet.
        await using (var docs = TestDatabase.Receipts())
            await new ReceiptsHandler(docs).IssueAsync(Guid.CreateVersion7(), new PiecesHeld(order.Id, owner, deal.Id, 2, order.Reference));

        // The owner reverses first.
        await using (var orders = TestDatabase.Orders())
            await new OrderService(orders, new FixedCatalog(deal), new TestUser(owner)).RefundAsync(order.Id, default);

        await using var late = TestDatabase.Orders();
        var mark = await Handler(late).MarkSoldAsync(Guid.CreateVersion7(), new ReceiptIssued(order.Id, "FL-TEST"));

        await using var check = TestDatabase.Orders();
        await using var checkDocs = TestDatabase.Receipts();
        Assert.Equal(MarkOutcome.Skipped, mark);
        Assert.Equal(OrderStatus.Refunded, (await check.Orders.SingleAsync()).Status);
        Assert.Equal(ReceiptStatus.Issued, (await checkDocs.Receipts.SingleAsync()).Status);
    }

    [Fact]
    public async Task FinalMark_RunsOnlyFromHeld()
    {
        await TestDatabase.OpenAsync();
        var deal = await TestDatabase.AddDealAsync(5);
        var held = await TestDatabase.AddOrderAsync(deal, "SHP-A", 1, OrderStatus.Held);
        var rejected = await TestDatabase.AddOrderAsync(deal, "SHP-B", 1, OrderStatus.Rejected);
        var requested = await TestDatabase.AddOrderAsync(deal, "SHP-C", 1, OrderStatus.Requested);

        await using (var a = TestDatabase.Orders())
            Assert.Equal(MarkOutcome.Marked, await Handler(a).MarkSoldAsync(Guid.CreateVersion7(), new ReceiptIssued(held.Id, "FL-1")));
        await using (var b = TestDatabase.Orders())
            Assert.Equal(MarkOutcome.Skipped, await Handler(b).MarkSoldAsync(Guid.CreateVersion7(), new ReceiptIssued(rejected.Id, "FL-2")));
        // Not Held yet: the message is retried later instead of being dropped.
        await using (var c = TestDatabase.Orders())
            await Assert.ThrowsAsync<OutOfOrderException>(() => Handler(c).MarkSoldAsync(Guid.CreateVersion7(), new ReceiptIssued(requested.Id, "FL-3")));

        await using var check = TestDatabase.Orders();
        var statuses = (await check.Orders.ToListAsync()).ToDictionary(b => b.Reference, b => b.Status);
        Assert.Equal(OrderStatus.Sold, statuses["SHP-A"]);
        Assert.Equal(OrderStatus.Rejected, statuses["SHP-B"]);
        Assert.Equal(OrderStatus.Requested, statuses["SHP-C"]);
    }

    [Fact]
    public async Task Refund_OfARejectedOrder_Conflicts()
    {
        await TestDatabase.OpenAsync();
        var deal = await TestDatabase.AddDealAsync(5);
        var owner = Guid.CreateVersion7();
        var order = await TestDatabase.AddOrderAsync(deal, "SHP-1", 1, OrderStatus.Rejected, owner);

        await using var db = TestDatabase.Orders();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new OrderService(db, new FixedCatalog(deal), new TestUser(owner)).RefundAsync(order.Id, default));
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task Refund_ByAnotherCustomer_IsNotFound_AndChangesNothing()
    {
        await TestDatabase.OpenAsync();
        var deal = await TestDatabase.AddDealAsync(5);
        var order = await TestDatabase.AddOrderAsync(deal, "SHP-1", 1, OrderStatus.Held);

        await using var db = TestDatabase.Orders();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new OrderService(db, new FixedCatalog(deal), new TestUser(Guid.NewGuid())).RefundAsync(order.Id, default));

        Assert.Equal(404, ex.Status);
        await using var check = TestDatabase.Orders();
        Assert.Equal(OrderStatus.Held, (await check.Orders.SingleAsync()).Status);
        Assert.Equal(0, await check.Outbox.CountAsync());
    }

    [Fact]
    public async Task Staff_CanRefundAnyonesOrder()
    {
        await TestDatabase.OpenAsync();
        var deal = await TestDatabase.AddDealAsync(5);
        var order = await TestDatabase.AddOrderAsync(deal, "SHP-1", 1, OrderStatus.Held);

        await using var db = TestDatabase.Orders();
        var refunded = await new OrderService(db, new FixedCatalog(deal), new TestUser(Guid.NewGuid(), staff: true)).RefundAsync(order.Id, default);
        Assert.Equal(OrderStatus.Refunded, refunded.Status);
    }

    [Fact]
    public async Task Create_RecordsTheCallerAsTheOwner_AndQueuesTheReservation()
    {
        await TestDatabase.OpenAsync();
        var deal = await TestDatabase.AddDealAsync(5);
        var owner = Guid.CreateVersion7();

        await using var db = TestDatabase.Orders();
        var order = await new OrderService(db, new FixedCatalog(deal), new TestUser(owner))
            .CreateAsync(new CreateOrderRequest(deal.Code, "SHP-1", 2), default);

        await using var check = TestDatabase.Orders();
        Assert.Equal(owner, (await check.Orders.SingleAsync()).OwnerId);
        var message = await check.Outbox.SingleAsync();
        Assert.Equal(Topology.ReserveKey, message.RoutingKey);
        Assert.Equal(owner, Json.Parse<ReservePieces>(message.Payload).OwnerId);
        Assert.Equal(OrderStatus.Requested, order.Status);
    }

    [Fact]
    public async Task Create_WithoutASignedInUser_IsRefused()
    {
        await TestDatabase.OpenAsync();
        var deal = await TestDatabase.AddDealAsync(5);
        await using var db = TestDatabase.Orders();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new OrderService(db, new FixedCatalog(deal), new TestUser(null)).CreateAsync(new CreateOrderRequest(deal.Code, "SHP-1", 1), default));
        Assert.Equal(401, ex.Status);
    }

    [Fact]
    public async Task Create_ForAnUnknownDeal_IsNotFound()
    {
        await TestDatabase.OpenAsync();
        await using var db = TestDatabase.Orders();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new OrderService(db, new FixedCatalog(), new TestUser(Guid.NewGuid())).CreateAsync(new CreateOrderRequest("NOPE", "SHP-1", 1), default));
        Assert.Equal(404, ex.Status);
    }
}
