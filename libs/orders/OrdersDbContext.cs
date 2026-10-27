using Flash.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Flash.Orders;

public class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "orders";

    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Order>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Reference).HasMaxLength(OrderRules.MaxReferenceLength);
            e.HasIndex(x => new { x.DealId, x.Reference }).IsUnique();
            e.HasIndex(x => x.OwnerId);
        });
    }
}
