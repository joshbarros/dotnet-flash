namespace Flash.Orders;

public enum OrderStatus { Requested, Held, Sold, Refunded, Rejected, Expired }

public class Order
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid DealId { get; set; }
    public string DealCode { get; set; } = "";
    public string Reference { get; set; } = "";
    public int Pieces { get; set; }
    public long TotalCents { get; set; }
    public OrderStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? ReceiptNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? PiecesReleasedAt { get; set; }
    /// <summary>Set while the hold waits for the shopper to confirm (POST /confirm); past it the pieces go back.</summary>
    public DateTimeOffset? HoldExpiresAt { get; set; }
}

public class OrderOptions
{
    /// <summary>Latency knob for the final mark. Compose sets it so the refund race window is wide enough to hit on purpose.</summary>
    public int FinalMarkDelayMs { get; set; }
}

public record CreateOrderRequest(string? DealCode, string? Reference, long Pieces, bool Hold = false);

public record OrderDto(
    Guid Id, Guid OwnerId, string DealCode, string Reference, int Pieces, long TotalCents, string Status,
    string? Reason, string? ReceiptNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? PiecesReleasedAt, DateTimeOffset? HoldExpiresAt = null)
{
    public static OrderDto From(Order b) => new(
        b.Id, b.OwnerId, b.DealCode, b.Reference, b.Pieces, b.TotalCents, b.Status.ToString(),
        b.Reason, b.ReceiptNumber, b.CreatedAt, b.UpdatedAt, b.PiecesReleasedAt, b.HoldExpiresAt);
}
