using RabbitMQ.Client;

namespace Flash.Shared.Messaging;

public static class Topology
{
    public const string Exchange = "flash.work";
    public const string DeadLetterExchange = "flash.dlx";
    public const string RequeueExchange = "flash.requeue";
    public const string KafkaTopic = "flash.events";

    public const int RetryDelayMs = 2000;
    public const int MaxAttempts = 3;

    public const string ReserveKey = "pieces.reserve";
    public const string ReleaseKey = "pieces.release";
    public const string HeldKey = "pieces.held";
    public const string ShortKey = "pieces.short";
    public const string ConfirmKey = "pieces.confirm";
    public const string ExpiredKey = "pieces.expired";
    public const string ReleasedKey = "pieces.released";
    public const string IssuedKey = "receipt.issued";
    public const string RejectedKey = "receipt.rejected";

    public const string DealsQueue = "deals.commands";
    public const string ReceiptsQueue = "receipts.work";
    public const string OrdersQueue = "orders.events";

    public static async Task DeclareAsync(IChannel channel, string queue, string[] routingKeys, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(RequeueExchange, ExchangeType.Direct, durable: true, cancellationToken: ct);

        await channel.QueueDeclareAsync($"{queue}.dlq", durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync($"{queue}.dlq", DeadLetterExchange, queue, cancellationToken: ct);

        await channel.QueueDeclareAsync($"{queue}.retry", durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = RetryDelayMs,
                ["x-dead-letter-exchange"] = RequeueExchange,
                ["x-dead-letter-routing-key"] = queue,
            }, cancellationToken: ct);

        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = DeadLetterExchange,
                ["x-dead-letter-routing-key"] = queue,
            }, cancellationToken: ct);
        await channel.QueueBindAsync(queue, RequeueExchange, queue, cancellationToken: ct);
        foreach (var key in routingKeys)
            await channel.QueueBindAsync(queue, Exchange, key, cancellationToken: ct);
    }
}
