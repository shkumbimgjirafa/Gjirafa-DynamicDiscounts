using PricingTool.Core.Algorithms;
using PricingTool.Core.Domain;
using PricingTool.Core.Options;
using PricingTool.Core.Services;

namespace PricingTool.Tests;

public class SellThroughTests
{
    private readonly SellThroughAlgorithm _algorithm = new();

    [Fact]
    public void ImminentSelloutHealthyMargin_RemovesDiscount()
    {
        // 2/day vs 10 units → 5 days ≤ 14; computed margin ≈44% well above floor+5 → remove the discount.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 85m, pptcv: 40m, ksStock: 10,
            qty7: 14, qty14: 28, qty30: 60, qty90: 180);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(100m, vote!.SuggestedPrice);   // Math.Max(anchor 100, current 85) — never a markdown
        Assert.Equal("SELL_THROUGH_REMOVE", vote.ReasonCode);
    }

    [Fact]
    public void ImminentSelloutThinMargin_ShavesInsteadOfRemoving()
    {
        // Same fast sellout but thin margin (cost 75 → (85-75)/85 = 11.8% < floor+5) → no remove;
        // the fast branch shaves 5pp off the 15% discount.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 85m, pptcv: 75m, ksStock: 10,
            qty7: 14, qty14: 28, qty30: 60, qty90: 180);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(90m, vote!.SuggestedPrice);     // 15% → 10% off anchor 100
        Assert.Equal("SELL_THROUGH_FAST", vote.ReasonCode);
    }

    [Fact]
    public void VerySlowSellThrough_DeepensDiscount()
    {
        // ~0.14/day vs 100 units → ~700 days → +10pp on top of today's 10%.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m, ksStock: 100,
            qty7: 1, qty14: 2, qty30: 4, qty90: 12);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(80m, vote!.SuggestedPrice);     // 10% + 10pp off anchor 100
        Assert.Equal("SELL_THROUGH_SLOW", vote.ReasonCode);
    }

    [Fact]
    public void DeceleratingTrend_DeepensViaTrendModifier()
    {
        // ~40 days of stock (hold band) but 7d velocity collapsed → decelerating → +3pp.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, ksStock: 20,
            qty7: 0, qty14: 14, qty30: 30, qty90: 90);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(77m, vote!.SuggestedPrice);     // hold 20% + 3pp decel = 23% off anchor 100
        Assert.Equal("SELL_THROUGH_SLOW", vote.ReasonCode);
    }

    // ---- Trend reliability + price-validity gates -----------------------
    // A fast mover, ~60 local units at a weighted ≈1.87/day → ≈32 days of stock → the "on pace — hold"
    // band, so the base target is the current 20% discount and only the trend modifier can move it.
    // Baseline (days 8–90) = 83 units → μ ≈ 7 expected in the last 7d; observing 15 is z ≈ 3.0 → a
    // trustworthy acceleration. Whether the −3pp shave then fires depends on the sold-at price ratio.

    [Fact]
    public void AcceleratingTrend_SoldCheaperRecently_TrendNotTrusted_Holds()
    {
        // Last week sold at 25% off vs 10% over the quarter → r = 0.75/0.90 = 0.833 < 0.97. The velocity
        // jump is largely the deeper discount, not organic demand — the shave must NOT fire.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, ksStock: 60,
            qty7: 15, qty14: 25, qty30: 40, qty90: 98,
            disc7: 0.25m, disc14: 0.10m, disc30: 0.10m, disc90: 0.10m);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal("SELL_THROUGH_HOLD", vote!.ReasonCode);
        Assert.Equal(80m, vote.SuggestedPrice);          // held at 20% off — no trend shave
        Assert.Contains("not trusted", vote.ReasonText);
    }

    [Fact]
    public void AcceleratingTrend_PricesFlat_ShavesDiscount()
    {
        // Same velocity acceleration but the sold-at discount held at 20% both windows → r = 1.0 → trusted.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, ksStock: 60,
            qty7: 15, qty14: 25, qty30: 40, qty90: 98,
            disc7: 0.20m, disc14: 0.20m, disc30: 0.20m, disc90: 0.20m);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal("SELL_THROUGH_FAST", vote!.ReasonCode);
        Assert.Equal(83m, vote.SuggestedPrice);          // 20% − 3pp accel = 17% off anchor 100
        Assert.Contains("accelerating", vote.ReasonText);
    }

    [Fact]
    public void AcceleratingTrend_SoldDearerRecently_OneSidedPass_ShavesDiscount()
    {
        // Last week sold at 15% off vs 25% over the quarter → r = 0.85/0.75 = 1.13 > 1. Demand accelerated
        // DESPITE a price rise, so the organic trend is real (if anything understated) — let the shave fire.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, ksStock: 60,
            qty7: 15, qty14: 25, qty30: 40, qty90: 98,
            disc7: 0.15m, disc14: 0.25m, disc30: 0.25m, disc90: 0.25m);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal("SELL_THROUGH_FAST", vote!.ReasonCode);
        Assert.Equal(83m, vote.SuggestedPrice);          // 20% − 3pp accel = 17% off anchor 100
        Assert.Contains("accelerating", vote.ReasonText);
    }

    [Fact]
    public void DeceleratingTrend_SoldDearerRecently_TrendNotTrusted_Holds()
    {
        // ≈20 units at ≈0.64/day → ≈31 days (hold). Baseline 88 units → μ ≈ 7.4 expected last week;
        // observing 2 is z ≈ −2.0 → a trustworthy deceleration. BUT last week sold at 10% off vs 30% over
        // the quarter → r = 0.90/0.70 = 1.29 > 1/0.97: the slowdown is the ended promo, not collapsing
        // demand — the extra markdown must NOT fire.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, ksStock: 20,
            qty7: 2, qty14: 14, qty30: 30, qty90: 90,
            disc7: 0.10m, disc14: 0.30m, disc30: 0.30m, disc90: 0.30m);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal("SELL_THROUGH_HOLD", vote!.ReasonCode);
        Assert.Equal(80m, vote.SuggestedPrice);          // held at 20% off — no trend deepening
        Assert.Contains("not trusted", vote.ReasonText);
    }

    [Fact]
    public void LowCountTrend_NoStatisticalPower_TrendSuppressed()
    {
        // 0/1/2/…/5 units: the baseline (5 in days 8–90) predicts μ = 5×7/83 ≈ 0.4 sales last week, so 0
        // last week is Poisson noise, not deceleration. Pre-fix, Qty90 − Qty7 = 5 cleared the crude ≥5 gate
        // and V7/V90 = 0 fired a spurious +3pp deepening (→ €81). Now the power gate (μ < 5) suppresses the
        // trend entirely, so only the very-slow level curve applies (+6pp).
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 90m, ksStock: 4,
            qty7: 0, qty14: 1, qty30: 2, qty60: 3, qty90: 5);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal("SELL_THROUGH_SLOW", vote!.ReasonCode);
        Assert.Equal(84m, vote.SuggestedPrice);          // 10% + 6pp very-slow only; NO extra −3pp decel (would be €81)
        Assert.DoesNotContain("decelerat", vote.ReasonText);
    }

    [Fact]
    public void FreshlyStockedBurst_NoFalseAccelerationTrend()
    {
        // Just came into stock: all 90-day sales are in the last week (Qty7 == Qty90), so the baseline
        // (days 8–90) is empty → μ = 0, no power. The trend modifier must NOT fire — pre-fix, V7/V90 =
        // 90/7 ≈ 12.9 falsely read as accelerating and shaved the discount. ~40 days of stock → hold.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, ksStock: 20,
            qty7: 5, qty14: 5, qty30: 5, qty90: 5);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal("SELL_THROUGH_HOLD", vote!.ReasonCode);
        Assert.Equal(80m, vote.SuggestedPrice);          // 20% off anchor 100, held — no trend nudge
        Assert.DoesNotContain("accelerating", vote.ReasonText);
    }

    [Fact]
    public void ZeroVelocity_Silent_DeadStockOwnsIt()
    {
        var ctx = TestData.Ctx(ksStock: 50); // all qty zero
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void ZeroStock_Silent()
    {
        var ctx = TestData.Ctx(ksStock: 0, supplierStock: 0, qty7: 7);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void HugeSupplierStock_Ignored_NoFalseMarkdown()
    {
        // 10 local units selling 2/day = 5 days of OUR stock → shave, not markdown — even though the
        // supplier holds 10,000 (which on total stock would read as 5,000 days → deep markdown).
        // Cost 75 → 11.8% thin margin keeps it on the fast-shave branch, not remove.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 85m, pptcv: 75m,
            ksStock: 10, supplierStock: 10000, qty7: 14, qty14: 28, qty30: 60, qty90: 180);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal("SELL_THROUGH_FAST", vote!.ReasonCode);  // fast on local stock, never SELL_THROUGH_SLOW
        Assert.Equal(90m, vote.SuggestedPrice);               // 15% → 10% off anchor 100 (a trim, not a markdown)
    }

    [Fact]
    public void SupplierOnly_Silent_NotOursToClear()
    {
        // No local stock, only supplier stock — even if it's selling, sell-through stays out of it
        // (the supplier-only-no-markdown guardrail owns this case).
        var ctx = TestData.Ctx(ksStock: 0, supplierStock: 500, qty7: 14, qty30: 60, qty90: 180);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void DeadStock_Silent_EvenWithTrailingVelocity_AtAShortWindow()
    {
        // Dead stock is the paced dead-stock lane's exclusively. At a SHORT band no-sale window a SKU can
        // qualify as dead (no sale in the window) yet still show trailing velocity from older windows; the
        // days-to-sellout guard below would let sell-through in, so the explicit IsDeadStock gate is what
        // keeps it out (a co-vote would dilute the markdown). At the default 90-day window a dead SKU has
        // zero velocity, so this is only reachable with a short window.
        var band = TestData.Band(deadStockNoSaleWindowDays: 7);
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, ksStock: 10,
            qty7: 0, qty14: 10, qty30: 20, qty90: 40, band: band);
        Assert.True(ctx.IsDeadStock);            // dead by the 7-day window
        Assert.NotNull(ctx.DaysToSelloutLocal);  // …but still has trailing velocity
        Assert.Null(_algorithm.Evaluate(ctx));   // sell-through defers to the dead-stock lane
    }
}

