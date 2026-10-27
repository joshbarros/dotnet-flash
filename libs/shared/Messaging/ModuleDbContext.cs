using Microsoft.EntityFrameworkCore;

namespace Flash.Shared.Messaging;

public class OutboxMessage
{
    public long Id { get; set; }
    public Guid MessageId { get; set; }
    public string Module { get; set; } = "";
    public string RoutingKey { get; set; } = "";
    public string Key { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}

public class InboxMessage
{
    public string Consumer { get; set; } = "";
    public Guid MessageId { get; set; }
    public DateTimeOffset ClaimedAt { get; set; }
}

/// <summary>
/// Base for a module's own DbContext: its own Postgres schema, its own outbox and its own inbox.
/// The module writes its rows and its outbox rows in one local transaction, so a module never needs another module's tables.
/// </summary>
public abstract class ModuleDbContext(DbContextOptions options, string schema) : DbContext(options)
{
    public string Schema { get; } = System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_]{1,32}$")
        ? schema
        : throw new ArgumentException("A schema name is lowercase letters and underscores.", nameof(schema));
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("OutboxMessages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.SentAt).HasFilter("\"SentAt\" IS NULL");
        });

        b.Entity<InboxMessage>(e =>
        {
            e.ToTable("InboxMessages");
            e.HasKey(x => new { x.Consumer, x.MessageId });
        });
    }

    /// <summary>Adds an outbox row to the current piece of work. Nothing is published here.</summary>
    public void Enqueue<T>(string module, string routingKey, Guid key, T payload) =>
        Outbox.Add(new OutboxMessage
        {
            MessageId = Guid.CreateVersion7(),
            Module = module,
            RoutingKey = routingKey,
            Key = key.ToString(),
            Payload = Json.Serialize(payload),
            CreatedAt = DateTimeOffset.UtcNow,
        });

    /// <summary>
    /// Claims (consumer, message id). False means this consumer already handled the message.
    /// Call it inside the transaction that does the work so the claim and the effect commit together.
    /// </summary>
    public async Task<bool> ClaimAsync(string consumer, Guid messageId, CancellationToken ct)
    {
        // The schema is validated against ^[a-z_]{1,32}$ in the constructor, so it cannot carry SQL. Values are parameters.
#pragma warning disable EF1002
        var rows = await Database.ExecuteSqlRawAsync(
            $"INSERT INTO \"{Schema}\".\"InboxMessages\" (\"Consumer\", \"MessageId\", \"ClaimedAt\") VALUES ({{0}}, {{1}}, {{2}}) ON CONFLICT DO NOTHING",
            [consumer, messageId, DateTimeOffset.UtcNow], ct);
#pragma warning restore EF1002
        return rows == 1;
    }
}
