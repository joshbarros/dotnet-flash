using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Flash.Shared.Messaging;

public record ConsumerRegistration(Type HandlerType, string Queue, string[] RoutingKeys, ushort Prefetch);

/// <summary>
/// One queue, one handler. A transient failure goes to the retry queue (2s TTL, back to the main queue) up to MaxAttempts,
/// then to the dead-letter queue. A poison message goes straight to the dead-letter queue.
/// </summary>
public sealed class ModuleConsumer(
    IServiceScopeFactory scopes,
    RabbitConnection rabbit,
    ConsumerRegistration spec,
    ILogger<ModuleConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var connection = await rabbit.GetAsync(ct);
        var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await Topology.DeclareAsync(channel, spec.Queue, spec.RoutingKeys, ct);
        await channel.BasicQosAsync(0, spec.Prefetch, false, ct);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, ea) => OnDeliveryAsync(channel, ea, ct);
        await channel.BasicConsumeAsync(spec.Queue, autoAck: false, consumer, ct);
        logger.LogInformation("Consuming {Queue} (prefetch {Prefetch})", spec.Queue, spec.Prefetch);

        await Task.Delay(Timeout.Infinite, ct);
    }

    private async Task OnDeliveryAsync(IChannel channel, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var attempt = ea.BasicProperties.Headers?.TryGetValue("x-attempt", out var raw) == true && raw is int n ? n : 0;
        try
        {
            var messageId = Guid.Parse(ea.BasicProperties.MessageId!);
            var body = Encoding.UTF8.GetString(ea.Body.Span);
            using var scope = scopes.CreateScope();
            var handler = (IMessageHandler)scope.ServiceProvider.GetRequiredService(spec.HandlerType);
            await handler.HandleAsync(new Delivery(messageId, ea.RoutingKey, body), ct);
            await channel.BasicAckAsync(ea.DeliveryTag, false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ConsumerRetryPolicy.Decide(attempt, ex) == FailureAction.DeadLetter)
            {
                if (ex is PoisonMessageException)
                    logger.LogWarning("Poison message on {Queue}, dead-lettering: {Message}", spec.Queue, ex.Message);
                else
                    logger.LogError(ex, "Giving up on a message from {Queue} after {Attempts} attempts; dead-lettering.", spec.Queue, attempt + 1);
                await channel.BasicNackAsync(ea.DeliveryTag, false, requeue: false, ct);
                return;
            }

            logger.LogWarning("Handler for {Queue} failed (attempt {Attempt}): {Message}", spec.Queue, attempt + 1, ex.Message);
            var properties = new BasicProperties
            {
                MessageId = ea.BasicProperties.MessageId,
                Type = ea.BasicProperties.Type,
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent,
                Headers = new Dictionary<string, object?> { ["x-attempt"] = attempt + 1 },
            };
            await channel.BasicPublishAsync("", $"{spec.Queue}.retry", mandatory: false, properties, ea.Body, ct);
            await channel.BasicAckAsync(ea.DeliveryTag, false, ct);
        }
    }
}
