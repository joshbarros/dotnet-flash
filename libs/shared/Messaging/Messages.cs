using System.Text.Json;

namespace Flash.Shared.Messaging;

public record ReservePieces(Guid OrderId, Guid OwnerId, Guid DealId, int Pieces, string Reference, bool Hold = false);
/// <summary>ExpiresAt is set while the hold waits for the shopper to confirm; the receipt is only issued without it.</summary>
public record PiecesHeld(Guid OrderId, Guid OwnerId, Guid DealId, int Pieces, string Reference, DateTimeOffset? ExpiresAt = null);
public record ConfirmPieces(Guid OrderId);
public record PiecesExpired(Guid OrderId, int Pieces);
public record PiecesShort(Guid OrderId, string Reference, int RequestedPieces, int PiecesLeft, string Reason = "stock", int PerShopperLimit = 0);
public record ReleasePieces(Guid OrderId, string Reason);
public record PiecesReleased(Guid OrderId, int Pieces, string Reason);
public record ReceiptIssued(Guid OrderId, string Number);
public record ReceiptRejected(Guid OrderId, string Reason);

public record Delivery(Guid MessageId, string RoutingKey, string Body);

public interface IMessageHandler
{
    Task HandleAsync(Delivery delivery, CancellationToken ct);
}

/// <summary>Committed as rejected; the message must be dead-lettered, not retried.</summary>
public class PoisonMessageException(string message) : Exception(message);

/// <summary>The message arrived before the state it depends on; retry later.</summary>
public class OutOfOrderException(string message) : Exception(message);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Parse<T>(string body) =>
        JsonSerializer.Deserialize<T>(body, Options) ?? throw new InvalidOperationException("Empty message body.");
}
