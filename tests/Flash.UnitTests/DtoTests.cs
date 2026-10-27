using Flash.Orders;
using Flash.Deals;
using Xunit;

namespace Flash.UnitTests;

public class DtoTests
{
    [Fact]
    public void OrderDto_MapsEveryFieldAndTheStatusName()
    {
        var now = DateTimeOffset.UtcNow;
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            DealId = Guid.NewGuid(),
            DealCode = "S-1",
            Reference = "SHP-1",
            Pieces = 3,
            TotalCents = 277_500,
            Status = OrderStatus.Sold,
            ReceiptNumber = "FL-1",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var dto = OrderDto.From(order);

        Assert.Equal(order.Id, dto.Id);
        Assert.Equal(order.OwnerId, dto.OwnerId);
        Assert.Equal("Sold", dto.Status);
        Assert.Equal(277_500, dto.TotalCents);
        Assert.Equal("FL-1", dto.ReceiptNumber);
    }

    [Fact]
    public void DealDto_MapsTheScarceNumber()
    {
        var deal = new Deal { Id = Guid.NewGuid(), Code = "S-1", Name = "v", Aisle = "o", Brand = "d", PiecesTotal = 10, PiecesLeft = 4, MaxPerShopper = 3 };
        var dto = DealDto.From(deal);
        Assert.Equal(10, dto.PiecesTotal);
        Assert.Equal(3, dto.MaxPerShopper);
        Assert.Equal(4, dto.PiecesLeft);
    }
}
