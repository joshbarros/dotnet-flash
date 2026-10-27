using Flash.Shared;
using Flash.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Flash.Receipts;

public enum ReceiptStatus { Issued, Rejected }

public class Receipt
{
    public Guid OrderId { get; set; }
    public Guid OwnerId { get; set; }
    public string? Number { get; set; }
    public ReceiptStatus Status { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class ReceiptsDbContext(DbContextOptions<ReceiptsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "receipts";

    public DbSet<Receipt> Receipts => Set<Receipt>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Receipt>(e =>
        {
            e.ToTable("Receipts");
            e.HasKey(x => x.OrderId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });
    }
}

public enum IssueOutcome { Issued, Rejected, Duplicate, Pending }

/// <summary>Issues the receipt once the pieces are held. A poison reference is committed as rejected.</summary>
public class ReceiptsHandler(ReceiptsDbContext db) : IMessageHandler
{
    public const string Name = "receipts";

    public async Task HandleAsync(Delivery delivery, CancellationToken ct)
    {
        if (delivery.RoutingKey != Topology.HeldKey)
            throw new InvalidOperationException($"Receipts does not handle '{delivery.RoutingKey}'.");

        var outcome = await IssueAsync(delivery.MessageId, Json.Parse<PiecesHeld>(delivery.Body), ct);
        if (outcome == IssueOutcome.Rejected)
            throw new PoisonMessageException("Reference carries the poison token; rejection is committed.");
    }

    public async Task<IssueOutcome> IssueAsync(Guid messageId, PiecesHeld held, CancellationToken ct = default)
    {
        if (held.ExpiresAt is not null)
            return IssueOutcome.Pending; // the hold still waits for the shopper; the confirmed copy of this event issues the receipt

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return IssueOutcome.Duplicate;
        }

        IssueOutcome outcome;
        if (PoisonRule.IsPoison(held.Reference))
        {
            const string reason = "Reference contains the poison token.";
            db.Receipts.Add(new Receipt
            {
                OrderId = held.OrderId,
                OwnerId = held.OwnerId,
                Status = ReceiptStatus.Rejected,
                Reason = reason,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("receipts", Topology.ReleaseKey, held.OrderId, new ReleasePieces(held.OrderId, "poison"));
            db.Enqueue("receipts", Topology.RejectedKey, held.OrderId, new ReceiptRejected(held.OrderId, reason));
            outcome = IssueOutcome.Rejected;
        }
        else
        {
            var number = $"FL-{held.OrderId.ToString("N")[^12..].ToUpperInvariant()}";
            db.Receipts.Add(new Receipt
            {
                OrderId = held.OrderId,
                OwnerId = held.OwnerId,
                Number = number,
                Status = ReceiptStatus.Issued,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("receipts", Topology.IssuedKey, held.OrderId, new ReceiptIssued(held.OrderId, number));
            outcome = IssueOutcome.Issued;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }
}
