namespace Flash.Shared.Contracts;

public record DealInfo(Guid Id, string Code, long PriceCents);

/// <summary>
/// What Orders may ask Deals, without referencing it. Deals implements it over its own tables;
/// Orders never reads another module's schema.
/// </summary>
public interface IDealCatalog
{
    Task<DealInfo?> FindAsync(string code, CancellationToken ct);
}
