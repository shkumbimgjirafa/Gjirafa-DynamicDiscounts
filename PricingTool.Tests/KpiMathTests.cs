using PricingTool.Core.Services;

namespace PricingTool.Tests;

public class KpiMathTests
{
    // One SKU at 18% VAT: cost 50 (all-in), current 20×5=100, proposed 25×5=125 (VAT-inclusive, 5 units).
    // Profit is VAT-EXCLUDED: now (100-50)/1.18 = 42.37; proposed (125-50)/1.18 = 63.56. Margins 50% → 60%.
    [Fact]
    public void FromSums_ProfitIsVatExcluded_MarginUnaffected()
    {
        var w = KpiMath.FromSums(7, sumCurrentPriceQty: 100m, sumProposedPriceQty: 125m, sumCostQty: 50m, vatRatePct: 18m);

        Assert.Equal(7, w.WindowDays);
        Assert.Equal(42.37m, Math.Round(w.ProfitNow, 2));
        Assert.Equal(63.56m, Math.Round(w.ProfitProposed, 2));
        Assert.Equal(21.19m, Math.Round(w.ProfitDelta, 2));
        Assert.Equal(50m, Math.Round(w.ProfitDeltaPct!.Value, 1));        // (75-50)/50, VAT cancels
        Assert.Equal(50m, Math.Round(w.MarginNowPct!.Value, 1));          // unchanged by VAT
        Assert.Equal(60m, Math.Round(w.MarginProposedPct!.Value, 1));
        Assert.Equal(10m, Math.Round(w.MarginDeltaPp!.Value, 1));
    }

    [Fact]
    public void FromSums_EmptyWindow_NoUnits_YieldsNullMargins()
    {
        var w = KpiMath.FromSums(30, 0m, 0m, 0m, 18m);

        Assert.Equal(0m, w.ProfitNow);
        Assert.Equal(0m, w.ProfitDelta);
        Assert.Null(w.MarginNowPct);        // no revenue → margin undefined
        Assert.Null(w.ProfitDeltaPct);      // non-positive baseline → relative % undefined
    }

    [Fact]
    public void FromSums_BaselineBelowCost_ProfitDeltaPctIsNull_ButDeltaStillComputed()
    {
        // current 40 (below the 60 cost), proposed 70 → baseline profit is negative.
        var w = KpiMath.FromSums(90, sumCurrentPriceQty: 40m, sumProposedPriceQty: 70m, sumCostQty: 60m, vatRatePct: 18m);

        Assert.True(w.ProfitNow < 0);
        Assert.True(w.ProfitProposed > 0);
        Assert.True(w.ProfitDelta > 0);
        Assert.Null(w.ProfitDeltaPct);      // % off a non-positive baseline is meaningless
    }

    // The bug this fixes: one elastic SKU, cost 100, marked DOWN 250 → 200, 10 units sold, E = −2.
    // 200 is the constant-elasticity profit-max price (cost·E/(E+1) = 100·−2/−1). VAT 0 for clean numbers.
    //   Naive (units frozen): profit 1500 → 1000  → reads as a −500 LOSS (the artifact).
    //   Demand response m = (200/250)^−2 = 1.5625 → 10 → 15.625 units (+56.25%).
    //   Elastic profit on the SKU: 15.625 × (200−100) = 1562.5  → a +62.5 GAIN over the 1500 baseline.
    //   Break-even needs (250−100)/(200−100) − 1 = +50% volume; the fit predicts +56.25% → clears.
    [Fact]
    public void ElasticProjection_PriceCutOnElasticSku_CreditsPredictedVolume()
    {
        var naive = KpiMath.FromSums(7, sumCurrentPriceQty: 2500m, sumProposedPriceQty: 2000m, sumCostQty: 1000m, vatRatePct: 0m);
        Assert.Equal(-500m, naive.ProfitDelta);   // naive same-units baseline shows a loss

        var e = KpiMath.ElasticProjectionFromSums(
            7, naive, vatRatePct: 0m,
            proposedDeltaSum: 1125m,   // 200 × 10 × (1.5625 − 1)
            costDeltaSum: 562.5m,      // 100 × 10 × (1.5625 − 1)
            qtySum: 10m, qtyAdjSum: 15.625m,
            curMarginSum: 1500m,       // (250 − 100) × 10
            propMarginSum: 1000m,      // (200 − 100) × 10
            skuCount: 1);

        Assert.NotNull(e);
        Assert.Equal(1, e!.Value.SkuCount);
        Assert.Equal(1562.5m, e.Value.ProfitProposed);              // gain, not loss
        // Single SKU → margin-weighted and quantity-weighted lift coincide at +56.25%.
        Assert.Equal(56.25m, Math.Round(e.Value.PredictedUnitsLiftPct, 2));
        Assert.Equal(56.25m, Math.Round(e.Value.PredictedLiftPct!.Value, 2));
        Assert.Equal(50m, Math.Round(e.Value.RequiredLiftPct!.Value, 2));
        Assert.True(e.Value.ClearsBreakeven);                       // predicted (+56.25%) ≥ break-even (+50%)

        var kpi = new ProposalWindowKpi(naive, e);
        Assert.True(kpi.HasElastic);
        Assert.Equal(62.5m, kpi.ElasticProfitDelta);                // vs the naive −500
        Assert.Equal(4.17m, Math.Round(kpi.ElasticProfitDeltaPct!.Value, 2)); // 62.5 / 1500
    }

