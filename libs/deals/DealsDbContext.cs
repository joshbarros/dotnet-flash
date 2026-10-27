using Flash.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Flash.Deals;

public class DealsDbContext(DbContextOptions<DealsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "deals";

    public DbSet<Deal> Deals => Set<Deal>();
    public DbSet<PieceHold> Holds => Set<PieceHold>();
    public DbSet<DealShopper> Shoppers => Set<DealShopper>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Deal>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(20);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Deals_Remaining", "\"PiecesLeft\" >= 0 AND \"PiecesLeft\" <= \"PiecesTotal\"");
                t.HasCheckConstraint("CK_Deals_Cap", "\"MaxPerShopper\" >= 1");
            });
            e.Property(x => x.MaxPerShopper).HasDefaultValue(2);
        });

        b.Entity<DealShopper>(e =>
        {
            e.ToTable("DealShoppers", t => t.HasCheckConstraint("CK_DealShoppers_Pieces", "\"Pieces\" >= 0"));
            e.HasKey(x => new { x.DealId, x.OwnerId });
            e.HasOne<Deal>().WithMany().HasForeignKey(x => x.DealId);
        });

        b.Entity<PieceHold>(e =>
        {
            e.ToTable("PieceHolds");
            e.HasIndex(x => new { x.Status, x.ExpiresAt });
            e.HasKey(x => x.OrderId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne<Deal>().WithMany().HasForeignKey(x => x.DealId);
        });
    }
}