public class ElasticityTests
{
    private readonly PriceElasticityHeuristicAlgorithm _algorithm = new();

    [Fact]
    public void ElasticCoefficient_VotesProfitMaxPrice()
    {
        // E=-2 → markup E/(E+1)=2 over the all-in cost 40 = 80.00 (cost is VAT-inclusive, so P* is a price).
        // Tight SE → confidently elastic.
        var ctx = TestData.Ctx(oldPrice: 200m, currentPrice: 75m, pptcv: 40m, qty90: 12, elasticity: -2.0m, elasticityStdError: 0.1m);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(80.00m, vote!.SuggestedPrice);
        Assert.Equal("ELASTIC_OPTIMAL", vote.ReasonCode);
    }

    [Fact]
    public void HighlyElastic_VotesNearerCost_AMarkdown()
    {
        // E=-5 → markup 1.25 over the all-in cost 40 = 50.00, below the 90 current → markdown.
        var ctx = TestData.Ctx(oldPrice: 200m, currentPrice: 90m, pptcv: 40m, qty90: 12, elasticity: -5.0m, elasticityStdError: 0.2m);

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(50.00m, vote!.SuggestedPrice);
        Assert.True(vote.SuggestedPrice < ctx.CurrentPrice);
    }

    [Fact]
    public void NoisyNearUnitElastic_StaysSilent()
    {
        // E = -1.18 but a wide SE → optimistic CI end (-1.18 + 1.2816·0.4 = -0.67) isn't < -1, so we're
        // NOT confident it's elastic → silent (avoids the exploding 6× markup).
        var ctx = TestData.Ctx(currentPrice: 75m, pptcv: 40m, qty90: 12, elasticity: -1.18m, elasticityStdError: 0.4m);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void NoStandardError_StaysSilent()
    {
        // Without an SE we can't confirm E < -1 (e.g. pre-refit rows) → silent.
        var ctx = TestData.Ctx(currentPrice: 75m, pptcv: 40m, qty90: 12, elasticity: -2.0m, elasticityStdError: null);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void ConfidentBarelyElastic_OptimalCappedAtAnchor()
    {
        // E = -1.18 with a tight SE IS confident (-1.18 + 1.2816·0.05 = -1.12 ≤ -1). Markup 6.56× cost
        // = 262 would exceed everything, but it's capped at the anchor (100) — never above the reference.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 40m, qty90: 12, elasticity: -1.18m, elasticityStdError: 0.05m);
        var vote = _algorithm.Evaluate(ctx);
        Assert.NotNull(vote);
        Assert.Equal(100m, vote!.SuggestedPrice);
    }

    [Fact]
    public void ConfidentBarelyElastic_CurrentAboveAnchor_CapsAtCurrent_NotAnchor()
    {
        // Same exploding markup, but the SKU already sells at 90 — above its anchor 80. The cap is the
        // higher of the two, so the vote holds at the current 90 rather than proposing a spurious
        // "discount" down to the anchor 80 (which the elasticity optimum never wanted — it wanted higher).
        var ctx = TestData.Ctx(oldPrice: 100m, anchorPrice: 80m, currentPrice: 90m, pptcv: 40m,
            qty90: 12, elasticity: -1.18m, elasticityStdError: 0.05m);
        var vote = _algorithm.Evaluate(ctx);
        Assert.NotNull(vote);
        Assert.Equal(90m, vote!.SuggestedPrice);
    }

    [Fact]
    public void ElasticButNoCost_StaysSilent()
    {
        var ctx = TestData.Ctx(currentPrice: 75m, pptcv: null, qty90: 12, elasticity: -2.0m);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void InelasticCoefficient_StaysSilent_OwnedByOtherAdvisors()
    {
        // -0.5 is a valid, well-fit elasticity but inelastic → Algo 5 defers to #8/#6.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 75m, qty90: 12, elasticity: -0.5m);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void UnitElastic_StaysSilent()
    {
        var ctx = TestData.Ctx(currentPrice: 75m, qty90: 12, elasticity: -1.0m);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void NoUsableCoefficient_StaysSilent()
    {
        var ctx = TestData.Ctx(qty30: 30, qty90: 90, elasticity: null);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void WrongSignCoefficient_StaysSilent()
    {
        var ctx = TestData.Ctx(currentPrice: 75m, elasticity: 0.5m);
        Assert.Null(_algorithm.Evaluate(ctx));
    }
}

public class DeadStockTests
{
    private readonly DeadStockMarkdownAlgorithm _algorithm = new();

    [Fact]
    public void FreshDeadStock_StartsAtTenPercent()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, ksStock: 10, qty90: 0);
        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(90m, vote!.SuggestedPrice);
        Assert.Equal(0.8m, vote.Confidence);
    }

    [Fact]
    public void LongObservedStreak_DoesNotCatchUpJump()
    {
        // THE pacing rule: 200 streak days with no persisted pacing state is still just the OPENING
        // drop (10%) — the old absolute curve would have leapt straight to 80% off. Depth now comes
        // only from persisted period-by-period steps.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, ksStock: 10,
            qty90: 0, zeroSaleStreakDays: 200);
        Assert.Equal(90m, _algorithm.Evaluate(ctx)!.SuggestedPrice);
    }

    [Fact]
    public void EngagementFromExistingDiscount_ContinuesOneStepDeeper()
    {
        // Already at -35% when it turns dead: the walk continues from there (-40%), no jump, no reset.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 65m, ksStock: 10, qty90: 0);
        Assert.Equal(60m, _algorithm.Evaluate(ctx)!.SuggestedPrice);
    }

    [Fact]
    public void NeverShrinksAnExistingDeeperDiscount()
    {
        // Persisted target -25%, but a human already marked it down to -40%: hold the deeper price.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 60m, ksStock: 10, qty90: 0,
            deadStockPacedTarget: 0.25m, deadStockPacedCaughtUpStreak: null, zeroSaleStreakDays: 30);
        Assert.Equal(60m, _algorithm.Evaluate(ctx)!.SuggestedPrice);
    }

    [Fact]
    public void Frozen_NewlyDead_WithNoPersistedTarget_Abstains()
    {
        // A SKU that turns dead on a frozen (off-tick) daily run must NOT open a fresh markdown — it holds
        // its current price until the next 15-day slow-lane tick engages the walk.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, ksStock: 10, qty90: 0,
            zeroSaleStreakDays: 95, slowLanesFrozen: true);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void Frozen_WithAppliedTarget_HoldsThePriceWithoutStepping()
    {
        // Applied at -25% with a full period elapsed (would step to -30% on a tick). Frozen, it re-emits
        // the held -25% (price 75) and the reason says so — the price stays put for the whole window.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 75m, ksStock: 10, qty90: 0,
            zeroSaleStreakDays: 109, deadStockPacedTarget: 0.25m, deadStockPacedCaughtUpStreak: 95,
            slowLanesFrozen: true);
        var vote = _algorithm.Evaluate(ctx);
        Assert.NotNull(vote);
        Assert.Equal(75m, vote!.SuggestedPrice);
        Assert.Equal(0.8m, vote.Confidence);
        Assert.Contains("frozen", vote.ReasonText);
    }

    [Fact]
    public void StartDiscount_IsPerBand()
    {
        // Same SKU, different bands: a 10% band opens at 90, a 5% band at 95 (the user's 0-10 vs 50-100 case).
        var ctx10 = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, ksStock: 10, qty90: 0,
            band: TestData.Band(deadStockStartDiscountPct: 10m));
        Assert.Equal(90m, _algorithm.Evaluate(ctx10)!.SuggestedPrice);

        var ctx5 = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, ksStock: 10, qty90: 0,
            band: TestData.Band(deadStockStartDiscountPct: 5m));
        Assert.Equal(95m, _algorithm.Evaluate(ctx5)!.SuggestedPrice);
    }

    [Fact]
    public void StepAndPeriod_ArePerBand()
    {
        // Band: +10pp every 7 days. Applied at -20% since streak 10, streak now 17 (period elapsed)
        // → step to -30% → 70.
        var band = TestData.Band(deadStockStartDiscountPct: 20m, deadStockStepDiscountPct: 10m, deadStockPeriodDays: 7);
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, ksStock: 10, qty90: 0,
            zeroSaleStreakDays: 17, band: band,
            deadStockPacedTarget: 0.20m, deadStockPacedCaughtUpStreak: 10);
        Assert.Equal(70m, _algorithm.Evaluate(ctx)!.SuggestedPrice);
    }

    [Fact]
    public void AnySaleInNinetyDays_NoOpinion()
    {
        var ctx = TestData.Ctx(ksStock: 10, qty90: 1);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void ConfigurableWindow_ShorterWindow_FiresEvenWithOlderSale()
    {
        // Window narrowed to 30 days: a sale 60–90 days ago no longer protects the SKU — only the
        // last 30 days matter, and those are dry, so it's dead stock under the tighter definition.
        var band = TestData.Band(deadStockNoSaleWindowDays: 30);
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, ksStock: 10,
            qty7: 0, qty14: 0, qty30: 0, qty60: 5, qty90: 5, band: band);
        var vote = _algorithm.Evaluate(ctx);
        Assert.NotNull(vote);
        Assert.Equal(90m, vote!.SuggestedPrice); // 10% start markdown
    }

    [Fact]
    public void ConfigurableWindow_SaleInsideWindow_NoOpinion()
    {
        // Same 30-day window, but there was a sale in the last 30 days → not dead, stays silent.
        var band = TestData.Band(deadStockNoSaleWindowDays: 30);
        var ctx = TestData.Ctx(ksStock: 10, qty30: 2, qty60: 2, qty90: 2, band: band);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void ConfigurableWindow_OffGridValue_SnapsToNearestSourceWindow()
    {
        // 50 days is off the source grid {7,14,30,60,90}; nearest is 60, so the 60-day qty governs.
        var band = TestData.Band(deadStockNoSaleWindowDays: 50);
        var dryCtx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, ksStock: 10,
            qty60: 0, qty90: 8, band: band);
        Assert.Equal(60, dryCtx.DeadStockWindowDays);
        Assert.NotNull(_algorithm.Evaluate(dryCtx)); // no sales in 60d → fires despite a 90d sale

        var soldCtx = TestData.Ctx(ksStock: 10, qty60: 3, qty90: 8, band: band);
        Assert.Null(_algorithm.Evaluate(soldCtx)); // a sale within 60d → silent
    }

    [Fact]
    public void SupplierOnlyDeadStock_NoOpinion_WeDontDiscountStockWeDontHold()
    {
        // Zero sales in 90d, but every unit sits in a supplier warehouse — not ours to mark down.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m,
            ksStock: 0, supplierStock: 50, qty90: 0, zeroSaleStreakDays: 60);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void MixedDeadStock_FiresOnLocallyHeldUnits()
    {
        // Some local stock plus some supplier stock, all dead → we still mark down what we hold.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m,
            ksStock: 5, supplierStock: 50, qty90: 0);
        Assert.Equal(90m, _algorithm.Evaluate(ctx)!.SuggestedPrice);
    }

    [Fact]
    public void FreshlyStockedPreOrder_NoOpinion_HasNotHadAChanceToSell()
    {
        // Just arrived (oldest unit 5 days old) with no 90d sales — not dead, just freshly stocked.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, ksStock: 10, qty90: 0,
            oldestUnitAgeDays: 5);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void StockOlderThanMinAge_StillMarksDown()
    {
        // Oldest unit is well past the 90-day freshness gate → genuinely stale, so it fires as normal.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, ksStock: 10, qty90: 0,
            oldestUnitAgeDays: 180);
        Assert.Equal(90m, _algorithm.Evaluate(ctx)!.SuggestedPrice);
    }

    [Fact]
    public void StockYoungerThanDefaultGate_NoOpinion()
    {
        // The default gate is 90 days (per the stock-age sales analysis: ~90% of stock sales happen
        // within 90 days of check-in) — a 60-day-old unit still has a real chance to sell naturally.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, ksStock: 10, qty90: 0,
            oldestUnitAgeDays: 60);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void StockAgeExactlyAtThreshold_MarksDown()
    {
        // The gate is "younger than" the minimum (default 90), so exactly 90 days qualifies as dead.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, ksStock: 10, qty90: 0,
            oldestUnitAgeDays: 90);
        Assert.NotNull(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void UnknownStockAge_FallsBackToPriorBehaviour_MarksDown()
    {
        // No WMS check-in row (null age): we can't confirm freshness, so behave as before and mark down.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, ksStock: 10, qty90: 0,
            oldestUnitAgeDays: null);
        Assert.Equal(90m, _algorithm.Evaluate(ctx)!.SuggestedPrice);
    }

    [Fact]
    public void PerBandMinStockAge_LongerGate_SparesStockYoungerThanTheBandSetting()
    {
        // Band raises the freshness gate to 180 days: a 120-day-old unit is now "still fresh" for this
        // band and is spared, even though it would have been marked down under the default 90-day gate.
        var band = TestData.Band(deadStockMinStockAgeDays: 180);
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, ksStock: 10, qty90: 0,
            oldestUnitAgeDays: 120, band: band);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void StageDoesNotChangeTheMarkdownCurve()
    {
        // The age tiers live in the guardrail's floor, NOT in the markdown pacing: a stage-1 and a
        // stage-3 SKU with the same pacing state get the identical vote price (default band = equal steps).
        var stage1 = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, ksStock: 10, qty90: 0,
            zeroSaleStreakDays: 28, deadObservedDays: 100);
        var stage3 = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, ksStock: 10, qty90: 0,
            zeroSaleStreakDays: 28, deadObservedDays: 400);

        Assert.Equal(_algorithm.Evaluate(stage1)!.SuggestedPrice, _algorithm.Evaluate(stage3)!.SuggestedPrice);
    }
}

