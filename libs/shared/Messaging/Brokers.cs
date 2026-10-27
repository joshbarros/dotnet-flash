using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Flash.Shared.Messaging;

public class RabbitOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5714;
    public string User { get; set; } = "platform";
    public string Password { get; set; } = "platform";
    public string VirtualHost { get; set; } = "/";
}

public class KafkaOptions
{
    public string BootstrapServers { get; set; } = "localhost:9134";
}

public sealed class RabbitConnection(IOptions<RabbitOptions> options, ILogger<RabbitConnection> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true })
                return _connection;

            var o = options.Value;
            var factory = new ConnectionFactory
            {
                HostName = o.Host,
                Port = o.Port,
                UserName = o.User,
                Password = o.Password,
                VirtualHost = o.VirtualHost,
                AutomaticRecoveryEnabled = true,
            };
            while (true)
            {
                try
                {
                    _connection = await factory.CreateConnectionAsync(ct);
                    return _connection;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("RabbitMQ at {Host}:{Port} not ready: {Message}", o.Host, o.Port, ex.Message);
                    await Task.Delay(2000, ct);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}

/// <summary>The Kafka event log. Every outbox row is appended here by the relay, keyed by order id.</summary>
public sealed class KafkaLog(IOptions<KafkaOptions> options, ILogger<KafkaLog> logger) : IDisposable
{
    private IProducer<string, string>? _producer;
    private readonly object _lock = new();

    public string BootstrapServers => options.Value.BootstrapServers;

    private IProducer<string, string> Producer
    {
        get
        {
            lock (_lock)
            {
                return _producer ??= new ProducerBuilder<string, string>(new ProducerConfig
                {
                    BootstrapServers = BootstrapServers,
                    EnableIdempotence = true,
                    Acks = Acks.All,
                    MessageTimeoutMs = 15000,
                }).Build();
            }
        }
    }

    public async Task EnsureTopicAsync(CancellationToken ct)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = BootstrapServers }).Build();
        while (true)
        {
            try
            {
                await admin.CreateTopicsAsync([new TopicSpecification
                {
                    Name = Topology.KafkaTopic,
                    NumPartitions = 1,
                    ReplicationFactor = 1,
                }]);
                return;
            }
            catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
            {
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Kafka at {Servers} not ready: {Message}", BootstrapServers, ex.Message);
                await Task.Delay(2000, ct);
            }
        }
    }

    public Task AppendAsync(string key, string value, CancellationToken ct) =>
        Producer.ProduceAsync(Topology.KafkaTopic, new Message<string, string> { Key = key, Value = value }, ct);

    /// <summary>Reads the log from the beginning and returns the values whose key matches.</summary>
    public List<string> Read(string key, TimeSpan timeout)
    {
        var values = new List<string>();
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = BootstrapServers,
            GroupId = $"deals-read-{Guid.NewGuid():N}",
            EnablePartitionEof = true,
            EnableAutoCommit = false,
        }).Build();
        consumer.Assign(new TopicPartitionOffset(Topology.KafkaTopic, 0, Offset.Beginning));
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var result = consumer.Consume(TimeSpan.FromMilliseconds(500));
            if (result is null)
                continue;
            if (result.IsPartitionEOF)
                break;
            if (result.Message.Key == key)
                values.Add(result.Message.Value);
        }
        return values;
    }

    public void Dispose() => _producer?.Dispose();
}
