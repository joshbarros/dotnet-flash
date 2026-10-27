using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Flash.Shared;

/// <summary>Something a module needs done once at startup, before traffic: migrate its schema, seed its roles. Lower order runs first.</summary>
public interface IModuleMigrator
{
    int Order { get; }
    Task MigrateAsync(IServiceProvider services, CancellationToken ct);
}

public sealed class EfMigrator<TContext>(int order = 10) : IModuleMigrator where TContext : DbContext
{
    public int Order => order;

    public async Task MigrateAsync(IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TContext>().Database.MigrateAsync(ct);
    }
}

/// <summary>Ready means this module's database can be reached.</summary>
public sealed class DbReadinessCheck<TContext>(TContext db) : IHealthCheck where TContext : DbContext
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await db.Database.CanConnectAsync(ct) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy($"{typeof(TContext).Name} cannot connect");
}