/// <summary>
/// The pacing state machine itself (<see cref="DeadStockMarkdownPacer"/>): every ctx here is a dead
/// SKU (anchor 100, KS stock, zero sales); what varies is the live discount, the persisted state and
/// the streak. Defaults: start 10%, step 5pp, period 14.
/// </summary>
public class DeadStockPacerTests
{
    private static SkuContext Dead(decimal currentPrice, int streak = 90,
        decimal? target = null, int? caught = null, PriceBandConfig? band = null, bool frozen = false) =>
        TestData.Ctx(oldPrice: 100m, currentPrice: currentPrice, ksStock: 10, qty90: 0,
            zeroSaleStreakDays: streak, band: band,
            deadStockPacedTarget: target, deadStockPacedCaughtUpStreak: caught, slowLanesFrozen: frozen);

    [Fact]
    public void Engagement_FromCleanPrice_OpensAtBandStart()
    {
        var paced = DeadStockMarkdownPacer.Next(Dead(100m));
        Assert.Equal(0.10m, paced.TargetDiscount);
        Assert.Null(paced.CaughtUpStreak);                      // clock starts only once applied
        Assert.Equal(DeadStockPacedPhase.Engaged, paced.Phase);
    }

    [Fact]
    public void Engagement_FromExistingDiscount_OpensOneStepDeeper()
    {
        // -20% live → open at -25% (current + step beats the 10% start).
        var paced = DeadStockMarkdownPacer.Next(Dead(80m));
        Assert.Equal(0.25m, paced.TargetDiscount);
        Assert.Equal(DeadStockPacedPhase.Engaged, paced.Phase);
    }

