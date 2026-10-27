using Flash.Orders;
using Flash.Deals;
using Flash.Receipts;
using Flash.Shared.Auth;
using Flash.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Flash.IntegrationTests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("FLASH_TEST_DB")
        ?? "Host=localhost;Port=5484;Database=flash_test;Username=flash;Password=flash";

    private static DbContextOptions<T> Options<T>(string schema) where T : DbContext =>
        new DbContextOptionsBuilder<T>()
            .UseNpgsql(ConnectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory", schema))
            .Options;

    public static OrdersDbContext Orders() => new(Options<OrdersDbContext>(OrdersDbContext.SchemaName));
    public static DealsDbContext Deals() => new(Options<DealsDbContext>(DealsDbContext.SchemaName));
    public static ReceiptsDbContext Receipts() => new(Options<ReceiptsDbContext>(ReceiptsDbContext.SchemaName));

    /// <summary>Migrates the business modules and empties their tables. The identity schema is left alone: the API tests share it.</summary>
    public static async Task OpenAsync()
    {
        await using var orders = Orders();
        await using var pieces = Deals();
        await using var receipts = Receipts();
        await orders.Database.MigrateAsync();
        await pieces.Database.MigrateAsync();
        await receipts.Database.MigrateAsync();
        await orders.Database.ExecuteSqlRawAsync(
            "TRUNCATE orders.\"InboxMessages\", orders.\"OutboxMessages\", orders.\"Orders\", " +
            "deals.\"InboxMessages\", deals.\"OutboxMessages\", deals.\"PieceHolds\", deals.\"DealShoppers\", deals.\"Deals\", " +
            "receipts.\"InboxMessages\", receipts.\"OutboxMessages\", receipts.\"Receipts\" RESTART IDENTITY CASCADE;");
    }

    public static async Task<Deal> AddDealAsync(int piecesTotal, long priceCents = 5_000, int? maxPerShopper = null)
    {
        await using var db = Deals();
        var deal = new Deal
        {
            Id = Guid.CreateVersion7(),
            Code = $"T-{Guid.NewGuid():N}"[..14],
            Name = "Test Deal",
            Aisle = "Home",
            Brand = "Acme Goods",
            SaleDate = DateTimeOffset.UtcNow.AddDays(7),
            PriceCents = priceCents,
            PiecesTotal = piecesTotal,
            PiecesLeft = piecesTotal,
            MaxPerShopper = maxPerShopper ?? piecesTotal,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Deals.Add(deal);
        await db.SaveChangesAsync();
        return deal;
    }

    public static async Task<Order> AddOrderAsync(Deal deal, string reference, int pieces, OrderStatus status, Guid? ownerId = null)
    {
        await using var db = Orders();
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId ?? Guid.CreateVersion7(),
            DealId = deal.Id,
            DealCode = deal.Code,
            Reference = reference,
            Pieces = pieces,
            TotalCents = OrderRules.TotalCents(deal.PriceCents, pieces),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }
}

public sealed class TestUser(Guid? id, bool staff = false) : ICurrentUser
{
    public bool IsAuthenticated => id is not null;
    public Guid? Id => id;
    public bool IsStaff => staff;
}

public sealed class FixedCatalog(params Deal[] deals) : IDealCatalog
{
    public Task<DealInfo?> FindAsync(string code, CancellationToken ct) =>
        Task.FromResult(deals.Where(s => s.Code == code).Select(s => new DealInfo(s.Id, s.Code, s.PriceCents)).FirstOrDefault());
}
