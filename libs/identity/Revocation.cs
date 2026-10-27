using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Flash.Identity;

/// <summary>
/// Access tokens are short-lived, but a password change, a reset or "sign out everywhere" must end them sooner.
/// Each token carries the user's session version; ending every session bumps the version, which invalidates every token
/// sold before it. The version is cached briefly so this is not a database read per request.
/// </summary>
public sealed class SessionVersionChecker(IdentityModuleDbContext db, IMemoryCache cache, IOptions<JwtOptions> options)
{
    public async Task<bool> IsCurrentAsync(Guid userId, string? tokenVersion, CancellationToken ct)
    {
        if (!int.TryParse(tokenVersion, out var presented))
            return false;
        var seconds = options.Value.RevocationCacheSeconds;
        var key = $"session-version:{userId}";
        if (seconds > 0 && cache.TryGetValue(key, out int cached))
            return cached == presented;

        var current = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => (int?)u.SessionVersion).FirstOrDefaultAsync(ct);
        if (seconds > 0 && current is { } known)
            cache.Set(key, known, TimeSpan.FromSeconds(seconds));
        return current == presented;
    }
}
