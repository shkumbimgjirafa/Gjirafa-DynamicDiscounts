using PricingTool.Core.Domain;
using PricingTool.Core.Options;
using PricingTool.Core.Services;

namespace PricingTool.Tests;

/// <summary>
/// The min-displayed-discount guardrail (pipeline tail in <see cref="PriceCalculator"/>): a proposed
/// price whose discount off the shelf (Old) price is below
/// <see cref="PricingEngineOptions.MinDisplayedDiscountPct"/> (default 3%) is a token markdown not worth
/// showing, so it's held at the full Old price. Fed through <see cref="PriceCalculator.Reprice"/>, which
/// runs the same clamp + rounding tail as the normal path.
/// </summary>
public class MinDiscountTests
{
    private readonly PriceCalculator _calc = new(new WeightedScoringService(), new GuardrailService(), new RoundingService());

    private static readonly string[] Reason = { "TEST" };

    // A normal, locally-selling SKU (not dead stock, not pinned): shelf/anchor 100, selling at 90.
    private static SkuContext SellingSku(PricingEngineOptions? options = null) =>
        TestData.Ctx(oldPrice: 100m, currentPrice: 90m, pptcv: 40m,
            ksStock: 10, supplierStock: 0, qty90: 12,
            band: TestData.Band(marginFloorPct: 10m), options: options);

    [Fact]
    public void SubThresholdDiscountOffOldPrice_HeldAtFullPrice()
    {
        // Proposed 98 = a 2% discount off the shelf price of 100 → under the 3% minimum → snap to 100.
        var decision = _calc.Reprice(SellingSku(), 98m, Reason);

        Assert.Equal(100m, decision.FinalPrice);
        Assert.Contains(GuardrailFlags.MinDiscountSuppressed, decision.GuardrailFlagsApplied);
        Assert.True(decision.Changed);
    }

    [Fact]
    public void DiscountAboveThreshold_Kept()
    {
        // Proposed 90 = a 10% discount off 100 → a real discount → left exactly where it is.
        var decision = _calc.Reprice(SellingSku(), 90m, Reason);

        Assert.Equal(90m, decision.FinalPrice);
        Assert.DoesNotContain(GuardrailFlags.MinDiscountSuppressed, decision.GuardrailFlagsApplied);
    }

    [Fact]
    public void DiscountExactlyAtThreshold_Kept()
    {
        // (100 − 97) / 100 = 3.0% is NOT below the 3% minimum, so the discount stands (strictly "under").
        var decision = _calc.Reprice(SellingSku(), 97m, Reason);

        Assert.Equal(97m, decision.FinalPrice);
        Assert.DoesNotContain(GuardrailFlags.MinDiscountSuppressed, decision.GuardrailFlagsApplied);
    }

    [Fact]
    public void ThresholdZero_Disables_TheRule()
    {
        var decision = _calc.Reprice(SellingSku(new PricingEngineOptions { MinDisplayedDiscountPct = 0m }), 98m, Reason);

        Assert.Equal(98m, decision.FinalPrice);
        Assert.DoesNotContain(GuardrailFlags.MinDiscountSuppressed, decision.GuardrailFlagsApplied);
    }

    [Fact]
    public void CustomThreshold_IsHonoured()
    {
        // A 4% discount (proposed 96) survives the default 3% but is suppressed under a 5% minimum.
        var decision = _calc.Reprice(SellingSku(new PricingEngineOptions { MinDisplayedDiscountPct = 5m }), 96m, Reason);

        Assert.Equal(100m, decision.FinalPrice);
        Assert.Contains(GuardrailFlags.MinDiscountSuppressed, decision.GuardrailFlagsApplied);
    }

    [Fact]
    public void DeadStock_IsExempt_NotRaisedToFullPrice()
    {
        // Locally-held, zero 90d sales → dead stock. Its markdown is a deliberate clearing price; the
        // min-discount rule must never lift it back to the full shelf price. (98 is a 2% discount.)
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m, pptcv: 40m,
            ksStock: 50, supplierStock: 0, qty90: 0, band: TestData.Band(marginFloorPct: 10m));

        var decision = _calc.Reprice(ctx, 98m, Reason);

