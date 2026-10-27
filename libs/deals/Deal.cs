namespace Flash.Deals;

public class Deal
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Aisle { get; set; } = "";
    public string Brand { get; set; } = "";
    public DateTimeOffset SaleDate { get; set; }
    public long PriceCents { get; set; }
    public int PiecesTotal { get; set; }
    public int PiecesLeft { get; set; }
    /// <summary>The most pieces one shopper may hold in this deal at any time.</summary>
    public int MaxPerShopper { get; set; } = 2;
    public DateTimeOffset CreatedAt { get; set; }
}

public enum HoldStatus { Held, Released }

public class PieceHold
{
    public Guid OrderId { get; set; }
    public Guid OwnerId { get; set; }
    public Guid DealId { get; set; }
    public int Pieces { get; set; }
    public HoldStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
    /// <summary>Set for holds that wait for the shopper to confirm. Past this time, and unconfirmed, the sweeper takes the pieces back.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool Confirmed { get; set; }
    public string Reference { get; set; } = "";
}

public class DealOptions
{
    /// <summary>How long an unconfirmed hold keeps its pieces.</summary>
    public int HoldSeconds { get; set; } = 600;

    /// <summary>How often each instance looks for expired holds.</summary>
    public int SweepIntervalMs { get; set; } = 1000;
}

/// <summary>How many pieces one shopper holds in one deal. Written only by the Deals handler.</summary>
public class DealShopper
{
    public Guid DealId { get; set; }
    public Guid OwnerId { get; set; }
    public int Pieces { get; set; }
}
