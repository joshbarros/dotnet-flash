using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Flash.Identity;

/// <summary>
/// A fixed-window limit on /api/auth/*, counted in Postgres so every API instance shares one count per client.
/// One upsert per request; windows older than an hour are swept now and then.
/// </summary>
public sealed class AuthRateLimitMiddleware(RequestDelegate next, IConfiguration config, TimeProvider clock)
{
    private readonly int _perMinute = config.GetValue("RateLimit:AuthPerMinute", 20);
    private long _lastSweep;

    public async Task InvokeAsync(HttpContext context, IdentityModuleDbContext db)
    {
        if (!context.Request.Path.StartsWithSegments("/api/auth"))
        {
            await next(context);
            return;
        }

        var now = clock.GetUtcNow();
        var window = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
        var client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        var counts = await db.Database.SqlQuery<int>($"""
            INSERT INTO identity."RateLimitHits" ("Key", "WindowStart", "Count") VALUES ({client}, {window}, 1)
            ON CONFLICT ("Key", "WindowStart") DO UPDATE SET "Count" = identity."RateLimitHits"."Count" + 1
            RETURNING "Count" AS "Value"
            """).ToListAsync(context.RequestAborted);
        var count = counts[0];

        if (count > _perMinute)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = (60 - now.Second).ToString();
            return;
        }

        var tick = now.UtcTicks / TimeSpan.TicksPerMinute;
        var last = Interlocked.Read(ref _lastSweep);
        if (tick - last >= 30 && Interlocked.CompareExchange(ref _lastSweep, tick, last) == last)
        {
            var cutoff = now.AddHours(-1);
            await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM identity."RateLimitHits" WHERE "WindowStart" < {cutoff}""", context.RequestAborted);
        }

        await next(context);
    }
}

public static class AuthRateLimitExtensions
{
    public static IApplicationBuilder UseAuthRateLimit(this IApplicationBuilder app) => app.UseMiddleware<AuthRateLimitMiddleware>();
}
