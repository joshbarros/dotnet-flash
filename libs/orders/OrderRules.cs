namespace Flash.Orders;

public static class OrderRules
{
    public const int MaxPiecesPerOrder = 6;
    public const int MaxReferenceLength = 40;
    public const long ServiceFeeBasisPoints = 1_200;

    /// <summary>Returns an error message, or null when the request is acceptable.</summary>
    public static string? Validate(string? reference, long pieces)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return "Shopper reference is required.";
        if (reference.Trim().Length > MaxReferenceLength)
            return $"Shopper reference must be at most {MaxReferenceLength} characters.";
        if (pieces < 1)
            return "An order needs at least 1 piece.";
        // Compared as long on purpose: narrowing 4294967297 to int would wrap to 1 and pass.
        if (pieces > MaxPiecesPerOrder)
            return $"An order takes at most {MaxPiecesPerOrder} pieces.";
        return null;
    }

    /// <summary>Face value plus a 12% service fee truncated to a whole cent, in cents. Overflow throws instead of wrapping.</summary>
    public static long TotalCents(long priceCents, int pieces)
    {
        var face = checked(priceCents * pieces);
        return checked(face + face * ServiceFeeBasisPoints / 10_000);
    }
}
