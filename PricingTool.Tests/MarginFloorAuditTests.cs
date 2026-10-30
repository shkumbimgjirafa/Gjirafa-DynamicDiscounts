using PricingTool.Core.Algorithms;
using PricingTool.Core.Domain;
using PricingTool.Core.Services;

namespace PricingTool.Tests;

/// <summary>
/// The margin-floor audit on the algorithm-silent path (<see cref="PriceCalculator.Decide"/>): when no
/// algorithm votes there is no proposed price for the clamp to bind, so a CURRENT price already sitting
/// below the floor (a campaign discount cutting past cost, a cost rise, a stale anchor) used to pass
/// through untouched and unflagged. The audit puts the current price through the same clamp + rounding
/// tail so the floor still binds, while every deliberate below-floor price (dead-stock tunnel, clearance
/// lock) stays exactly where it is.
///
/// Every test drives <c>Decide</c> with an EMPTY algorithm roster, which is the no-vote branch by
/// construction — the condition these SKUs hit in production.
/// </summary>
public class MarginFloorAuditTests
{
    private readonly PriceCalculator _calc = new(new WeightedScoringService(), new GuardrailService(), new RoundingService());

    private static readonly IPricingAlgorithm[] NoAlgorithms = Array.Empty<IPricingAlgorithm>();

    private PricingDecision DecideSilent(SkuContext ctx) => _calc.Decide(ctx, NoAlgorithms);

    /// <summary>
    /// The production shape this was built for: supplier-fulfilled (nothing held locally), no sales, and a
    /// live campaign price under cost. Cost 45 at a 10% floor ⇒ floor price 50.
    /// </summary>
    private static SkuContext SupplierOnlyBelowFloor(decimal currentPrice = 40m) =>
        TestData.Ctx(oldPrice: 100m, currentPrice: currentPrice, pptcv: 45m,
            ksStock: 0, supplierStock: 2, qty90: 0, band: TestData.Band(marginFloorPct: 10m));

    [Fact]
    public void SupplierOnly_CurrentBelowFloor_IsLiftedToTheFloor()
    {
        var decision = DecideSilent(SupplierOnlyBelowFloor());

        Assert.Equal(50m, decision.FinalPrice);
        Assert.True(decision.Changed);
        Assert.Contains(GuardrailFlags.MarginFloorClamped, decision.GuardrailFlagsApplied);
        Assert.Contains(GuardrailFlags.CurrentBelowFloorLifted, decision.ReasonCodes);
        Assert.Contains(GuardrailFlags.CurrentBelowFloorLifted, decision.GuardrailFlagsApplied);
        // Still an algorithm-silent row: no vote was invented, so the margin-target pass keeps treating
        // it as "free" and the Proposals UI keeps showing it as a guardrail move, not a recommendation.
        Assert.Null(decision.RawWeightedPrice);
        Assert.Empty(decision.Votes);
    }

    [Fact]
    public void CurrentAboveFloor_StillShortCircuits_Untouched()
    {
        // 60 clears the 50 floor: the audit must not fire, and the row must stay byte-for-byte what it was
        // before this change — unchanged price, no flags, no reason codes.
        var decision = DecideSilent(SupplierOnlyBelowFloor(currentPrice: 60m));

        Assert.Equal(60m, decision.FinalPrice);
        Assert.False(decision.Changed);
        Assert.Empty(decision.GuardrailFlagsApplied);
        Assert.Empty(decision.ReasonCodes);
        Assert.Null(decision.ClampedPrice);
    }

    [Fact]
    public void CurrentExactlyAtFloor_IsNotLifted()
    {
        var decision = DecideSilent(SupplierOnlyBelowFloor(currentPrice: 50m));

        Assert.Equal(50m, decision.FinalPrice);
        Assert.False(decision.Changed);
        Assert.Empty(decision.ReasonCodes);
    }

