using PricingTool.Core.Domain;
using PricingTool.Web.Services;

namespace PricingTool.Tests;

/// <summary>
/// The computed "current price below the band's margin floor" diagnostic shared by the Proposals grid
/// badge and the CSV export column (<see cref="ReasonCodeText.IsCurrentBelowFloor"/>).
/// </summary>
public class ReasonCodeTextTests
{
    [Fact]
    public void CurrentBelowFloor_True_WhenCurrentMarginUnderBandFloor()
    {
        // cost 90, price 100 → 10% margin, under the 20% floor.
        Assert.True(ReasonCodeText.IsCurrentBelowFloor(90m, 100m, 20m, "", ""));
    }

    [Fact]
    public void CurrentBelowFloor_False_WhenMarginAtOrAboveFloor()
    {
        // cost 80, price 100 → exactly 20% = the floor → not below (strict).
        Assert.False(ReasonCodeText.IsCurrentBelowFloor(80m, 100m, 20m, "", ""));
        // cost 50, price 100 → 50% margin, well above.
        Assert.False(ReasonCodeText.IsCurrentBelowFloor(50m, 100m, 20m, "", ""));
    }

    [Fact]
    public void CurrentBelowFloor_Suppressed_ForDeadStock()
    {
        // Below floor, but a DEAD_STOCK_* guardrail already explains the below-floor price.
        Assert.False(ReasonCodeText.IsCurrentBelowFloor(
            90m, 100m, 20m, GuardrailFlags.DeadStockFloorRelaxed, ""));
        Assert.False(ReasonCodeText.IsCurrentBelowFloor(
            90m, 100m, 20m, GuardrailFlags.DeadStockTunnelHeld, ""));
    }

    [Fact]
    public void CurrentBelowFloor_Suppressed_ForGj50Synced()
    {
        // Gjirafa50-synced SKUs skip Mall's guardrails entirely — don't flag them.
        Assert.False(ReasonCodeText.IsCurrentBelowFloor(
            90m, 100m, 20m, "", GuardrailFlags.SyncedToGj50));
    }

    [Fact]
    public void CurrentBelowFloor_False_WhenCostOrFloorOrPriceMissing()
    {
        Assert.False(ReasonCodeText.IsCurrentBelowFloor(null, 100m, 20m, "", ""));   // no cost
        Assert.False(ReasonCodeText.IsCurrentBelowFloor(90m, 100m, null, "", ""));   // no band floor
        Assert.False(ReasonCodeText.IsCurrentBelowFloor(90m, 0m, 20m, "", ""));      // no usable price
    }
}
