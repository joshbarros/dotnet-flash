using Flash.Shared.Messaging;
using Xunit;

namespace Flash.UnitTests;

public class ConsumerRetryPolicyTests
{
    [Fact]
    public void PoisonMessage_IsDeadLetteredAtOnce() =>
        Assert.Equal(FailureAction.DeadLetter, ConsumerRetryPolicy.Decide(0, new PoisonMessageException("poison")));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TransientFailure_RetriesWhileAttemptsRemain(int priorAttempts) =>
        Assert.Equal(FailureAction.Retry, ConsumerRetryPolicy.Decide(priorAttempts, new InvalidOperationException("db down")));

    [Fact]
    public void TransientFailure_IsDeadLetteredOnTheLastAttempt() =>
        Assert.Equal(FailureAction.DeadLetter, ConsumerRetryPolicy.Decide(Topology.MaxAttempts - 1, new InvalidOperationException("db down")));

    [Fact]
    public void OutOfOrderMessage_IsRetriedLikeAnyTransientFailure() =>
        Assert.Equal(FailureAction.Retry, ConsumerRetryPolicy.Decide(0, new OutOfOrderException("not yet")));
}

public class TopologyTests
{
    private static readonly string[] Keys =
    [
        Topology.ReserveKey, Topology.ReleaseKey, Topology.HeldKey, Topology.ShortKey,
        Topology.ReleasedKey, Topology.ConfirmKey, Topology.ExpiredKey, Topology.IssuedKey, Topology.RejectedKey,
    ];

    [Fact]
    public void RoutingKeys_AreDistinct() => Assert.Equal(Keys.Length, Keys.Distinct().Count());

    [Fact]
    public void Queues_AreDistinct()
    {
        string[] queues = [Topology.DealsQueue, Topology.ReceiptsQueue, Topology.OrdersQueue];
        Assert.Equal(queues.Length, queues.Distinct().Count());
    }

    [Fact]
    public void Exchanges_AreDistinctAndNamedForTheSystem()
    {
        string[] exchanges = [Topology.Exchange, Topology.DeadLetterExchange, Topology.RequeueExchange];
        Assert.Equal(exchanges.Length, exchanges.Distinct().Count());
        Assert.All(exchanges, e => Assert.StartsWith("flash.", e));
        Assert.Equal("flash.events", Topology.KafkaTopic);
    }

    [Fact]
    public void Retries_AreBoundedAndDelayed()
    {
        Assert.InRange(Topology.MaxAttempts, 2, 10);
        Assert.InRange(Topology.RetryDelayMs, 500, 60_000);
    }
}

public class MessageSerializationTests
{
    [Fact]
    public void DealsHeld_RoundTripsWithItsOwner()
    {
        var held = new PiecesHeld(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 3, "SHP-1");
        Assert.Equal(held, Json.Parse<PiecesHeld>(Json.Serialize(held)));
    }

    [Fact]
    public void Serialization_UsesCamelCase() =>
        Assert.Contains("\"orderId\"", Json.Serialize(new ReleasePieces(Guid.NewGuid(), "poison")));

    [Fact]
    public void Parse_IgnoresFieldsAddedByANewerPublisher()
    {
        var id = Guid.NewGuid();
        var parsed = Json.Parse<ReleasePieces>($$"""{"orderId":"{{id}}","reason":"poison","somethingNew":42}""");
        Assert.Equal(new ReleasePieces(id, "poison"), parsed);
    }

    [Fact]
    public void Parse_RejectsAnEmptyBody() => Assert.ThrowsAny<Exception>(() => Json.Parse<ReleasePieces>("null"));
}

public class DeadLetterTests
{
    [Fact]
    public void TheRoutingKey_ComesFromTheTypeProperty()
    {
        var props = new RabbitMQ.Client.BasicProperties { Type = "pieces.reserve" };
        Assert.Equal("pieces.reserve", DeadLetterService.RoutingKeyOf(props));
    }

    [Fact]
    public void WithoutAType_ThereIsNothingToReplayTo() =>
        Assert.Null(DeadLetterService.RoutingKeyOf(new RabbitMQ.Client.BasicProperties()));
}
