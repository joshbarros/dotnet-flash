using Flash.Orders;
using Xunit;

namespace Flash.UnitTests;

public class OrderRulesTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("   ", 1)]
    [InlineData("SHP-1", 0)]
    [InlineData("SHP-1", -3)]
    public void EmptyInput_IsRejected(string? reference, long pieces) =>
        Assert.NotNull(OrderRules.Validate(reference, pieces));

    [Fact]
    public void OverLimit_IsRejected()
    {
        Assert.Null(OrderRules.Validate("SHP-1", OrderRules.MaxPiecesPerOrder));
        Assert.NotNull(OrderRules.Validate("SHP-1", OrderRules.MaxPiecesPerOrder + 1));
        Assert.NotNull(OrderRules.Validate(new string('x', OrderRules.MaxReferenceLength + 1), 1));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData((long)int.MaxValue + 1)]
    [InlineData(4_294_967_297L)] // (int) of this wraps to 1; it must not pass as one piece
    [InlineData(long.MaxValue)]
    public void IntegerOverflow_IsRejectedNotWrapped(long pieces)
    {
        Assert.NotNull(OrderRules.Validate("SHP-1", pieces));
        Assert.Equal(1, unchecked((int)4_294_967_297L)); // documents what the naive cast would have done
    }

    [Fact]
    public void HappyTotal_IsFaceValuePlusATwelvePercentServiceFee()
    {
        Assert.Null(OrderRules.Validate("SHP-1", 2));
        Assert.Equal(10_000L + 1_200L, OrderRules.TotalCents(5_000, 2)); // $50.00 x 2 + $12.00 fee
        Assert.Equal(1L, OrderRules.TotalCents(1, 1)); // the fee truncates to zero
        Assert.Equal(7L, OrderRules.TotalCents(7, 1)); // 12% of 7 is 0.84 cents, truncated to 0
        Assert.Equal(112L, OrderRules.TotalCents(100, 1)); // 100 + 12
    }

    [Fact]
    public void TotalArithmetic_ThrowsInsteadOfWrapping()
    {
        Assert.Throws<OverflowException>(() => OrderRules.TotalCents(long.MaxValue, 2));
        Assert.Throws<OverflowException>(() => OrderRules.TotalCents(long.MaxValue / 2, 2)); // the fee multiplication overflows
    }
}
