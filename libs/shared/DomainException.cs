namespace Flash.Shared;

/// <summary>An error the caller can act on. The middleware turns it into a problem+json response with this status.</summary>
public class DomainException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
