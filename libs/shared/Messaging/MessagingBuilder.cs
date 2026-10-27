using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Flash.Shared.Messaging;

/// <summary>Modules register their outbox and their consumers here; the host starts them once, and only if messaging is enabled.</summary>
public sealed class MessagingBuilder(IServiceCollection services, bool enabled)
{
    private readonly List<ConsumerRegistration> _consumers = [];
    private readonly List<Type> _outboxes = [];

    public bool Enabled => enabled;
    public IReadOnlyList<ConsumerRegistration> Consumers => _consumers;
    public IReadOnlyList<Type> Outboxes => _outboxes;

    public MessagingBuilder AddOutbox<TContext>() where TContext : ModuleDbContext
    {
        _outboxes.Add(typeof(TContext));
        return this;
    }

    public MessagingBuilder AddConsumer<THandler>(string queue, string[] routingKeys, ushort prefetch) where THandler : class, IMessageHandler
    {
        _consumers.Add(new ConsumerRegistration(typeof(THandler), queue, routingKeys, prefetch));
        return this;
    }

    public void Start()
    {
        if (!enabled)
            return;

        services.AddSingleton(new OutboxSources(_outboxes));
        services.AddSingleton(new DeadLetterQueues(_consumers.Select(c => c.Queue).ToList()));
        services.AddSingleton<DeadLetterService>();
        services.AddHostedService<OutboxRelay>();
        foreach (var registration in _consumers)
        {
            services.AddSingleton<IHostedService>(sp => new ModuleConsumer(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<RabbitConnection>(),
                registration,
                sp.GetRequiredService<ILogger<ModuleConsumer>>()));
        }
    }
}

public static class MessagingExtensions
{
    public static MessagingBuilder AddMessaging(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<RabbitOptions>(config.GetSection("RabbitMq"));
        services.Configure<KafkaOptions>(config.GetSection("Kafka"));
        services.AddSingleton<RabbitConnection>();
        services.AddSingleton<KafkaLog>();
        services.AddControllers().AddApplicationPart(typeof(MessagingExtensions).Assembly);
        return new MessagingBuilder(services, config.GetValue("Messaging:Enabled", true));
    }
}