    [Fact]
    public void Engagement_LongStreak_DoesNotJump()
    {
        // Streak 300 without state is still just the opening step — depth never comes from the streak.
        var paced = DeadStockMarkdownPacer.Next(Dead(80m, streak: 300));
        Assert.Equal(0.25m, paced.TargetDiscount);
    }

    [Fact]
    public void PendingProposal_IsStable_UntilTheShopApplies()
    {
        // Target -25% persisted, shop still at -20%: re-propose the same target day after day, the
        // period clock never starts (streak growth alone must not deepen an unapplied proposal).
        foreach (var streak in new[] { 91, 100, 150 })
        {
            var paced = DeadStockMarkdownPacer.Next(Dead(80m, streak, target: 0.25m));
            Assert.Equal(0.25m, paced.TargetDiscount);
            Assert.Null(paced.CaughtUpStreak);
            Assert.Equal(DeadStockPacedPhase.Pending, paced.Phase);
        }
    }

    [Fact]
    public void PendingProposal_ErodedBelowStart_ReopensAtStartDiscount()
    {
        // Regression: an engagement opened at the 10% start discount, but the proposal was never
        // applied, so an earlier build eroded the persisted target down to one-step-below-live (5%).
        // The next run must DISREGARD that eroded target and re-open at the band's start discount —
        // the start discount holds until the markdown actually reaches the shop.
        var paced = DeadStockMarkdownPacer.Next(Dead(100m, streak: 120, target: 0.05m));
        Assert.Equal(0.10m, paced.TargetDiscount);
        Assert.Null(paced.CaughtUpStreak);
        Assert.Equal(DeadStockPacedPhase.Pending, paced.Phase);

        // …and it stays at the start discount run after run while still un-applied (no decay, no drift).
        var next = DeadStockMarkdownPacer.Next(Dead(100m, streak: 150, target: paced.TargetDiscount));
        Assert.Equal(0.10m, next.TargetDiscount);
        Assert.Equal(DeadStockPacedPhase.Pending, next.Phase);
    }

