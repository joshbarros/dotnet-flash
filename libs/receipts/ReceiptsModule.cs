using Flash.Shared;
using Flash.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Flash.Receipts;

public static class ReceiptsModule
{
    public static IServiceCollection AddReceiptsModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Flash") ?? throw new InvalidOperationException("Connection string 'Flash' is not configured.");
        services.AddDbContext<ReceiptsDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", ReceiptsDbContext.SchemaName)));
        services.AddScoped<ReceiptsHandler>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<ReceiptsDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<ReceiptsDbContext>>("receipts-db", tags: ["ready"]);

        messaging.AddOutbox<ReceiptsDbContext>()
            .AddConsumer<ReceiptsHandler>(Topology.ReceiptsQueue, [Topology.HeldKey], prefetch: 4);
        return services;
    }
}
