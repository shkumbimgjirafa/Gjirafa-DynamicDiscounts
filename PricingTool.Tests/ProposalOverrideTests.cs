using PricingTool.Web.Services;

namespace PricingTool.Tests;

public class ProposalOverrideTests
{
    [Fact]
    public void Recompute_PriceUp_PositiveChange()
    {
        var (proposed, changePct, hasChange) = ProposalOverride.Recompute(currentPrice: 10m, newPrice: 12m);
        Assert.Equal(12.00m, proposed);
        Assert.Equal(20.0000m, changePct);
        Assert.True(hasChange);
    }

    [Fact]
    public void Recompute_PriceDown_NegativeChange()
    {
        var (proposed, changePct, hasChange) = ProposalOverride.Recompute(50m, 40m);
        Assert.Equal(40.00m, proposed);
        Assert.Equal(-20.0000m, changePct);
        Assert.True(hasChange);
    }

    [Fact]
    public void Recompute_SamePrice_NoChange()
    {
        var (proposed, changePct, hasChange) = ProposalOverride.Recompute(29.99m, 29.99m);
        Assert.Equal(29.99m, proposed);
        Assert.Equal(0m, changePct);
        Assert.False(hasChange);
    }

    [Fact]
    public void Recompute_RoundsMoneyToTwoDecimals()
    {
        // 41.999 rounds to 42.00, and the Δ% is computed off the ROUNDED value.
        var (proposed, changePct, _) = ProposalOverride.Recompute(40m, 41.999m);
        Assert.Equal(42.00m, proposed);
        Assert.Equal(5.0000m, changePct); // (42 - 40) / 40 * 100
    }

    [Fact]
    public void Recompute_ZeroCurrent_ChangeIsZeroButStillChanged()
    {
        // Mirrors PricingDecision.ChangePct: no division when current price is 0.
        var (proposed, changePct, hasChange) = ProposalOverride.Recompute(0m, 5m);
        Assert.Equal(5.00m, proposed);
        Assert.Equal(0m, changePct);
        Assert.True(hasChange);
    }
}