    [Fact]
    public void AppliedTarget_StartsThePeriodClock()
    {
        // Shop now at -25% (the target): clock starts at today's streak, target holds.
        var paced = DeadStockMarkdownPacer.Next(Dead(75m, streak: 95, target: 0.25m));
        Assert.Equal(0.25m, paced.TargetDiscount);
        Assert.Equal(95, paced.CaughtUpStreak);
        Assert.Equal(DeadStockPacedPhase.Waiting, paced.Phase);
    }

    [Fact]
    public void AppliedWithinRoundingTolerance_CountsAsApplied()
    {
        // Charm rounding left the shop at -23.5% against a -25% target (within step/2 = 2.5pp): the
        // clock must still start, or rounding would freeze the walk forever.
        var paced = DeadStockMarkdownPacer.Next(Dead(76.50m, streak: 95, target: 0.25m));
        Assert.Equal(95, paced.CaughtUpStreak);
        Assert.Equal(DeadStockPacedPhase.Waiting, paced.Phase);
    }

    [Fact]
    public void PeriodNotElapsed_Holds_AndKeepsTheClock()
    {
        var paced = DeadStockMarkdownPacer.Next(Dead(75m, streak: 100, target: 0.25m, caught: 95));
        Assert.Equal(0.25m, paced.TargetDiscount);
        Assert.Equal(95, paced.CaughtUpStreak);
        Assert.Equal(DeadStockPacedPhase.Waiting, paced.Phase);
    }

    [Fact]
    public void PeriodElapsedAtAppliedLevel_StepsOneDeeper_AndResetsTheClock()
    {
        // Applied at -25% since streak 95; streak now 109 (14 later) → -30%, clock reset.
        var paced = DeadStockMarkdownPacer.Next(Dead(75m, streak: 109, target: 0.25m, caught: 95));
        Assert.Equal(0.30m, paced.TargetDiscount);
        Assert.Null(paced.CaughtUpStreak);
        Assert.Equal(DeadStockPacedPhase.Stepped, paced.Phase);
    }

    [Fact]
    public void PeriodCountsTheClockStartDay_StepsOnTheBoundary()
    {
        // The clock-start day is applied zero-sale day 1 (the price was live even earlier — the snapshot
        // only observes a push the day after it lands). Applied at streak 95, streak 108 → 14 observed
        // days INCLUSIVE against period 14 → steps. The exclusive interval (108-95 = 13) would hold —
        // that off-by-one is what produced 0 steps on the 2026-08-01 prod tick.
        var paced = DeadStockMarkdownPacer.Next(Dead(75m, streak: 108, target: 0.25m, caught: 95));
        Assert.Equal(0.30m, paced.TargetDiscount);
        Assert.Null(paced.CaughtUpStreak);
        Assert.Equal(DeadStockPacedPhase.Stepped, paced.Phase);
    }

    [Fact]
    public void OneObservedDayShortOfThePeriod_StillHolds()
    {
        // 13 observed applied days (95..107 inclusive) against period 14: hold — inclusive counting
        // must not overshoot into stepping a day early.
        var paced = DeadStockMarkdownPacer.Next(Dead(75m, streak: 107, target: 0.25m, caught: 95));
        Assert.Equal(0.25m, paced.TargetDiscount);
        Assert.Equal(95, paced.CaughtUpStreak);
        Assert.Equal(DeadStockPacedPhase.Waiting, paced.Phase);
    }

    [Fact]
    public void FifteenDayPeriod_StepsOnTheFifteenDaySlowLaneTick()
    {
        // The 2026-08-01 prod scenario verbatim: markdown applied on the first snapshot after the
        // 7/17 push (clock start = streak 1), tick fires at streak 15, band period 15. 15 observed
        // applied days → the tick must step, not report "next step after 1 more zero-sale days".
        var band = TestData.Band(deadStockPeriodDays: 15);
        var paced = DeadStockMarkdownPacer.Next(Dead(80m, streak: 15, target: 0.20m, caught: 1, band: band));
        Assert.Equal(0.25m, paced.TargetDiscount);
        Assert.Equal(DeadStockPacedPhase.Stepped, paced.Phase);
    }

    [Fact]
    public void HumanWentDeeper_NextStepBasesOffTheLivePrice()
    {
        // Target was -25% but a human cleared it to -40%; period elapsed → step from the LIVE level
        // (-45%), never back over the human's markdown.
        var paced = DeadStockMarkdownPacer.Next(Dead(60m, streak: 109, target: 0.25m, caught: 95));
        Assert.Equal(0.45m, paced.TargetDiscount);
        Assert.Equal(DeadStockPacedPhase.Stepped, paced.Phase);
    }

