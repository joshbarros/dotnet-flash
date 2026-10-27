using System.Text.Json;
using Flash.Shared.Auth;
using Flash.Shared.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Flash.Orders;

[ApiController]
[Route("api/orders")]
[Authorize]
public class OrdersController(OrderService service, KafkaLog kafka) : ControllerBase
{
    /// <summary>Accepts the order for the signed-in user. Deals and the receipt follow asynchronously; poll GET to see them.</summary>
    [HttpPost]
    public async Task<ActionResult<OrderDto>> Create(CreateOrderRequest request, CancellationToken ct)
    {
        var order = await service.CreateAsync(request, ct);
        return Accepted($"/api/orders/{order.Id}", OrderDto.From(order));
    }

    /// <summary>Your orders, newest first, a page at a time (default 100, at most 100). Staff see everyone's. The total is in X-Total-Count.</summary>
    [HttpGet]
    public async Task<IEnumerable<OrderDto>> List([FromQuery] string? dealCode, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default)
    {
        var (items, total) = await service.ListAsync(dealCode, Math.Max(page, 1), Math.Clamp(pageSize, 1, 100), ct);
        Response.Headers["X-Total-Count"] = total.ToString();
        return items.Select(OrderDto.From);
    }

    [HttpGet("{id:guid}")]
    public async Task<OrderDto> Get(Guid id, CancellationToken ct) => OrderDto.From(await service.GetAsync(id, ct));

    /// <summary>Pays for a held order before its hold runs out. 202: the receipt follows. 409 when there is nothing to confirm.</summary>
    [HttpPost("{id:guid}/confirm")]
    public async Task<ActionResult<OrderDto>> Confirm(Guid id, CancellationToken ct) =>
        Accepted($"/api/orders/{id}", OrderDto.From(await service.ConfirmAsync(id, ct)));

    [HttpPost("{id:guid}/refund")]
    public async Task<ActionResult<OrderDto>> Refund(Guid id, CancellationToken ct) =>
        Accepted($"/api/orders/{id}", OrderDto.From(await service.RefundAsync(id, ct)));

    /// <summary>The Kafka event log for one order, read from the start of the topic. Staff only.</summary>
    [HttpGet("{id:guid}/events")]
    [Authorize(Policy = Policies.ViewAll)]
    public async Task<IEnumerable<JsonElement>> Events(Guid id, CancellationToken ct) =>
        (await Task.Run(() => kafka.Read(id.ToString(), TimeSpan.FromSeconds(10)), ct))
            .Select(v => JsonDocument.Parse(v).RootElement.Clone());
}