    [Fact]
    public void MissingCost_CannotBeAudited_Untouched()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 40m, pptcv: null,
            ksStock: 0, supplierStock: 2, qty90: 0, band: TestData.Band(marginFloorPct: 10m));

        var decision = DecideSilent(ctx);

        Assert.Equal(40m, decision.FinalPrice);
        Assert.False(decision.Changed);
        Assert.Empty(decision.ReasonCodes);
    }

    [Fact]
    public void DeadStock_BelowItsStageFloor_IsHeld_NotLifted()
    {
        // Locally held with zero 90-day sales ⇒ dead stock. 40 sits below the stage-1 floor (100% of the
        // 45 cost), which the guardrail deliberately HOLDS rather than raising. The audit must respect that.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 40m, pptcv: 45m,
            ksStock: 10, supplierStock: 0, qty90: 0, band: TestData.Band(marginFloorPct: 10m));

        var decision = DecideSilent(ctx);

        Assert.Equal(40m, decision.FinalPrice);
        Assert.False(decision.Changed);
        Assert.DoesNotContain(GuardrailFlags.CurrentBelowFloorLifted, decision.ReasonCodes);
        // The stage hold is the reason nothing moved, so the row has to carry it.
        Assert.Contains(GuardrailFlags.DeadStockBelowStageHeld, decision.GuardrailFlagsApplied);
    }

    [Fact]
    public void DeadStock_SellingAgainAtTunnelPrice_IsHeld_NotLifted()
    {
        // Locally held and selling again at a below-floor tunnel price (40 against a 50 floor) that the
        // dead-stock lane itself set (belowFloorTunnelPrice). The "that price is finally moving units" hold
        // outranks the floor — never claw it back.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 40m, pptcv: 45m, belowFloorTunnelPrice: 40m,
            ksStock: 10, supplierStock: 0, qty90: 12, band: TestData.Band(marginFloorPct: 10m));

        var decision = DecideSilent(ctx);

        Assert.Equal(40m, decision.FinalPrice);
        Assert.False(decision.Changed);
        Assert.DoesNotContain(GuardrailFlags.CurrentBelowFloorLifted, decision.ReasonCodes);
        Assert.Contains(GuardrailFlags.DeadStockTunnelHeld, decision.GuardrailFlagsApplied);
    }

    [Fact]
    public void SellingBelowFloor_WithoutTunnelProvenance_IsLifted_NotSilentlyPinned()
    {
        // The same shape with NO tunnel provenance: the dead-stock lane never touched this price, the FLOOR
        // moved under it (a cost rise). It used to satisfy the "started selling again" hold on nothing more
        // than old local stock + a sale + being below the floor, which pinned the clamp — and because a
        // no-op clamp means the audit sees no raise, the row was written with no flags at all, invisible.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 40m, pptcv: 45m,
            ksStock: 10, supplierStock: 0, qty90: 12, band: TestData.Band(marginFloorPct: 10m));

        var decision = DecideSilent(ctx);

        Assert.Equal(50m, decision.FinalPrice);
        Assert.True(decision.Changed);
        Assert.Contains(GuardrailFlags.MarginFloorClamped, decision.GuardrailFlagsApplied);
        Assert.Contains(GuardrailFlags.CurrentBelowFloorLifted, decision.ReasonCodes);
        Assert.Contains(GuardrailFlags.CurrentBelowFloorLifted, decision.GuardrailFlagsApplied);
    }

    [Fact]
    public void ProductionCase_GjirafaMallKosovo_CostRoseUnderASellingSku()
    {
        // SKU 201215lt (Lattafa Niche Emarati Zikra, GjirafaMall Kosovo run 352). PPTCV rose 31.6830 →
        // 39.6008 between the 03:00 and 09:01 runs, moving the band-2 (14%) floor from 36.84 to 46.05 and
        // leaving the live 38.99 campaign price BELOW COST. Nothing voted (DEAD_STOCK silent — it sells;
        // ELASTICITY usable at −1.84 but not confidently elastic), and the SKU had 2 units of local stock
        // whose oldest unit had just crossed the 90-day freshness gate (92 days) — so the hold fired on a
        // SKU that was never dead, and the run reported "no change" with no flags at all.
        var ctx = TestData.Ctx(oldPrice: 79.99m, anchorPrice: 76.50m, currentPrice: 38.99m, pptcv: 39.6008m,
            ksStock: 2, supplierStock: 483, qty7: 1, qty14: 2, qty30: 3, qty60: 5, qty90: 9,
            oldestUnitAgeDays: 92,
            band: TestData.Band(marginFloorPct: 14m, rounding: RoundingConvention.GjmCharm, roundingEnabled: true));

        var decision = DecideSilent(ctx);

        // 39.6008 / 0.86 = 46.0474, rounded UP the GjmCharm grid — never back below the floor.
        Assert.Equal(46.49m, decision.FinalPrice);
        Assert.True(decision.FinalPrice >= 46.0474m, $"rounded price {decision.FinalPrice} breached the floor");
        Assert.True(decision.Changed);
        Assert.Contains(GuardrailFlags.MarginFloorClamped, decision.GuardrailFlagsApplied);
        Assert.Contains(GuardrailFlags.CurrentBelowFloorLifted, decision.GuardrailFlagsApplied);
        Assert.DoesNotContain(GuardrailFlags.DeadStockTunnelHeld, decision.GuardrailFlagsApplied);
    }

    [Fact]
    public void DeadStock_BelowTheAbsoluteHardFloor_IsPulledUpToIt()
    {
        // The one below-floor case that IS a genuine breach: 30 sits under the absolute stage-4 floor
        // (40% of the 100 cost = 40). Nothing may ever sell that far below cost, so it is pulled up —
        // to the hard floor, NOT to the margin floor.
        var ctx = TestData.Ctx(oldPrice: 200m, currentPrice: 30m, pptcv: 100m,
            ksStock: 10, supplierStock: 0, qty90: 0, band: TestData.Band(marginFloorPct: 10m));

        var decision = DecideSilent(ctx);

        Assert.Equal(40m, decision.FinalPrice);
        Assert.True(decision.Changed);
        Assert.Contains(GuardrailFlags.DeadStockHardFloorClamped, decision.GuardrailFlagsApplied);
        Assert.Contains(GuardrailFlags.CurrentBelowFloorLifted, decision.ReasonCodes);
        Assert.Contains(GuardrailFlags.CurrentBelowFloorLifted, decision.GuardrailFlagsApplied);
    }

    [Fact]
    public void ClearanceLocked_IsExempt_PinNotDisturbed_ButSaysSo()
    {
        // An approved clearance price is deliberately below cost. Pulling a silent SKU up to (or off) that
        // pin is the batch's business, not the floor's — leave it exactly as it was.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 40m, pptcv: 45m, clearanceLockPrice: 60m,
            ksStock: 0, supplierStock: 2, qty90: 0, band: TestData.Band(marginFloorPct: 10m));

        var decision = DecideSilent(ctx);

        Assert.Equal(40m, decision.FinalPrice);
        Assert.False(decision.Changed);
        Assert.Empty(decision.ReasonCodes);
        // …and the row must ACCOUNT for itself: a price held below cost with no flag at all is
        // indistinguishable from an untouched SKU, and unfindable in the Guardrails filter.
        Assert.Contains(GuardrailFlags.ClearanceLockHeld, decision.GuardrailFlagsApplied);
    }

    [Fact]
    public void NoGuardrailActing_RecordsNothing_NoNoise()
    {
        // The ~99% case on a full-catalog run: nothing voted, nothing is holding it, price clears the floor.
        // This must stay completely blank — the hold flags are only ever an explanation, never decoration.
        var decision = DecideSilent(SupplierOnlyBelowFloor(currentPrice: 60m));

        Assert.False(decision.Changed);
        Assert.Empty(decision.GuardrailFlagsApplied);
        Assert.Empty(decision.ReasonCodes);
    }

    [Fact]
    public void ProductionCase_GjirafaMallKosovo_ClearanceLockedBelowFloor_IsHeldAndFlagged()
    {
        // SKU 166916lt (Maison Alhambra Sceptre Amazonite, GjirafaMall Kosovo run 353): a member of the
        // ACTIVE clearance batch pushed 2026-08-03 at 22.49, still holding 2 local units and selling (4 in
        // 7d), so the lock pins it. Cost had risen to 21.5114, putting it under the band-2 (14%) floor of
        // 25.01 at a 4.35% margin. Holding is correct — but the run used to report it as a blank row, so
        // there was no way to see that an approved clearance lock was the reason.
        var ctx = TestData.Ctx(oldPrice: 29.99m, anchorPrice: 29.50m, currentPrice: 22.49m, pptcv: 21.5114m,
            clearanceLockPrice: 22.49m, ksStock: 2, supplierStock: 0,
            qty7: 4, qty14: 4, qty30: 4, qty60: 4, qty90: 4, oldestUnitAgeDays: 251,
            band: TestData.Band(marginFloorPct: 14m, rounding: RoundingConvention.GjmCharm, roundingEnabled: true));

        var decision = DecideSilent(ctx);

        Assert.Equal(22.49m, decision.FinalPrice);
        Assert.False(decision.Changed);
        Assert.Contains(GuardrailFlags.ClearanceLockHeld, decision.GuardrailFlagsApplied);
        Assert.DoesNotContain(GuardrailFlags.CurrentBelowFloorLifted, decision.GuardrailFlagsApplied);
    }

    [Fact]
    public void NewProduct_BelowFloor_StaysHeldByTheNewProductRule()
    {
        // New-product protection short-circuits ahead of the audit and must keep doing so.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 40m, pptcv: 45m, isNewProduct: true,
            ksStock: 0, supplierStock: 2, qty90: 0, band: TestData.Band(marginFloorPct: 10m));

        var decision = DecideSilent(ctx);

        Assert.Equal(40m, decision.FinalPrice);
        Assert.False(decision.Changed);
        Assert.Contains(GuardrailFlags.NewProductProtected, decision.ReasonCodes);
        Assert.DoesNotContain(GuardrailFlags.CurrentBelowFloorLifted, decision.ReasonCodes);
    }

    [Fact]
    public void MispricedFloorAboveAnchor_LiftsOnlyToTheAnchor_NotPastIt_AndKeepsItsHumanAlert()
    {
        // Cost 90 at a 10% floor ⇒ floor 100, above the 90 anchor. The current price (80) is lifted back
        // UP TOWARD the floor — but never past the anchor, so it stops at the anchor (90), not at 100. The
        // "the floor sits above the anchor" alarm survives so a human sees the SKU is priced at/under cost.
        // The price never reaches the floor, so it is NOT a margin-floor clamp.
        var ctx = TestData.Ctx(oldPrice: 90m, currentPrice: 80m, pptcv: 90m,
            ksStock: 0, supplierStock: 2, qty90: 0, band: TestData.Band(marginFloorPct: 10m));

        var decision = DecideSilent(ctx);

        Assert.Equal(90m, decision.FinalPrice);
        Assert.True(decision.Changed);
        Assert.Contains(GuardrailFlags.MarginFloorAboveAnchor, decision.GuardrailFlagsApplied);
        Assert.DoesNotContain(GuardrailFlags.MarginFloorClamped, decision.GuardrailFlagsApplied);
        // It did lift the current price (up to the anchor), so the repair is still recorded and findable.
        Assert.Contains(GuardrailFlags.CurrentBelowFloorLifted, decision.ReasonCodes);
        Assert.Contains(GuardrailFlags.CurrentBelowFloorLifted, decision.GuardrailFlagsApplied);
    }

    [Fact]
    public void MispricedFloorAboveAnchor_CurrentAtAnchor_HeldWithNoChange_ButStillFlagged()
    {
        // The production shape (run 381): anchor == current == cost, and the cost-derived floor lands above
        // both. There is no room to lift without passing the anchor, so nothing moves — but the row must
        // still carry MARGIN_FLOOR_ABOVE_ANCHOR so the Guardrails filter can surface the mispriced SKU
        // (otherwise it would be an invisible no-change, no-flag row).
        var ctx = TestData.Ctx(oldPrice: 90m, anchorPrice: 90m, currentPrice: 90m, pptcv: 90m,
            ksStock: 0, supplierStock: 2, qty90: 0, band: TestData.Band(marginFloorPct: 10m));

        var decision = DecideSilent(ctx);

        Assert.Equal(90m, decision.FinalPrice);
        Assert.False(decision.Changed);
        Assert.Contains(GuardrailFlags.MarginFloorAboveAnchor, decision.GuardrailFlagsApplied);
        Assert.DoesNotContain(GuardrailFlags.CurrentBelowFloorLifted, decision.GuardrailFlagsApplied);
        Assert.DoesNotContain(GuardrailFlags.CurrentBelowFloorLifted, decision.ReasonCodes);
    }

    [Fact]
    public void ProductionCase_GjirafaMallKosovo_SupplierOnlySkuUnderCost()
    {
        // The case this was built for (SKU 7057823mo, GjirafaMall Kosovo run 291): supplier-only, no sales,
        // and a live campaign discount holding it at 25.99 against a 28.2488 all-in cost — selling at a
        // ~8.7% NEGATIVE margin. Only ELASTICITY and DEAD_STOCK are enabled on that layer and both abstain
        // (no fitted curve; no local stock), so nothing voted and the price used to stand untouched.
        // Band "10-50": 13% floor ⇒ floor price 28.2488 / 0.87 = 32.4699, rounded up the GjmCharm grid.
        var ctx = TestData.Ctx(oldPrice: 44.50m, anchorPrice: 39.50m, currentPrice: 25.99m, pptcv: 28.2488m,
            ksStock: 0, supplierStock: 2, qty90: 0,
            band: TestData.Band(marginFloorPct: 13m, rounding: RoundingConvention.GjmCharm, roundingEnabled: true));

        var decision = DecideSilent(ctx);

        Assert.Equal(32.99m, decision.FinalPrice);
        Assert.True(decision.Changed);
        Assert.Contains(GuardrailFlags.MarginFloorClamped, decision.GuardrailFlagsApplied);
        Assert.Contains(GuardrailFlags.CurrentBelowFloorLifted, decision.ReasonCodes);
        Assert.Contains(GuardrailFlags.CurrentBelowFloorLifted, decision.GuardrailFlagsApplied);
    }

    [Fact]
    public void LiftedPrice_StillRoundsOnTheBandConvention()
    {
        // The audit reuses the normal pipeline tail, so psychological rounding applies to the lifted
        // price and stays inside the guardrail bounds (floor 50 ⇒ rounds UP to the .99 grid, never below).
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 40m, pptcv: 45m,
            ksStock: 0, supplierStock: 2, qty90: 0,
            band: TestData.Band(marginFloorPct: 10m, rounding: RoundingConvention.Gj50Charm, roundingEnabled: true));

        var decision = DecideSilent(ctx);

        Assert.True(decision.FinalPrice >= 50m, $"rounded price {decision.FinalPrice} breached the 50 floor");
        Assert.Contains(GuardrailFlags.CurrentBelowFloorLifted, decision.ReasonCodes);
        Assert.Contains(GuardrailFlags.CurrentBelowFloorLifted, decision.GuardrailFlagsApplied);
    }
}
