using Flash.Shared;
using Flash.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Flash.Orders;

public static class OrdersModule
{
    public static IServiceCollection AddOrdersModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Flash") ?? throw new InvalidOperationException("Connection string 'Flash' is not configured.");
        services.AddDbContext<OrdersDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", OrdersDbContext.SchemaName)));
        services.Configure<OrderOptions>(config.GetSection("Orders"));
        services.AddScoped<OrderService>();
        services.AddScoped<OrdersHandler>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<OrdersDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<OrdersDbContext>>("orders-db", tags: ["ready"]);

        messaging.AddOutbox<OrdersDbContext>()
            .AddConsumer<OrdersHandler>(Topology.OrdersQueue,
                [Topology.HeldKey, Topology.ShortKey, Topology.ExpiredKey, Topology.IssuedKey, Topology.RejectedKey, Topology.ReleasedKey], prefetch: 4);
        return services;
    }
}
