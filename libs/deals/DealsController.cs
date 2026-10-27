using Flash.Shared;
using Flash.Shared.Auth;
using Flash.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Flash.Deals;

public record CreateDealRequest(string? Code, string? Name, string? Aisle, string? Brand, DateTimeOffset? SaleDate, long PiecesTotal, long PriceCents, long? MaxPerShopper = null);

public record DealDto(Guid Id, string Code, string Name, string Aisle, string Brand, DateTimeOffset SaleDate, long PriceCents, int PiecesTotal, int PiecesLeft, int MaxPerShopper)
{
    public static DealDto From(Deal s) => new(s.Id, s.Code, s.Name, s.Aisle, s.Brand, s.SaleDate, s.PriceCents, s.PiecesTotal, s.PiecesLeft, s.MaxPerShopper);
}

/// <summary>What other modules may ask Deals, over Deals's own tables.</summary>
public sealed class DealCatalog(DealsDbContext db) : IDealCatalog
{
    public async Task<DealInfo?> FindAsync(string code, CancellationToken ct) =>
        await db.Deals.AsNoTracking().Where(s => s.Code == code).Select(s => new DealInfo(s.Id, s.Code, s.PriceCents)).FirstOrDefaultAsync(ct);
}

[ApiController]
[Route("api/deals")]
[Authorize]
public class DealsController(DealsDbContext db) : ControllerBase
{
    private const int MaxPiecesTotal = 5000;
    private const long MaxPriceCents = 1_000_000;

    [HttpGet]
    public async Task<IEnumerable<DealDto>> List(CancellationToken ct) =>
        (await db.Deals.AsNoTracking().OrderBy(s => s.SaleDate).ThenBy(s => s.Code).ToListAsync(ct)).Select(DealDto.From);

    [HttpGet("{code}")]
    public async Task<ActionResult<DealDto>> Get(string code, CancellationToken ct)
    {
        var deal = await db.Deals.AsNoTracking().FirstOrDefaultAsync(s => s.Code == code, ct);
        return deal is null ? NotFound() : DealDto.From(deal);
    }

    /// <summary>Opens a deal. Staff only. This is the only place PiecesLeft is initialised; after that only the Deals handler writes it.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.ManageCatalog)]
    public async Task<ActionResult<DealDto>> Create(CreateDealRequest request, CancellationToken ct)
    {
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 20)
            throw new DomainException(400, "Code is required and at most 20 characters.");
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Aisle) || string.IsNullOrWhiteSpace(request.Brand))
            throw new DomainException(400, "Name, aisle and brand are required.");
        if (request.PiecesTotal < 1 || request.PiecesTotal > MaxPiecesTotal)
            throw new DomainException(400, $"Pieces on sale must be between 1 and {MaxPiecesTotal} pieces.");
        if (request.PriceCents < 1 || request.PriceCents > MaxPriceCents)
            throw new DomainException(400, $"Price must be between 1 and {MaxPriceCents} cents per piece.");
        var cap = request.MaxPerShopper ?? 2;
        if (cap < 1 || cap > request.PiecesTotal)
            throw new DomainException(400, "Per-shopper cap must be at least 1 and no more than the pieces on sale.");
        if (await db.Deals.AnyAsync(s => s.Code == code, ct))
            throw new DomainException(409, $"Deal '{code}' already exists.");

        var deal = new Deal
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            Name = request.Name.Trim(),
            Aisle = request.Aisle.Trim(),
            Brand = request.Brand.Trim(),
            SaleDate = request.SaleDate ?? DateTimeOffset.UtcNow.AddDays(14),
            PriceCents = request.PriceCents,
            PiecesTotal = (int)request.PiecesTotal,
            PiecesLeft = (int)request.PiecesTotal,
            MaxPerShopper = (int)cap,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Deals.Add(deal);
        await db.SaveChangesAsync(ct);
        return Created($"/api/deals/{deal.Code}", DealDto.From(deal));
    }
}