    [Fact]
    public void HumanRaisedThePrice_TargetReanchors_NoJumpBack()
    {
        // Target -25% persisted, but a human RAISED the shop price back to -10%: the target re-anchors
        // to one step below the live price (-15%) instead of re-proposing the old -25% leap.
        var paced = DeadStockMarkdownPacer.Next(Dead(90m, streak: 120, target: 0.25m, caught: 95));
        Assert.Equal(0.15m, paced.TargetDiscount);
        Assert.Null(paced.CaughtUpStreak);
    }

    [Fact]
    public void SameDayRerun_AfterStep_DoesNotDoubleStep()
    {
        // Run 1 stepped to -30% (clock reset). A same-day run 2 sees the unapplied -30%: Pending, no
        // further step — pacing is idempotent per snapshot day.
        var paced = DeadStockMarkdownPacer.Next(Dead(75m, streak: 109, target: 0.30m, caught: null));
        Assert.Equal(0.30m, paced.TargetDiscount);
        Assert.Equal(DeadStockPacedPhase.Pending, paced.Phase);
    }

    [Fact]
    public void TargetIsCappedAtNinetyNinePercent()
    {
        var paced = DeadStockMarkdownPacer.Next(Dead(2m, streak: 500, target: 0.98m, caught: 480));
        Assert.Equal(0.99m, paced.TargetDiscount);
    }

    [Fact]
    public void FloorBlockedWalk_ConvergesOneStepBelowLive_NoOscillation()
    {
        // The guardrail floor stopped the shop at -42% while the persisted target had reached -50%:
        // the target re-anchors to live+step (-47%) and then STAYS there run after run — the walk
        // parks one step past the floor instead of oscillating or drifting deeper.
        var first = DeadStockMarkdownPacer.Next(Dead(58m, streak: 200, target: 0.50m));
        Assert.Equal(0.47m, first.TargetDiscount);
        Assert.Null(first.CaughtUpStreak);              // floor keeps the live price short of the target

        var second = DeadStockMarkdownPacer.Next(Dead(58m, streak: 230, target: first.TargetDiscount));
        Assert.Equal(0.47m, second.TargetDiscount);
        Assert.Equal(DeadStockPacedPhase.Pending, second.Phase);
    }

    [Fact]
    public void PerBandStepAndPeriod_DriveThePace()
    {
        // Band with +10pp per 7 days (stage 1): applied at -20% since streak 10, streak 17 → -30%.
        var band = TestData.Band(deadStockStepDiscountPct: 10m, deadStockPeriodDays: 7);
        var paced = DeadStockMarkdownPacer.Next(Dead(80m, streak: 17, target: 0.20m, caught: 10, band: band));
        Assert.Equal(0.30m, paced.TargetDiscount);
        Assert.Equal(DeadStockPacedPhase.Stepped, paced.Phase);
    }

    [Fact]
    public void StepAndPeriod_AreTheCurrentStages_NotStage1s()
    {
        // Stage 3 (dead 400 observed days ≥ 360) has its OWN step 12pp / period 7d, distinct from stage
        // 1's 5/14. Applied at -30% since streak 100, streak 107 (period 7 elapsed) → step 12pp → -42%.
        var band = TestData.Band(
            deadStockStepDiscountPct: 5m, deadStockPeriodDays: 14,
            deadStockStage3StepDiscountPct: 12m, deadStockStage3PeriodDays: 7);
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 70m, ksStock: 10, qty90: 0,
            zeroSaleStreakDays: 107, deadObservedDays: 400, band: band,
            deadStockPacedTarget: 0.30m, deadStockPacedCaughtUpStreak: 100);
        var paced = DeadStockMarkdownPacer.Next(ctx);
        Assert.Equal(0.42m, paced.TargetDiscount);
        Assert.Equal(DeadStockPacedPhase.Stepped, paced.Phase);
    }

    [Fact]
    public void Stage4_DoesNotStep_WhenItsOwnPeriodHasNotElapsed()
    {
        // Same applied gap (7 days) but stage 4's period is 30d → not yet: hold, don't step.
        var band = TestData.Band(deadStockStage4PeriodDays: 30, deadStockStage4StepDiscountPct: 10m);
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 60m, ksStock: 10, qty90: 0,
            zeroSaleStreakDays: 1107, deadObservedDays: 1200, band: band,
            deadStockPacedTarget: 0.40m, deadStockPacedCaughtUpStreak: 1100);
        var paced = DeadStockMarkdownPacer.Next(ctx);
        Assert.Equal(0.40m, paced.TargetDiscount);
        Assert.Equal(DeadStockPacedPhase.Waiting, paced.Phase);
    }

    // ---- Slow-lane freeze: between 15-day ticks the STEP is suppressed, but the applied/pending
    // branches (hold, re-anchor, never-shrink) still behave correctly. Same inputs as the non-frozen
    // tests above so the contrast is explicit.

    [Fact]
    public void Frozen_AppliedAndPeriodElapsed_HoldsTargetInsteadOfStepping()
    {
        // The non-frozen twin (PeriodElapsedAtAppliedLevel_...) steps -25% → -30%. Frozen, the step is
        // gated off: hold at -25% (Waiting), clock preserved — the price stays put for the whole window.
        var paced = DeadStockMarkdownPacer.Next(Dead(75m, streak: 109, target: 0.25m, caught: 95, frozen: true));
        Assert.Equal(0.25m, paced.TargetDiscount);
        Assert.Equal(95, paced.CaughtUpStreak);
        Assert.Equal(DeadStockPacedPhase.Waiting, paced.Phase);
    }

    [Fact]
    public void Frozen_HumanRaisedThePrice_StillReanchorsOneStepBelowLive()
    {
        // Freezing must NOT blindly re-emit the stored target: with the shop raised back to -10%, the
        // pacer takes the (ungated) Pending branch and re-anchors to one step below live (-15%), exactly
        // like the non-frozen HumanRaisedThePrice test — no -25% jump-back.
        var paced = DeadStockMarkdownPacer.Next(Dead(90m, streak: 120, target: 0.25m, caught: 95, frozen: true));
        Assert.Equal(0.15m, paced.TargetDiscount);
        Assert.Equal(DeadStockPacedPhase.Pending, paced.Phase);
    }

    [Fact]
    public void Frozen_HumanWentDeeper_HoldsTheDeeperLive_NoStepNoRaise()
    {
        // A human cleared to -40% (deeper than the -25% target). Frozen: the step is gated, so it does not
        // step to -45%; NEVER-SHRINK still holds the deeper live -40% (never proposes a raise).
        var paced = DeadStockMarkdownPacer.Next(Dead(60m, streak: 109, target: 0.25m, caught: 95, frozen: true));
        Assert.Equal(0.40m, paced.TargetDiscount);
        Assert.Equal(DeadStockPacedPhase.Waiting, paced.Phase);
    }
}

