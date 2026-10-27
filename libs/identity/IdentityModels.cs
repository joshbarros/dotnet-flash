using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Flash.Identity;

public class ApplicationUser : IdentityUser<Guid>
{
    /// <summary>Counts how many times every session of this user was ended at once. Access tokens carry the value they were sold under.</summary>
    public int SessionVersion { get; set; }
}

/// <summary>
/// A refresh token as stored: only its SHA-256 hash, never the token. Tokens rotate on every use and belong to a family;
/// presenting a token that was already used revokes the whole family.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid FamilyId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? ReplacedByHash { get; set; }
}

/// <summary>One counter per client per minute window, shared by every API instance through Postgres.</summary>
public class RateLimitHit
{
    public string Key { get; set; } = "";
    public DateTimeOffset WindowStart { get; set; }
    public int Count { get; set; }
}

public class IdentityModuleDbContext(DbContextOptions<IdentityModuleDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options), IDataProtectionKeyContext
{
    public const string Schema = "identity";

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<RateLimitHit> RateLimitHits => Set<RateLimitHit>();

    /// <summary>The keys that protect confirmation and reset tokens, shared by every instance and kept across restarts.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.HasDefaultSchema(Schema);
        b.Entity<RateLimitHit>(e =>
        {
            e.HasKey(x => new { x.Key, x.WindowStart });
            e.Property(x => x.Key).HasMaxLength(128);
        });
        b.Entity<RefreshToken>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.FamilyId);
            e.Property(x => x.TokenHash).HasMaxLength(64);
        });
    }
}
