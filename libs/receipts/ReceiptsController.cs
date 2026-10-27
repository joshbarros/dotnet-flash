using Flash.Shared.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Flash.Receipts;

public record ReceiptDto(Guid OrderId, string? Number, string Status, string? Reason, DateTimeOffset CreatedAt);

[ApiController]
[Route("api/receipts")]
[Authorize]
public class ReceiptsController(ReceiptsDbContext db, ICurrentUser user) : ControllerBase
{
    /// <summary>The owner or staff only; anyone else gets a 404.</summary>
    [HttpGet("{orderId:guid}")]
    public async Task<ActionResult<ReceiptDto>> Get(Guid orderId, CancellationToken ct)
    {
        var doc = await db.Receipts.AsNoTracking().FirstOrDefaultAsync(d => d.OrderId == orderId, ct);
        return doc is null || !OwnerAccess.CanAccess(user, doc.OwnerId)
            ? NotFound()
            : new ReceiptDto(doc.OrderId, doc.Number, doc.Status.ToString(), doc.Reason, doc.CreatedAt);
    }
}