    // Heterogeneous subset — the bug the review caught. Two SKUs (k=0 VAT):
    //   LOWM  {q=100, cur=11, prop=10.2, cost=10, m=5}  — thin margin, big volume response
    //   HIGHM {q=100, cur=30, prop=25,  cost=10, m=1}  — fat margin, no volume response
    // The RAW units lift is +200% (the volume piles onto the thin-margin SKU), but the proposed prices
    // actually LOSE money (subset profit 1600 < 2100). A units-weighted "predicts +200% vs break-even +38%"
    // would have shown a ✓ that contradicts reality. The margin-weighted predicted lift (+5.3%) is below the
    // +38.2% break-even, so the figures and the ✗ agree.
    [Fact]
    public void ElasticProjection_HeterogeneousMargins_DisplayedFiguresMatchVerdict()
    {
        var naive = KpiMath.FromSums(7, sumCurrentPriceQty: 11m * 100 + 30m * 100,
            sumProposedPriceQty: 10.2m * 100 + 25m * 100, sumCostQty: 10m * 200, vatRatePct: 0m);

        var e = KpiMath.ElasticProjectionFromSums(
            7, naive, vatRatePct: 0m,
            proposedDeltaSum: 10.2m * 100 * 4m,   // LOWM only (HIGHM m=1 contributes 0)
            costDeltaSum: 10m * 100 * 4m,
            qtySum: 200m, qtyAdjSum: 500m + 100m,
            curMarginSum: 1m * 100 + 20m * 100,   // (cur−cost)·q  = 100 + 2000 = 2100
            propMarginSum: 0.2m * 100 + 15m * 100, // (prop−cost)·q = 20 + 1500 = 1520
            skuCount: 2)!.Value;

        Assert.Equal(200m, Math.Round(e.PredictedUnitsLiftPct, 2));      // raw units (informational, large)
        Assert.Equal(5.26m, Math.Round(e.PredictedLiftPct!.Value, 2));   // margin-weighted (decision-relevant)
        Assert.Equal(38.16m, Math.Round(e.RequiredLiftPct!.Value, 2));
        Assert.False(e.ClearsBreakeven);                                 // 5.26% < 38.16% → no contradiction
        // The invariant the bug violated: the displayed comparison ALWAYS agrees with the verdict.
        Assert.Equal(e.PredictedLiftPct >= e.RequiredLiftPct, e.ClearsBreakeven);
    }

    [Fact]
    public void ElasticProjection_NoElasticSkus_ReturnsNull()
    {
        var naive = KpiMath.FromSums(30, 1000m, 900m, 500m, 18m);
        var e = KpiMath.ElasticProjectionFromSums(30, naive, 18m, 0m, 0m, 0m, 0m, 0m, 0m, skuCount: 0);

        Assert.Null(e);
        var kpi = new ProposalWindowKpi(naive, e);
        Assert.False(kpi.HasElastic);
        Assert.Null(kpi.ElasticProfitDelta);
    }

    [Fact]
    public void ElasticProjection_ProposedAtOrBelowCost_RequiredLiftIsNull_ButProjectionStillComputed()
    {
        // cost 100, marked down 120 → 90 (below cost): no volume can break even, so required lift is undefined.
        var naive = KpiMath.FromSums(7, sumCurrentPriceQty: 1200m, sumProposedPriceQty: 900m, sumCostQty: 1000m, vatRatePct: 0m);
        var e = KpiMath.ElasticProjectionFromSums(
            7, naive, vatRatePct: 0m,
            proposedDeltaSum: 700m, costDeltaSum: 777.78m,
            qtySum: 10m, qtyAdjSum: 17.7778m,
            curMarginSum: 200m,        // (120 − 100) × 10
            propMarginSum: -100m,      // (90 − 100) × 10 → at/below cost
            skuCount: 1);

        Assert.NotNull(e);
        Assert.Null(e!.Value.RequiredLiftPct);        // break-even undefined below cost
        Assert.Null(e.Value.PredictedLiftPct);        // margin-weighted lift undefined below cost
        Assert.False(e.Value.ClearsBreakeven);        // selling more below-cost units loses more money
        Assert.True(e.Value.PredictedUnitsLiftPct > 0); // raw units still rise (informational)
    }
}
