using PricingTool.Core.Domain;
using PricingTool.Core.Services;

namespace PricingTool.Tests;

public class GuardrailTests
{
    private readonly GuardrailService _guardrails = new();

    [Fact]
    public void Clamp_RaisesToMarginFloor()
    {
        // cost 50 (all-in, VAT-incl), floor 20% → floor = 50 / (1 - 0.20) = 62.50.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 50m, qty90: 12,
            band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 60m);

        Assert.Equal(62.50m, result.Price);
        Assert.Contains(GuardrailFlags.MarginFloorClamped, result.Flags);
    }

    [Fact]
    public void Clamp_DeepDiscount_AllowedDownToMarginFloor_NoCeiling()
    {
        // No discount ceiling: a deep vote is only stopped by the margin floor (cheap cost → 0
        // floor binding here), so the raw price passes through untouched with no flags.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 10m,
            band: TestData.Band(marginFloorPct: 10m));

        var result = _guardrails.Clamp(ctx, 30m);

        Assert.Equal(30m, result.Price);
        Assert.Empty(result.Flags);
    }

    [Fact]
    public void Clamp_CapsAtOldPrice()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m, pptcv: 40m);
        var result = _guardrails.Clamp(ctx, 130m);

        Assert.Equal(100m, result.Price);
        Assert.Contains(GuardrailFlags.CappedAtAnchor, result.Flags);
    }

    [Fact]
    public void Clamp_InRangePrice_Unchanged_NoFlags()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m, pptcv: 40m,
            band: TestData.Band(marginFloorPct: 10m));
        var result = _guardrails.Clamp(ctx, 85m);

        Assert.Equal(85m, result.Price);
        Assert.Empty(result.Flags);
    }

    [Fact]
    public void Clamp_MarginFloorAboveAnchor_HoldsAtAnchor_NotRaisedToFloor()
    {
        // cost 90 (all-in), floor 20% → floor = 90 / 0.80 = 112.50, above the anchor (OldPrice 100).
        // The tool never prices above the anchor — not even to reach the floor — so the price is HELD at
        // the ceiling (anchor 100), NOT raised to 112.50, and MARGIN_FLOOR_ABOVE_ANCHOR flags it. Because
        // the price stops short of the floor it is NOT a margin-floor clamp.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 95m, pptcv: 90m, qty90: 12,
            band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 95m);

        Assert.Equal(100m, result.Price);
        Assert.Contains(GuardrailFlags.MarginFloorAboveAnchor, result.Flags);
        Assert.DoesNotContain(GuardrailFlags.MarginFloorClamped, result.Flags);
    }

    [Fact]
    public void Bounds_MarginFloorAboveAnchor_CollapseToTheCeiling_Pinned()
    {
        // Floor above the ceiling (cost 90, floor 20% → 112.50 > anchor 100): the bounds must collapse to
        // the ceiling as a pinned point so rounding and the min-discount lift can't carry the price up to
        // the floor and past the anchor.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 95m, pptcv: 90m, qty90: 12,
            band: TestData.Band(marginFloorPct: 20m));

        var bounds = _guardrails.GetBounds(ctx);

        Assert.Equal(100m, bounds.Lower);
        Assert.Equal(100m, bounds.Upper);
    }

    [Fact]
    public void Clamp_MarginFloor_IsAllInCostOverFloor_NoVat()
    {
        // PPTCV is the all-in (VAT-incl) cost → floor = 50 / (1 - 0.20) = 62.50, no VAT in the cost path.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 50m, qty90: 12,
            band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 60m);

        Assert.Equal(62.50m, result.Price);
        Assert.Contains(GuardrailFlags.MarginFloorClamped, result.Flags);
        var margin = VatMath.MarginPct(result.Price, 50m);
        Assert.True(margin >= 20m - 0.0001m);
    }

    [Fact]
    public void Bounds_LowerIsMarginFloor_UpperIsOldPrice()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, pptcv: 50m, qty90: 12,
            band: TestData.Band(marginFloorPct: 20m));
        var bounds = _guardrails.GetBounds(ctx);

        // No discount ceiling: the only lower bound is the margin floor (cost 50 all-in, floor 20%
        // → 50 / 0.80 = 62.50). Upper bound is the shelf price.
        Assert.Equal(62.50m, bounds.Lower);
        Assert.Equal(100m, bounds.Upper);
    }

    // ---- Supplier-only (cross-dock) stock: priced by the cross-dock lane, bounded by the margin floor ----

    [Fact]
    public void Clamp_SupplierOnlyNonSelling_MarksDownToFloor()
    {
        // No local stock, all supplier, zero 90d sales: the cross-dock lane prices it, so a markdown is
        // no longer frozen at today's price — it passes down toward (but not below) the margin floor.
        // Cost 45 all-in, floor 10% → 45 / 0.90 = 50; a vote of 70 sits above the floor and stands.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m, pptcv: 45m,
            ksStock: 0, supplierStock: 50, qty90: 0, band: TestData.Band(marginFloorPct: 10m));

        var result = _guardrails.Clamp(ctx, 70m);

        Assert.Equal(70m, result.Price);
        Assert.Empty(result.Flags);
    }

    [Fact]
    public void Clamp_SupplierOnly_StopsAtMarginFloor_NoDeadStockTunnel()
    {
        // Supplier-only stock gets the NORMAL margin floor, never the dead-stock 50%-of-cost tunnel —
        // nothing is sunk to recover. A vote of 40 is clamped UP to the floor (50), not relaxed below it.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m, pptcv: 45m,
            ksStock: 0, supplierStock: 50, qty90: 0, band: TestData.Band(marginFloorPct: 10m));

        var result = _guardrails.Clamp(ctx, 40m);

        Assert.Equal(50m, result.Price);
        Assert.Contains(GuardrailFlags.MarginFloorClamped, result.Flags);
        Assert.DoesNotContain(GuardrailFlags.DeadStockFloorRelaxed, result.Flags);
    }

    [Fact]
    public void Bounds_SupplierOnlyNonSelling_LowerBoundIsMarginFloor()
    {
        // Lower bound is the margin floor (50), not today's price — so the cross-dock markdown has room.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m, pptcv: 45m,
            ksStock: 0, supplierStock: 50, qty90: 0, band: TestData.Band(marginFloorPct: 10m));

        var bounds = _guardrails.GetBounds(ctx);

        Assert.Equal(50m, bounds.Lower);
        Assert.Equal(100m, bounds.Upper);
    }

    [Fact]
    public void Clamp_SupplierOnly_AllowsRaisingTowardFullPrice()
    {
        // Removing an existing discount (raising the price) is fine and capped at the anchor.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m, pptcv: 45m,
            ksStock: 0, supplierStock: 50, qty90: 0, band: TestData.Band(marginFloorPct: 10m));

        var result = _guardrails.Clamp(ctx, 95m);

        Assert.Equal(95m, result.Price);
        Assert.Empty(result.Flags);
    }

    [Fact]
    public void Clamp_SupplierStockThatSells_IsNotBlocked()
    {
        // Supplier-only but it IS selling (qty90 > 0): a markdown passes (above the floor of 50).
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m, pptcv: 45m,
            ksStock: 0, supplierStock: 50, qty90: 12, band: TestData.Band(marginFloorPct: 10m));

        var result = _guardrails.Clamp(ctx, 70m);

        Assert.Equal(70m, result.Price);
        Assert.Empty(result.Flags);
    }

    [Fact]
    public void Clamp_LocallyHeldDeadStock_IsNotBlocked()
    {
        // We hold it locally → dead-stock markdown is allowed; the supplier rule must not fire.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m, pptcv: 10m,
            ksStock: 50, supplierStock: 0, qty90: 0, band: TestData.Band(marginFloorPct: 10m));

        var result = _guardrails.Clamp(ctx, 70m);

        Assert.Equal(70m, result.Price);
        Assert.Empty(result.Flags);
    }

    // ---- Local dead-stock "tunnel": the one case allowed below the margin floor ------------

    [Fact]
    public void Clamp_LocalDeadStock_DiscountPiercesMarginFloor()
    {
        // Locally held, zero 90d sales: the markdown is allowed below the margin floor (62.50) down
        // toward the dead-stock cost floor (default 100% of cost = 50.00). No floor clamp fires.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 0, band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 55m);

        Assert.Equal(55m, result.Price);
        Assert.Contains(GuardrailFlags.DeadStockFloorRelaxed, result.Flags);
        Assert.DoesNotContain(GuardrailFlags.MarginFloorClamped, result.Flags);
    }

    [Fact]
    public void Clamp_LocalDeadStock_StopsAtCostFloor_NeverBelowCost()
    {
        // A very deep dead-stock vote can't go below cost while in stage 1 ("3–6 months",
        // 90–180 days): the default stage-1 floor is 100% of PPTCV = 50.00.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 0, oldestUnitAgeDays: 120,
            band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 5m);

        Assert.Equal(50.00m, result.Price);
        Assert.Contains(GuardrailFlags.DeadStockFloorRelaxed, result.Flags);
    }

    [Fact]
    public void Clamp_LocalDeadStock_FloorIsPerBand()
    {
        // The dead-stock floor is per band: a 70%-of-cost floor stops the markdown at 0.70 × 50 = 35.00
        // (vs the default 100% → 50.00 above).
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 0,
            band: TestData.Band(marginFloorPct: 20m, deadStockFloorCostPct: 70m));

        var result = _guardrails.Clamp(ctx, 5m);

        Assert.Equal(35.00m, result.Price);
        Assert.Contains(GuardrailFlags.DeadStockFloorRelaxed, result.Flags);
    }

    // ---- Age-tiered dead-stock floors: the floor deepens as the stock ages ------------------

    [Fact]
    public void Clamp_LocalDeadStock_Stage2_FloorIs80PctOfCost()
    {
        // "6–12 months" (180–360 observed no-sale days): the floor drops to 80% of cost = 40.00 —
        // below-cost clearing is allowed once the SKU has gone unsold for half a year.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 0, deadObservedDays: 200,
            band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 5m);

        Assert.Equal(40.00m, result.Price);
        Assert.Contains(GuardrailFlags.DeadStockFloorRelaxed, result.Flags);
    }

    [Fact]
    public void Clamp_LocalDeadStock_Stage3_FloorIs60PctOfCost()
    {
        // "12–36 months" (360–1080 observed no-sale days): floor 60% of cost = 30.00.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 0, deadObservedDays: 400,
            band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 5m);

        Assert.Equal(30.00m, result.Price);
        Assert.Contains(GuardrailFlags.DeadStockFloorRelaxed, result.Flags);
    }

    [Theory]
    [InlineData(179, 50.00)]  // last day of stage 1 → at cost
    [InlineData(180, 40.00)]  // stage-2 threshold is inclusive → 80% of cost
    [InlineData(359, 40.00)]  // last day of stage 2
    [InlineData(360, 30.00)]  // stage-3 threshold is inclusive → 60% of cost
    [InlineData(1079, 30.00)] // last day of stage 3
    [InlineData(1080, 20.00)] // stage-4 threshold is inclusive → 40% of cost = the deepest floor
    public void Clamp_LocalDeadStock_StageBoundariesAreInclusive(int deadDays, decimal expectedFloor)
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 0, deadObservedDays: deadDays,
            band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 5m);

        Assert.Equal(expectedFloor, result.Price);
    }

    [Fact]
    public void Clamp_LocalDeadStock_JustWentDead_StartsAtStage1Floor()
    {
        // A SKU that has only just qualified as dead has observed-dead 0, so it starts at stage 1 (at
        // cost = 50.00) — it re-earns the deeper stage-2/3/4 floors by continued observation, never
        // jumping straight to them. (WMS physical age no longer drives the stage at all.)
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 0, deadObservedDays: 0,
            band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 5m);

        Assert.Equal(50.00m, result.Price);
        Assert.Contains(GuardrailFlags.DeadStockFloorRelaxed, result.Flags);
    }

    [Fact]
    public void Clamp_LocalDeadStock_StageTiersArePerBand()
    {
        // Tier thresholds and floors are per band: stage 2 starting at 120 no-sale days with a 70% floor
        // puts a SKU dead for 130 observed days at 0.70 × 50 = 35.00.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 0, deadObservedDays: 130,
            band: TestData.Band(marginFloorPct: 20m,
                deadStockStage2AgeDays: 120, deadStockStage2FloorCostPct: 70m));

        var result = _guardrails.Clamp(ctx, 5m);

        Assert.Equal(35.00m, result.Price);
        Assert.Contains(GuardrailFlags.DeadStockFloorRelaxed, result.Flags);
    }

    [Fact]
    public void Bounds_LocalDeadStock_Stage2_LowerIsTierFloor()
    {
        // GetBounds mirrors Clamp: a SKU dead for half a year (observed) has the stage-2 floor as its lower bound.
        var ctx = TestData.Ctx(oldPrice: 100m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 0, deadObservedDays: 200,
            band: TestData.Band(marginFloorPct: 20m));

        var bounds = _guardrails.GetBounds(ctx);

        Assert.Equal(40.00m, bounds.Lower);
        Assert.Equal(100m, bounds.Upper);
    }

    [Fact]
    public void Bounds_LocalDeadStock_Stage3_LowerIsTierFloor()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 0, deadObservedDays: 400,
            band: TestData.Band(marginFloorPct: 20m));

        var bounds = _guardrails.GetBounds(ctx);

        Assert.Equal(30.00m, bounds.Lower);
        Assert.Equal(100m, bounds.Upper);
    }

    [Fact]
    public void Clamp_LocalDeadStock_StartedSelling_HoldsTheBelowFloorPrice()
    {
        // It finally sold (qty90 > 0) at a below-floor tunnel price (40 < floor 62.50) the dead-stock lane
        // put it at (belowFloorTunnelPrice): hold it — don't raise it back, even though the blend voted higher.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 40m, pptcv: 50m, belowFloorTunnelPrice: 40m,
            ksStock: 50, supplierStock: 0, qty90: 6, band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 90m);

        Assert.Equal(40m, result.Price);
        Assert.Contains(GuardrailFlags.DeadStockTunnelHeld, result.Flags);
    }

    [Fact]
    public void Clamp_SellingBelowFloor_WithoutTunnelProvenance_IsClampedToTheMarginFloor()
    {
        // THE REGRESSION. Identical to the hold above except the dead-stock lane never took this SKU below
        // the floor — the floor moved instead (PPTCV rose). "Old local stock + selling + below floor" used to
        // be enough to pin it at 40, which both froze the blend's conclusion and silently blocked the very
        // repair the margin floor exists for.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 40m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 6, band: TestData.Band(marginFloorPct: 20m));

        // The blend's 90 now stands instead of being pinned back down to the 40 current price.
        var voted = _guardrails.Clamp(ctx, 90m);
        Assert.Equal(90m, voted.Price);
        Assert.DoesNotContain(GuardrailFlags.DeadStockTunnelHeld, voted.Flags);

        // And the floor is a hard bound again: nothing may be priced under it without tunnel provenance.
        var underFloor = _guardrails.Clamp(ctx, 45m);
        Assert.Equal(62.50m, underFloor.Price);
        Assert.Contains(GuardrailFlags.MarginFloorClamped, underFloor.Flags);
        Assert.DoesNotContain(GuardrailFlags.DeadStockTunnelHeld, underFloor.Flags);
    }

    [Fact]
    public void Clamp_SellingProductAboveFloor_StillHardClampedToMarginFloor()
    {
        // A selling product (qty90 > 0) priced above the floor is NOT in the tunnel: the margin
        // floor stays a hard limit — only dead stock pierces it.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 12, band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 60m);

        Assert.Equal(62.50m, result.Price);
        Assert.Contains(GuardrailFlags.MarginFloorClamped, result.Flags);
        Assert.DoesNotContain(GuardrailFlags.DeadStockFloorRelaxed, result.Flags);
    }

    [Fact]
    public void Bounds_LocalDeadStock_LowerIsCostFloor()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 0, band: TestData.Band(marginFloorPct: 20m));

        var bounds = _guardrails.GetBounds(ctx);

        Assert.Equal(50.00m, bounds.Lower);   // 100% of the all-in cost — below the 62.50 margin floor
        Assert.Equal(100m, bounds.Upper);
    }

    [Fact]
    public void Bounds_LocalDeadStock_StartedSelling_PinnedAtCurrentPrice()
    {
        // belowFloorTunnelPrice is the provenance the hold requires: the dead-stock lane put this SKU at 40.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 40m, pptcv: 50m, belowFloorTunnelPrice: 40m,
            ksStock: 50, supplierStock: 0, qty90: 6, band: TestData.Band(marginFloorPct: 20m));

        var bounds = _guardrails.GetBounds(ctx);

        Assert.Equal(40m, bounds.Lower);
        Assert.Equal(40m, bounds.Upper);
    }

    [Fact]
    public void Bounds_SellingBelowFloor_WithoutTunnelProvenance_NotPinned()
    {
        // Same shape, no marker: the floor MOVED under a selling SKU (a cost rise), so the bounds must stay
        // open — Lower at the margin floor — or the margin-target scan drops it as "pinned/degenerate".
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 40m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 6, band: TestData.Band(marginFloorPct: 20m));

        var bounds = _guardrails.GetBounds(ctx);

        Assert.Equal(62.50m, bounds.Lower);
        Assert.Equal(100m, bounds.Upper);
    }

    [Fact]
    public void Clamp_FreshlyStockedDeadStock_NoTunnel_HardClampedToMarginFloor()
    {
        // Zero 90d sales and locally held, but the oldest unit is only 5 days old — freshly stocked, not
        // dead. The tunnel must NOT open: a below-floor vote is clamped back up to the 62.50 margin floor.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 0, oldestUnitAgeDays: 5,
            band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 40m);

        Assert.Equal(62.50m, result.Price);
        Assert.Contains(GuardrailFlags.MarginFloorClamped, result.Flags);
        Assert.DoesNotContain(GuardrailFlags.DeadStockFloorRelaxed, result.Flags);
    }

    // ---- Below the CURRENT stage's floor: hold, don't raise (resume only after aging) --------

    [Fact]
    public void Clamp_LocalDeadStock_BelowStageFloor_NotSelling_HeldNotRaisedToStageFloor()
    {
        // Stage 1 (120 days), cost 50 → stage-1 floor = 50.00. It already sits at 45 (below the floor,
        // e.g. an earlier manual/promo markdown) and still isn't selling. It must NOT be raised back up
        // to 50 — hold it exactly where it is, and don't cut it further either.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 45m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 0, oldestUnitAgeDays: 120,
            band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 5m);   // the pacer wants to go even deeper

        Assert.Equal(45m, result.Price);
        Assert.Contains(GuardrailFlags.DeadStockBelowStageHeld, result.Flags);
        Assert.DoesNotContain(GuardrailFlags.DeadStockFloorRelaxed, result.Flags);
        Assert.DoesNotContain(GuardrailFlags.MarginFloorClamped, result.Flags);
    }

    [Fact]
    public void Clamp_LocalDeadStock_BelowStageFloor_HigherVote_StillNotRaised()
    {
        // The blend voting HIGHER (a raise) must not lift a held below-stage SKU either — "leave it as is".
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 45m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 0, oldestUnitAgeDays: 120,
            band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 90m);

        Assert.Equal(45m, result.Price);
        Assert.Contains(GuardrailFlags.DeadStockBelowStageHeld, result.Flags);
    }

    [Fact]
    public void Clamp_LocalDeadStock_BelowStageFloor_ResumesMarkdownOnceItAgesIntoADeeperStage()
    {
        // Same SKU at 45, but now dead 200 observed days → stage 2 (floor 80% of cost = 40.00). 40 sits
        // below the current 45, so the markdown may resume — cutting down to the new stage floor, not held.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 45m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 0, deadObservedDays: 200,
            band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 5m);

        Assert.Equal(40m, result.Price);
        Assert.Contains(GuardrailFlags.DeadStockFloorRelaxed, result.Flags);
        Assert.DoesNotContain(GuardrailFlags.DeadStockBelowStageHeld, result.Flags);
    }

    [Fact]
    public void Clamp_LocalDeadStock_BelowStageFloor_HeldUntilStageFloorDropsBelowIt()
    {
        // At 35 (below both the stage-1 floor 50 and the stage-2 floor 40, but above the stage-3 floor
        // 30): it stays HELD through stage 2 and only resumes cutting once it reaches stage 3.
        var band = TestData.Band(marginFloorPct: 20m);

        var stage2 = _guardrails.Clamp(
            TestData.Ctx(oldPrice: 100m, currentPrice: 35m, pptcv: 50m, ksStock: 50,
                qty90: 0, deadObservedDays: 200, band: band), 5m);
        Assert.Equal(35m, stage2.Price);   // still held — 35 is below the stage-2 floor (40)
        Assert.Contains(GuardrailFlags.DeadStockBelowStageHeld, stage2.Flags);

        var stage3 = _guardrails.Clamp(
            TestData.Ctx(oldPrice: 100m, currentPrice: 35m, pptcv: 50m, ksStock: 50,
                qty90: 0, deadObservedDays: 400, band: band), 5m);
        Assert.Equal(30m, stage3.Price);   // stage-3 floor (30) is below 35 → markdown resumes
        Assert.Contains(GuardrailFlags.DeadStockFloorRelaxed, stage3.Flags);
    }

    [Fact]
    public void Clamp_LocalDeadStock_BelowStage4Floor_NotSelling_PulledUpToHardFloor()
    {
        // Below the deepest (stage-4) floor (40% of 50 = 20.00): even the hold must not sit below it.
        // Current 15 is pulled UP to 20 — nothing may ever sell that far below cost.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 15m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 0, oldestUnitAgeDays: 120,
            band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 5m);

        Assert.Equal(20m, result.Price);
        Assert.Contains(GuardrailFlags.DeadStockBelowStageHeld, result.Flags);
        Assert.Contains(GuardrailFlags.DeadStockHardFloorClamped, result.Flags);
    }

    [Fact]
    public void Clamp_LocalDeadStock_Selling_BelowStage4Floor_PulledUpToHardFloor()
    {
        // The started-selling hold is also bounded by the stage-4 floor: a SKU selling at 15 (below
        // the 20 hard floor) is pulled up to 20 rather than frozen below it.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 15m, pptcv: 50m, belowFloorTunnelPrice: 15m,
            ksStock: 50, supplierStock: 0, qty90: 6, band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 90m);

        Assert.Equal(20m, result.Price);
        Assert.Contains(GuardrailFlags.DeadStockTunnelHeld, result.Flags);
        Assert.Contains(GuardrailFlags.DeadStockHardFloorClamped, result.Flags);
    }

    [Fact]
    public void Bounds_LocalDeadStock_BelowStageFloor_PinnedAtCurrent()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 45m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 0, oldestUnitAgeDays: 120,
            band: TestData.Band(marginFloorPct: 20m));

        var bounds = _guardrails.GetBounds(ctx);

        Assert.Equal(45m, bounds.Lower);
        Assert.Equal(45m, bounds.Upper);
    }

    [Fact]
    public void Bounds_LocalDeadStock_BelowStage4Floor_PinnedAtHardFloor()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 15m, pptcv: 50m,
            ksStock: 50, supplierStock: 0, qty90: 0, oldestUnitAgeDays: 120,
            band: TestData.Band(marginFloorPct: 20m));

        var bounds = _guardrails.GetBounds(ctx);

        Assert.Equal(20m, bounds.Lower);
        Assert.Equal(20m, bounds.Upper);
    }

    // ---- Anchor (FinalPrice) drives the ceiling, not the display-only OldPrice -------------

    [Fact]
    public void Clamp_CapsAtAnchor_NotInflatedOldPrice()
    {
        // The anchor (FinalPrice) 80 sits below the inflated shelf OldPrice 120. A vote above the
        // anchor is capped at the anchor — the shelf no longer governs the ceiling.
        var ctx = TestData.Ctx(oldPrice: 120m, anchorPrice: 80m, currentPrice: 75m, pptcv: 20m);

        var result = _guardrails.Clamp(ctx, 100m);

        Assert.Equal(80m, result.Price);
        Assert.Contains(GuardrailFlags.CappedAtAnchor, result.Flags);
    }

    [Fact]
    public void Clamp_CurrentAboveAnchor_NotForcedBelowCurrent()
    {
        // Today's selling price 90 sits above the anchor 80. The tool proposes discounts and may raise
        // back toward the anchor — but it must NOT force a markdown below the current price just because
        // the anchor is lower. The no-raise ceiling is max(anchor, current), so holding today's price stands.
        var ctx = TestData.Ctx(oldPrice: 120m, anchorPrice: 80m, currentPrice: 90m, pptcv: 20m);

        var result = _guardrails.Clamp(ctx, 90m);

        Assert.Equal(90m, result.Price);
        Assert.DoesNotContain(GuardrailFlags.CappedAtAnchor, result.Flags);
    }

    [Fact]
    public void Clamp_CurrentAboveAnchor_CapsAtCurrent_NotAnchor()
    {
        // A vote above BOTH the anchor (80) and the current price (90) is capped at the higher of the
        // two — the current price (90), not the anchor — so the guardrail never proposes a raise above
        // where the SKU already sits.
        var ctx = TestData.Ctx(oldPrice: 120m, anchorPrice: 80m, currentPrice: 90m, pptcv: 20m);

        var result = _guardrails.Clamp(ctx, 130m);

        Assert.Equal(90m, result.Price);
        Assert.Contains(GuardrailFlags.CappedAtAnchor, result.Flags);
    }

    [Fact]
    public void Bounds_UpperIsAnchor_NotOldPrice()
    {
        // Current price sits below the anchor, so the anchor (80) governs the ceiling — and the inflated
        // shelf OldPrice (120) never does.
        var ctx = TestData.Ctx(oldPrice: 120m, anchorPrice: 80m, currentPrice: 70m, pptcv: 20m,
            band: TestData.Band(marginFloorPct: 10m));

        var bounds = _guardrails.GetBounds(ctx);

        Assert.Equal(80m, bounds.Upper);
    }

    [Fact]
    public void Bounds_CurrentAboveAnchor_UpperIsCurrent()
    {
        // When the current price exceeds the anchor, the ceiling rises to the current price — the guardrail
        // won't force the SKU below where it already sits.
        var ctx = TestData.Ctx(oldPrice: 120m, anchorPrice: 80m, currentPrice: 90m, pptcv: 20m,
            band: TestData.Band(marginFloorPct: 10m));

        var bounds = _guardrails.GetBounds(ctx);

        Assert.Equal(90m, bounds.Upper);
    }

    // ---- Clearance lock: an active approved-batch SKU is pinned at its pushed price ----------------

    [Fact]
    public void Clamp_ClearanceLock_PinsAtPushedPrice_OverridesRaise()
    {
        // A batch member selling at its pushed clearance price of 80. An algorithm (or margin-target)
        // wants to walk it back up toward the 100 anchor — the lock pins it at 80 instead.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 40m, clearanceLockPrice: 80m,
            band: TestData.Band(marginFloorPct: 10m));

        var result = _guardrails.Clamp(ctx, 95m);

        Assert.Equal(80m, result.Price);
        Assert.Contains(GuardrailFlags.ClearanceLockHeld, result.Flags);
    }

    [Fact]
    public void Clamp_ClearanceLock_HoldsAtCurrent_WhenShopIsAlreadyDeeperThanPushed()
    {
        // The live price drifted below the pushed level (70 < 80) and an algorithm proposes deeper still
        // (65). The lock blocks the deepening — but it holds at what the shopper pays TODAY (70), not at
        // the batch's 80: pegging back up would raise a clearance price on stock we are still clearing,
        // which is the move the lock exists to prevent. It is a hold, never a raise.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 70m, pptcv: 40m, clearanceLockPrice: 80m,
            band: TestData.Band(marginFloorPct: 10m));

        var result = _guardrails.Clamp(ctx, 65m);

        Assert.Equal(70m, result.Price);   // not deepened to 65, and not raised to 80
        Assert.Contains(GuardrailFlags.ClearanceLockHeld, result.Flags);
    }

    [Fact]
    public void Clamp_ClearanceLock_PullsBackToPushed_WhenRaiseLeakedThrough()
    {
        // A raise already leaked to the shop (current 90 > pushed 80). The lock pulls it back down to the
        // approved 80 rather than leaving it raised.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m, pptcv: 40m, clearanceLockPrice: 80m,
            band: TestData.Band(marginFloorPct: 10m));

        var result = _guardrails.Clamp(ctx, 90m);

        Assert.Equal(80m, result.Price);
        Assert.Contains(GuardrailFlags.ClearanceLockHeld, result.Flags);
    }

    [Fact]
    public void Clamp_ClearanceLock_HoldsBelowMarginFloorClearancePrice_ForDeadStock()
    {
        // A dead-stock clearance member (default ctx: ksStock=10, qty90=0) pushed BELOW the margin floor —
        // the common case, since dead-stock markdown prices sit below cost by construction. The lock HOLDS
        // the approved price (deep below-cost clearance is the dead-stock tunnel's domain) rather than
        // raising it to the margin floor. cost 50, floor 20% → margin floor 62.50; hard floor 40% → 20.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, pptcv: 50m, clearanceLockPrice: 55m,
            band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 55m);

        Assert.Equal(55m, result.Price);                                       // held, NOT raised to 62.50
        Assert.Contains(GuardrailFlags.ClearanceLockHeld, result.Flags);
        Assert.DoesNotContain(GuardrailFlags.MarginFloorClamped, result.Flags);
    }

    [Fact]
    public void Clamp_ClearanceLock_ClampsUpToDeadStockHardFloor_WhenPushedBelowIt()
    {
        // The lock still respects the absolute stage-4 floor: cost 50, stage-4 40% → hard floor 20. A pushed
        // price below that (15) is pulled up to 20 — never deeper than the dead-stock system ever tolerates.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 20m, pptcv: 50m, clearanceLockPrice: 15m,
            band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 15m);

        Assert.Equal(20m, result.Price);
        Assert.Contains(GuardrailFlags.ClearanceLockHeld, result.Flags);
        Assert.Contains(GuardrailFlags.DeadStockHardFloorClamped, result.Flags);
    }

    [Fact]
    public void Clamp_ClearanceLock_NonDeadStock_StillBoundedByMarginFloor()
    {
        // A clearance-locked SKU that is NOT dead stock (freshly stocked, so the dead-stock tunnel doesn't
        // apply) keeps the ordinary margin-floor bound: a below-floor pushed price is raised to it.
        // cost 50, floor 20% → 62.50; freshly stocked (oldest unit 1 day < 90-day gate).
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, pptcv: 50m, clearanceLockPrice: 55m,
            oldestUnitAgeDays: 1, band: TestData.Band(marginFloorPct: 20m));

        var result = _guardrails.Clamp(ctx, 55m);

        Assert.Equal(62.50m, result.Price);
        Assert.Contains(GuardrailFlags.ClearanceLockHeld, result.Flags);
        Assert.Contains(GuardrailFlags.MarginFloorClamped, result.Flags);
    }

    [Fact]
    public void Clamp_NoClearanceLock_RaisesTowardAnchorAsBefore()
    {
        // Without a lock the guardrail is unchanged: a would-be raise still caps at the anchor.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 40m,
            band: TestData.Band(marginFloorPct: 10m));

        var result = _guardrails.Clamp(ctx, 130m);

        Assert.Equal(100m, result.Price);
        Assert.DoesNotContain(GuardrailFlags.ClearanceLockHeld, result.Flags);
    }

    [Fact]
    public void Bounds_ClearanceLock_PinsBothBoundsAtPushed()
    {
        // The pin makes bounds degenerate (Lower == Upper == pushed), so rounding is a no-op and the
        // margin-target scan skips the SKU as pinned.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 40m, clearanceLockPrice: 80m,
            band: TestData.Band(marginFloorPct: 10m));

        var bounds = _guardrails.GetBounds(ctx);

        Assert.Equal(80m, bounds.Lower);
        Assert.Equal(80m, bounds.Upper);
    }
}
