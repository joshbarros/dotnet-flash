using Flash.Shared;
using Flash.Shared.Contracts;
using Flash.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Flash.Deals;

public static class DealsModule
{
    public static IServiceCollection AddDealsModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Flash") ?? throw new InvalidOperationException("Connection string 'Flash' is not configured.");
        services.AddDbContext<DealsDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", DealsDbContext.SchemaName)));
        services.Configure<DealOptions>(config.GetSection("Deals"));
        services.AddScoped<DealsHandler>();
        services.AddHostedService<HoldSweeper>();
        services.AddScoped<IDealCatalog, DealCatalog>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<DealsDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<DealsDbContext>>("deals-db", tags: ["ready"]);

        messaging.AddOutbox<DealsDbContext>()
            .AddConsumer<DealsHandler>(Topology.DealsQueue, [Topology.ReserveKey, Topology.ReleaseKey, Topology.ConfirmKey], prefetch: 1);
        return services;
    }
}
