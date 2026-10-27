using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RabbitMQ.Client;

namespace Flash.Shared.Messaging;

/// <summary>The consumer queues of this process; each has a "<queue>.dlq" next to it.</summary>
public sealed record DeadLetterQueues(IReadOnlyList<string> Names);

public sealed record DeadLetter(string RoutingKey, string Body, int Deliveries);

public sealed record DeadLetterQueue(string Queue, uint Count, IReadOnlyList<DeadLetter> Messages);

public sealed record ReplayResult(string Queue, int Replayed);

/// <summary>Looks inside the dead-letter queues and puts messages back on the work exchange.</summary>
public sealed class DeadLetterService(RabbitConnection rabbit, DeadLetterQueues queues)
{
    public IReadOnlyList<string> Names => queues.Names;

    /// <summary>The routing key a dead-lettered message was first published with: the relay stores it in the Type property.</summary>
    public static string? RoutingKeyOf(IReadOnlyBasicProperties properties) =>
        string.IsNullOrWhiteSpace(properties.Type) ? null : properties.Type;

    public async Task<IReadOnlyList<DeadLetterQueue>> ListAsync(int peek, CancellationToken ct)
    {
        await using var channel = await OpenAsync(ct);
        var result = new List<DeadLetterQueue>();
        foreach (var queue in queues.Names)
        {
            var dlq = $"{queue}.dlq";
            var declared = await channel.QueueDeclarePassiveAsync(dlq, ct);
            var messages = new List<DeadLetter>();
            var tags = new List<ulong>();
            while (messages.Count < peek && await channel.BasicGetAsync(dlq, autoAck: false, ct) is { } got)
            {
                tags.Add(got.DeliveryTag);
                messages.Add(new DeadLetter(RoutingKeyOf(got.BasicProperties) ?? "?", Encoding.UTF8.GetString(got.Body.Span), Deliveries(got.BasicProperties)));
            }
            foreach (var tag in tags)
                await channel.BasicNackAsync(tag, multiple: false, requeue: true, ct); // a look, not a take
            result.Add(new DeadLetterQueue(queue, declared.MessageCount, messages));
        }
        return result;
    }

    /// <summary>
    /// Takes up to <paramref name="max"/> messages out of a queue's dead-letter queue and publishes each again.
    /// A message that fails for the same reason is dead-lettered again after its retries; replay is for fixed causes.
    /// </summary>
    public async Task<ReplayResult> ReplayAsync(string queue, int max, CancellationToken ct)
    {
        if (!queues.Names.Contains(queue))
            throw new DomainException(404, $"There is no queue '{queue}'.");
        await using var channel = await OpenAsync(ct);
        var replayed = 0;
        while (replayed < max && await channel.BasicGetAsync($"{queue}.dlq", autoAck: false, ct) is { } got)
        {
            var key = RoutingKeyOf(got.BasicProperties);
            if (key is null)
            {
                await channel.BasicNackAsync(got.DeliveryTag, multiple: false, requeue: true, ct);
                break;
            }
            var properties = new BasicProperties
            {
                MessageId = got.BasicProperties.MessageId,
                Type = key,
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent,
            };
            await channel.BasicPublishAsync(Topology.Exchange, key, mandatory: false, properties, got.Body, ct);
            await channel.BasicAckAsync(got.DeliveryTag, multiple: false, ct);
            replayed++;
        }
        return new ReplayResult(queue, replayed);
    }

    private static int Deliveries(IReadOnlyBasicProperties properties) =>
        properties.Headers is { } h && h.TryGetValue("x-death", out var death) && death is List<object> list ? list.Count : 1;

    private async Task<IChannel> OpenAsync(CancellationToken ct)
    {
        var connection = await rabbit.GetAsync(ct);
        return await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
    }
}

[ApiController]
[Route("api/admin/dead-letters")]
[Authorize(Policy = Auth.Policies.ViewAll)]
public class DeadLettersController(DeadLetterService? service = null) : ControllerBase
{
    private DeadLetterService Service => service ?? throw new DomainException(503, "Messaging is switched off in this process.");

    /// <summary>What is waiting in each dead-letter queue, with the first few messages. Staff only.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<DeadLetterQueue>> List([FromQuery] int peek = 10, CancellationToken ct = default) =>
        await Service.ListAsync(Math.Clamp(peek, 0, 50), ct);

    /// <summary>Publishes up to <c>max</c> dead-lettered messages of one queue again. Staff only.</summary>
    [HttpPost("{queue}/replay")]
    [Authorize(Policy = Auth.Policies.ManageCatalog)]
    public async Task<ReplayResult> Replay(string queue, [FromQuery] int max = 100, CancellationToken ct = default) =>
        await Service.ReplayAsync(queue, Math.Clamp(max, 1, 1000), ct);
}