/// <summary>The stage-4 age tier's deeper cost floor in the guardrail tunnel.</summary>
public class DeadStockStage4FloorTests
{
    private readonly GuardrailService _guardrails = new();

    [Fact]
    public void Stage4_ClampsToTheStage4CostFloor()
    {
        // Dead 1200 observed days → stage 4, floor 40% of cost. Cost 40 → floor 16. A paced price of 10
        // (below the stage floor) is lifted to 16; the margin floor is pierced (DeadStockFloorRelaxed).
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, pptcv: 40m, ksStock: 10, qty90: 0,
            deadObservedDays: 1200);
        var result = _guardrails.Clamp(ctx, 10m);
        Assert.Equal(16m, result.Price);
        Assert.Contains(GuardrailFlags.DeadStockFloorRelaxed, result.Flags);
    }

    [Fact]
    public void Stage4_FloorIsDeeperThanStage3()
    {
        // Same SKU, one dead 400 observed days (stage 3) and one 1200 (stage 4): the stage-4 floor sits lower.
        var s3 = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, pptcv: 40m, ksStock: 10, qty90: 0, deadObservedDays: 400);
        var s4 = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, pptcv: 40m, ksStock: 10, qty90: 0, deadObservedDays: 1200);
        Assert.True(_guardrails.Clamp(s4, 1m).Price < _guardrails.Clamp(s3, 1m).Price);
        Assert.Equal(24m, _guardrails.Clamp(s3, 1m).Price);   // 60% × 40
        Assert.Equal(16m, _guardrails.Clamp(s4, 1m).Price);   // 40% × 40
    }
}

/// <summary>
/// The shared dead-stock stage definition on SkuContext — the observed-no-sale-duration tier the
/// guardrail floor and the persisted ProposedPrice.DeadStockStage label both key off.
/// </summary>
public class DeadStockStageTests
{
    [Fact]
    public void NotDead_IsNone()
    {
        // Each of the four gate conditions alone keeps the stage at None.
        Assert.Equal(DeadStockStage.None, TestData.Ctx(ksStock: 10, qty90: 3, oldestUnitAgeDays: 400).DeadStockStage);          // selling
        Assert.Equal(DeadStockStage.None, TestData.Ctx(ksStock: 10, qty90: 0, oldestUnitAgeDays: 5).DeadStockStage);            // freshly stocked
        Assert.Equal(DeadStockStage.None, TestData.Ctx(ksStock: 0, supplierStock: 50, qty90: 0, oldestUnitAgeDays: 400).DeadStockStage); // supplier-only
        Assert.Equal(DeadStockStage.None, TestData.Ctx(ksStock: 10, qty90: 0, oldestUnitAgeDays: 400, isNewProduct: true).DeadStockStage); // platform-new
    }

    [Theory]
    [InlineData(0, 1)]     // just went dead (observed-dead 0) → stage 1
    [InlineData(90, 1)]
    [InlineData(179, 1)]
    [InlineData(180, 2)]   // stage-2 threshold inclusive
    [InlineData(359, 2)]
    [InlineData(360, 3)]   // stage-3 threshold inclusive
    [InlineData(1079, 3)]
    [InlineData(1080, 4)]  // stage-4 threshold inclusive
    [InlineData(5000, 4)]
    public void StageFollowsObservedDeadDays(int deadDays, int expectedStage)
    {
        var ctx = TestData.Ctx(ksStock: 10, qty90: 0, deadObservedDays: deadDays);
        Assert.Equal((DeadStockStage)expectedStage, ctx.DeadStockStage);
    }

    [Fact]
    public void StageThresholds_ArePerBand()
    {
        var band = TestData.Band(deadStockStage2AgeDays: 120, deadStockStage3AgeDays: 240, deadStockStage4AgeDays: 500);
        Assert.Equal(DeadStockStage.Stage2,
            TestData.Ctx(ksStock: 10, qty90: 0, deadObservedDays: 130, band: band).DeadStockStage);
        Assert.Equal(DeadStockStage.Stage3,
            TestData.Ctx(ksStock: 10, qty90: 0, deadObservedDays: 240, band: band).DeadStockStage);
        Assert.Equal(DeadStockStage.Stage4,
            TestData.Ctx(ksStock: 10, qty90: 0, deadObservedDays: 500, band: band).DeadStockStage);
    }
}

/// <summary>The dataset caveat: qty 0 with NULL discount history must never crash or mislead any algorithm.</summary>
public class ZeroVersusNullHandlingTests
{
    public static IEnumerable<object[]> AllAlgorithms() => new IPricingAlgorithm[]
    {
        new SellThroughAlgorithm(),
        new PriceElasticityHeuristicAlgorithm(),
        new DeadStockMarkdownAlgorithm(), new CrossDockMarkdownAlgorithm(),
    }.Select(a => new object[] { a });

    [Theory]
    [MemberData(nameof(AllAlgorithms))]
    public void NoSalesAndNullDiscounts_NoAlgorithmThrows(IPricingAlgorithm algorithm)
    {
        var ctx = TestData.Ctx(oldPrice: 49.99m, currentPrice: 39.99m, pptcv: 20m,
            ksStock: 25, qty7: 0, qty14: 0, qty30: 0, qty60: 0, qty90: 0,
            disc7: null, disc14: null, disc30: null, disc60: null, disc90: null,
            zeroSaleStreakDays: 10);

        var exception = Record.Exception(() => algorithm.Evaluate(ctx));
        Assert.Null(exception);
    }

    [Fact]
    public void NoSalesAndNullDiscounts_OnlyDeadStockVotes()
    {
        var ctx = TestData.Ctx(oldPrice: 49.99m, currentPrice: 49.99m, pptcv: 20m,
            ksStock: 25, zeroSaleStreakDays: 10);

        Assert.NotNull(new DeadStockMarkdownAlgorithm().Evaluate(ctx));
        Assert.Null(new PriceElasticityHeuristicAlgorithm().Evaluate(ctx));
        Assert.Null(new SellThroughAlgorithm().Evaluate(ctx));
        Assert.Null(new CrossDockMarkdownAlgorithm().Evaluate(ctx));   // locally held → not cross-dock's lane
    }
}

