using Flash.Shared;
using Xunit;

namespace Flash.UnitTests;

public class PoisonRuleTests
{
    [Theory]
    [InlineData("POISON")]
    [InlineData("SHP-POISON-7")]
    [InlineData("xxPOISONyy")]
    [InlineData("A-VERY-LONG-TOKEN-THAT-MERELY-CONTAINS-POISON-INSIDE")]
    public void Substring_MatchesInsideLongerTokens(string reference) =>
        Assert.True(PoisonRule.IsPoison(reference));

    [Theory]
    [InlineData("SHP-1")]
    [InlineData("POISO")]
    [InlineData("poison")]
    [InlineData("")]
    public void NonPoison_DoesNotMatch(string reference) =>
        Assert.False(PoisonRule.IsPoison(reference));
}
