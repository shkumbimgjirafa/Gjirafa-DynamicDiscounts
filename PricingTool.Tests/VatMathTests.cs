using PricingTool.Core.Services;

namespace PricingTool.Tests;

public class VatMathTests
{
    [Fact]
    public void NetFromGross_StripsKosovoVat()
    {
        Assert.Equal(100m, VatMath.NetFromGross(118m, 18m));
    }

    [Fact]
    public void GrossFromNet_RoundTripsWithNetFromGross()
    {
        var gross = VatMath.GrossFromNet(100m, 18m);
        Assert.Equal(118m, gross);
        Assert.Equal(100m, VatMath.NetFromGross(gross, 18m));
    }

    [Fact]
    public void MarginPct_IsPriceMinusAllInCostOverPrice()
    {
        // PPTCV is the all-in, VAT-inclusive cost, so margin is measured directly off the selling price.
        Assert.Equal(50m, VatMath.MarginPct(100m, 50m));   // (100-50)/100
        Assert.Equal(20m, VatMath.MarginPct(50m, 40m));    // (50-40)/50
    }

    [Fact]
    public void MarginPct_NullCost_ReturnsNull_NeverAssumesZero()
    {
        Assert.Null(VatMath.MarginPct(118m, null));
    }

    [Fact]
    public void MinGrossPriceForMargin_YieldsExactlyTheFloor()
    {
        var floorPrice = VatMath.MinGrossPriceForMargin(50m, 50m); // 50 / (1 - 0.50) = 100
        Assert.Equal(100m, floorPrice);
        Assert.Equal(50m, VatMath.MarginPct(floorPrice, 50m));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(30)]
    public void MinGrossPriceForMargin_AlwaysSatisfiesFloor(decimal floor)
    {
        foreach (var cost in new[] { 1m, 12.34m, 500m })
        {
            var price = VatMath.MinGrossPriceForMargin(cost, floor);
            var margin = VatMath.MarginPct(price, cost);
            Assert.True(margin >= floor - 0.0001m, $"cost={cost} floor={floor} → margin={margin}");
        }
    }
}