/// <summary>
/// Cross-dock (supplier-fulfilled) lane: KsStock == 0, SupplierStock &gt; 0. A sell-through/dead-stock
/// hybrid — hold the working price when selling, soft progressive markdown to the margin floor when not.
/// </summary>
public class CrossDockTests
{
    private readonly CrossDockMarkdownAlgorithm _algorithm = new();

    // ---- Eligibility ----------------------------------------------------------------------

    [Fact]
    public void LocallyHeldStock_Abstains()
    {
        // We hold it locally → SELL_THROUGH / DEAD_STOCK own this; cross-dock stays out.
        var ctx = TestData.Ctx(ksStock: 10, supplierStock: 50, qty90: 0);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void NoSupplierStock_Abstains()
    {
        var ctx = TestData.Ctx(ksStock: 0, supplierStock: 0, qty90: 5);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void NewProduct_Abstains()
    {
        // Held by the engine's new-product rule — never vote on it.
        var ctx = TestData.Ctx(ksStock: 0, supplierStock: 50, qty90: 0, isNewProduct: true);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    [Fact]
    public void NoCost_Abstains()
    {
        // No PPTCV → no margin floor to bound the markdown; abstain rather than vote unbounded.
        var ctx = TestData.Ctx(ksStock: 0, supplierStock: 50, qty90: 0, pptcv: null);
        Assert.Null(_algorithm.Evaluate(ctx));
    }

    // ---- Branch B: not selling (Qty90 == 0) — soft progressive tunnel ---------------------

    [Fact]
    public void NonSelling_FirstStep_AppliesGentleStartDiscount()
    {
        // anchor 100, no existing discount, streak 0 → schedule disc = 5% start → 95.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, pptcv: 45m,
            ksStock: 0, supplierStock: 50, qty90: 0, zeroSaleStreakDays: 0,
            band: TestData.Band(marginFloorPct: 10m));

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(95m, vote!.SuggestedPrice);          // 100 × (1 − 0.05)
        Assert.Equal("CROSS_DOCK_TUNNEL", vote.ReasonCode);
    }

    [Fact]
    public void NonSelling_DeepensWithStreak()
    {
        // streak 42 → 2 steps → disc = 5% + 2×5% = 15% → 85; monotonic vs the 21-day single step (90).
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, pptcv: 45m,
            ksStock: 0, supplierStock: 50, qty90: 0, zeroSaleStreakDays: 42,
            band: TestData.Band(marginFloorPct: 10m));

        Assert.Equal(85m, _algorithm.Evaluate(ctx)!.SuggestedPrice);
    }

    [Fact]
    public void NonSelling_StopsAtMarginFloor()
    {
        // Deep streak: schedule disc (55%) would price below the floor; clamp at the floor (cost 45,
        // floor 10% → 45 / 0.90 = 50), never the dead-stock 50%-of-cost tunnel.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 100m, pptcv: 45m,
            ksStock: 0, supplierStock: 50, qty90: 0, zeroSaleStreakDays: 210,
            band: TestData.Band(marginFloorPct: 10m));

        Assert.Equal(50m, _algorithm.Evaluate(ctx)!.SuggestedPrice);
    }

    [Fact]
    public void NonSelling_StartsAtCurrentPrice_HoldsWhileScheduleBelowExistingDiscount()
    {
        // Already 20% off (current 80). One step's schedule (10%) is shallower than the existing 20%,
        // so the price holds at today's 80 — the markdown starts AT the current price, never raises it.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 45m,
            ksStock: 0, supplierStock: 50, qty90: 0, zeroSaleStreakDays: 21,
            band: TestData.Band(marginFloorPct: 10m));

        Assert.Equal(80m, _algorithm.Evaluate(ctx)!.SuggestedPrice);
    }

    [Fact]
    public void PricedBelowFloor_LiftsToFloor()
    {
        // current 40 is below the floor (50): the one upward move — lift to the floor and stop.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 40m, pptcv: 45m,
            ksStock: 0, supplierStock: 50, qty90: 0, band: TestData.Band(marginFloorPct: 10m));

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(50m, vote!.SuggestedPrice);
        Assert.Equal("CROSS_DOCK_FLOOR", vote.ReasonCode);
    }

    // ---- Branch A: selling (Qty90 > 0) — sticky hold ---------------------------------------

    [Fact]
    public void Selling_HoldsTheWorkingPrice()
    {
        // Selling steadily at a 20% discount → hold it; the discount is what's moving units.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 45m,
            ksStock: 0, supplierStock: 50, qty7: 7, qty90: 30,
            band: TestData.Band(marginFloorPct: 10m));

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(80m, vote!.SuggestedPrice);          // never clawed back
        Assert.Equal("CROSS_DOCK_HOLD", vote.ReasonCode);
    }

    [Fact]
    public void Selling_ButDecaying_WithMarginRoom_DeepensOneStep()
    {
        // Sold over 90d but nothing in the last 7 (v7 0 ≤ ½·v90) and margin is healthy → deepen one step:
        // 20% + 5% = 25% → 75.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 80m, pptcv: 45m,
            ksStock: 0, supplierStock: 50, qty7: 0, qty90: 30,
            band: TestData.Band(marginFloorPct: 10m));

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(75m, vote!.SuggestedPrice);
        Assert.Equal("CROSS_DOCK_DEFEND", vote.ReasonCode);
    }

    [Fact]
    public void Selling_ButDecaying_ThinMargin_Holds()
    {
        // Decaying but margin is thin (current 52, cost 45 → 13.5% < floor 10% + 5pp buffer) → hold,
        // don't erode it further.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 52m, pptcv: 45m,
            ksStock: 0, supplierStock: 50, qty7: 0, qty90: 30,
            band: TestData.Band(marginFloorPct: 10m));

        var vote = _algorithm.Evaluate(ctx);

        Assert.NotNull(vote);
        Assert.Equal(52m, vote!.SuggestedPrice);
        Assert.Equal("CROSS_DOCK_HOLD", vote.ReasonCode);
    }

    [Fact]
    public void NeverRaisesAboveCurrent_WhenSelling()
    {
        // Even selling well at a deep discount, the lane never votes a price above today's — it's monotonic.
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 60m, pptcv: 20m,
            ksStock: 0, supplierStock: 50, qty7: 14, qty90: 60,
            band: TestData.Band(marginFloorPct: 10m));

        Assert.True(_algorithm.Evaluate(ctx)!.SuggestedPrice <= 60m);
    }
}
