namespace Flash.Shared;

public static class PoisonRule
{
    public const string Token = "POISON";

    /// <summary>Case-sensitive substring match: a longer token that merely contains the poison matches too.</summary>
    public static bool IsPoison(string reference) => reference.Contains(Token, StringComparison.Ordinal);
}