        Assert.Equal(98m, decision.FinalPrice);
        Assert.DoesNotContain(GuardrailFlags.MinDiscountSuppressed, decision.GuardrailFlagsApplied);
    }

    [Fact]
    public void ClearanceLockPinned_IsExempt_LockNotBroken()
    {
        // A clearance-locked SKU is pinned at its pushed price (99); a 1% discount off the 100 shelf. The
        // min-discount rule must not override the pin by lifting it to full price.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, pptcv: 40m, clearanceLockPrice: 99m,
            ksStock: 0, supplierStock: 50, qty90: 12, band: TestData.Band(marginFloorPct: 10m));

        var decision = _calc.Reprice(ctx, 95m, Reason);

        Assert.Equal(99m, decision.FinalPrice);
        Assert.Contains(GuardrailFlags.ClearanceLockHeld, decision.GuardrailFlagsApplied);
        Assert.DoesNotContain(GuardrailFlags.MinDiscountSuppressed, decision.GuardrailFlagsApplied);
    }

    [Fact]
    public void NoDiscount_Raise_NotAffected()
    {
        // A would-be raise capped at the anchor (100) equals the shelf price — no discount to remove, so
        // the rule stays out of it (guards on final >= OldPrice).
        var decision = _calc.Reprice(SellingSku(), 130m, Reason);

        Assert.Equal(100m, decision.FinalPrice);
        Assert.DoesNotContain(GuardrailFlags.MinDiscountSuppressed, decision.GuardrailFlagsApplied);
    }

    // ---- The lift stops at the no-raise ceiling ------------------------------------------------
    // The shelf price (TierPrice.OldPrice) and the anchor (ProductPricing.FinalPrice) are different
    // sources, and the shelf one is routinely the higher — an inflated strike-through. Lifting to it
    // unbounded would carry the SKU further ahead of its anchor than it already sits, which the engine
    // must never do, so the lift is bounded by the guardrail ceiling (higher of anchor and current price).

    // A SKU whose shelf price (102) sits just ABOVE its anchor (100) — an inflated strike-through, the
    // common shape on GjirafaMall. Only a shelf price within the threshold of the ceiling can reach this
    // rule at all: anything further above is already a >3% discount once the clamp caps the price.
    private static SkuContext InflatedShelfSku(decimal currentPrice) =>
        TestData.Ctx(oldPrice: 102m, anchorPrice: 100m, currentPrice: currentPrice, pptcv: 40m,
            ksStock: 10, supplierStock: 0, qty90: 12, band: TestData.Band(marginFloorPct: 10m));

    [Fact]
    public void ShelfPriceAboveAnchor_LiftStopsAtTheAnchor()
    {
        // Proposed 99 is a 2.9% discount off the shelf price of 102 → under the 3% minimum. The lift stops
        // at the anchor (100) instead of reaching the full shelf price, which would sit 2% ahead of it.
        var decision = _calc.Reprice(InflatedShelfSku(currentPrice: 92m), 99m, Reason);

        Assert.Equal(100m, decision.FinalPrice);
        Assert.Contains(GuardrailFlags.MinDiscountSuppressed, decision.GuardrailFlagsApplied);
    }

    [Fact]
    public void ShelfPriceAboveAnchor_AlreadyAtTheAnchor_NotPushedPastIt()
    {
        // A would-be raise capped at the anchor (100) still shows only a 2% discount off the 102 shelf, so
        // the rule engages — but there is nowhere left to lift to. The price holds at the anchor, and the
        // flag isn't raised for a lift that never happened.
        var decision = _calc.Reprice(InflatedShelfSku(currentPrice: 92m), 130m, Reason);

        Assert.Equal(100m, decision.FinalPrice);
        Assert.DoesNotContain(GuardrailFlags.MinDiscountSuppressed, decision.GuardrailFlagsApplied);
    }

    [Fact]
    public void ShelfPriceAboveAnchor_CeilingIsTheCurrentPrice_WhenItSitsHigher()
    {
        // The SKU already sells at 101, above its 100 anchor: the ceiling is the current price, so the lift
        // may reach 101 but not the 102 shelf — the engine never pushes a SKU FURTHER ahead of its anchor
        // than it already sits. (Landing on the current price, this is a no-change proposal.)
        var decision = _calc.Reprice(InflatedShelfSku(currentPrice: 101m), 100m, Reason);

        Assert.Equal(101m, decision.FinalPrice);
        Assert.False(decision.Changed);
        Assert.Contains(GuardrailFlags.MinDiscountSuppressed, decision.GuardrailFlagsApplied);
    }

    [Fact]
    public void ShelfPriceBelowAnchor_StillLiftsToTheFullShelfPrice()
    {
        // The ceiling only ever binds an inflated shelf price. With the shelf (100) UNDER the anchor (130)
        // there is nothing to cap, and the rule lifts to the full shelf price exactly as before.
        var ctx = TestData.Ctx(oldPrice: 100m, anchorPrice: 130m, currentPrice: 90m, pptcv: 40m,
            ksStock: 10, supplierStock: 0, qty90: 12, band: TestData.Band(marginFloorPct: 10m));

        var decision = _calc.Reprice(ctx, 98m, Reason);

        Assert.Equal(100m, decision.FinalPrice);
        Assert.Contains(GuardrailFlags.MinDiscountSuppressed, decision.GuardrailFlagsApplied);
    }
}
