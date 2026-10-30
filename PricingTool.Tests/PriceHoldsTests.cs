using PricingTool.Core.Domain;

namespace PricingTool.Tests;

/// <summary>
/// Which proposals the Price Bands views (Target impact, discount-optimization impact) treat as held — prices the
/// Mall engine does not set, so neither a band target nor a discount trim can move them.
/// </summary>
public class PriceHoldsTests
{
    [Fact]
    public void Gj50Adoption_IsHeld_FromItsReasonCodes()
    {
        // A synced row as SyncedProposal writes it: SYNCED_TO_GJ50 leads the reason codes, followed by the Gj50
        // decision's own (here its margin-target trim), and the guardrail flags are empty — no Mall guardrail ran.
        Assert.True(PriceHolds.IsHeld(DeadStockStage.None, "", "SYNCED_TO_GJ50,MARGIN_TARGET_TRIM"));
    }

    [Fact]
    public void MallsOwnMarginTargetTrim_IsNotHeld()
    {
        Assert.False(PriceHolds.IsHeld(DeadStockStage.None, "MARGIN_TARGET_ADJUSTED", "MARGIN_TARGET_TRIM"));
        Assert.False(PriceHolds.IsHeld(DeadStockStage.None, null, null));
    }

    [Fact]
    public void HoldFlagsAndDeadStock_AreHeld()
    {
        Assert.True(PriceHolds.IsHeld(DeadStockStage.None, "CLEARANCE_LOCK_HELD", "ELASTIC_OPTIMAL"));
        Assert.True(PriceHolds.IsHeld(DeadStockStage.Stage1, "", ""));
    }
}
