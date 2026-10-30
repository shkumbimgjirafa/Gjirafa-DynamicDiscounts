using PricingTool.Core.Algorithms;
using PricingTool.Core.Domain;
using PricingTool.Core.Services;

namespace PricingTool.Tests;

/// <summary>
/// Discount optimization: a price whose displayed discount off the shelf (Old) price falls in one of the
/// layer's trim ranges has that discount trimmed by the range's share (a share OF the discount, not points),
/// bounded by the no-raise ceiling, never on dead stock or a pinned/held price. Covers the arithmetic
/// (<see cref="DiscountOptimizer"/>), the pipeline (<see cref="PriceCalculator"/> — the algorithm path via
/// Reprice and the no-vote live-price audit via Decide), and the Price Bands impact view
/// (<see cref="DiscountOptimizationImpact"/>).
/// </summary>
public class DiscountOptimizationTests
{
    private readonly PriceCalculator _calc = new(new WeightedScoringService(), new GuardrailService(), new RoundingService());

    private static readonly string[] Reason = { "TEST" };
    private static readonly IReadOnlyList<DiscountTrimRange> Ranges = DiscountOptimizer.DefaultRanges;

    // A normal, locally-selling SKU: shelf/anchor 100, selling at 75 = 25% off (the 20–30% row, 18% trim).
    private static SkuContext Deep(decimal currentPrice = 75m, decimal? anchorPrice = null,
        IReadOnlyList<DiscountTrimRange>? ranges = null, int ksStock = 10, int qty90 = 12,
        decimal? clearanceLockPrice = null, PriceBandConfig? band = null) =>
        TestData.Ctx(oldPrice: 100m, currentPrice: currentPrice, pptcv: 40m, anchorPrice: anchorPrice,
            ksStock: ksStock, supplierStock: 0, qty90: qty90, clearanceLockPrice: clearanceLockPrice,
            band: band ?? TestData.Band(marginFloorPct: 10m), discountTrimRanges: ranges ?? Ranges);

    // ---------------------------------------------------------------- the arithmetic

    [Theory]
    [InlineData(75, 79.50)]   // 25% off, 18% of it trimmed → 20.5% off
    [InlineData(70, 74.80)]   // exactly 30% off: the lower edge is inclusive → 30–40% row, 16% → 25.2% off
    [InlineData(55, 61.30)]   // 45% off, 14% → 38.7% off
    [InlineData(35, 41.50)]   // 65% off: the open-ended 60%+ row, 10% → 58.5% off
    public void TrimmedPrice_TakesTheRangeShareOfTheDiscount(decimal price, decimal expected)
    {
        Assert.Equal(expected, DiscountOptimizer.TrimmedPrice(100m, price, Ranges));
    }

    [Theory]
    [InlineData(85)]    // 15% off: the 0–20% row trims nothing
    [InlineData(100)]   // no discount at all
    [InlineData(110)]   // priced above the shelf
    public void TrimmedPrice_LeavesShallowOrNoDiscountAlone(decimal price)
    {
        Assert.Null(DiscountOptimizer.TrimmedPrice(100m, price, Ranges));
    }

    [Fact]
    public void TrimmedPrice_ZeroTrimRow_AndNoTable_AreOff()
    {
        var zeros = new[] { new DiscountTrimRange(20m, null, 0m) };
        Assert.Null(DiscountOptimizer.TrimmedPrice(100m, 75m, zeros));
        Assert.Null(DiscountOptimizer.TrimmedPrice(100m, 75m, null));
    }

    // ---------------------------------------------------------------- the pipeline

    [Fact]
    public void Proposal_DeepDiscount_IsTrimmedAndFlagged()
    {
        var decision = _calc.Reprice(Deep(), 75m, Reason);

        Assert.Equal(79.50m, decision.FinalPrice);
        Assert.Contains(GuardrailFlags.DiscountOptimized, decision.GuardrailFlagsApplied);
        Assert.True(decision.Changed);
    }

    [Fact]
    public void Proposal_TrimStopsAtTheAnchor_NeverAboveIt()
    {
        // The full trim wants 79.50, but the anchor (78) is the ceiling — the firm no-above-anchor rule.
        var decision = _calc.Reprice(Deep(anchorPrice: 78m), 75m, Reason);

        Assert.Equal(78m, decision.FinalPrice);
        Assert.Contains(GuardrailFlags.DiscountOptimized, decision.GuardrailFlagsApplied);
    }

    [Fact]
    public void Proposal_NoRoomUnderTheCeiling_NothingMoves()
    {
        // Anchor 70 sits below the current 75, so the ceiling is the current price: no raise is possible.
        var decision = _calc.Reprice(Deep(anchorPrice: 70m), 75m, Reason);

        Assert.Equal(75m, decision.FinalPrice);
        Assert.DoesNotContain(GuardrailFlags.DiscountOptimized, decision.GuardrailFlagsApplied);
    }

    [Fact]
    public void FeatureOff_PriceUntouched()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 75m, pptcv: 40m, ksStock: 10, qty90: 12,
            band: TestData.Band(marginFloorPct: 10m));   // no trim table on the context = layer switched off

        var decision = _calc.Reprice(ctx, 75m, Reason);

        Assert.Equal(75m, decision.FinalPrice);
        Assert.DoesNotContain(GuardrailFlags.DiscountOptimized, decision.GuardrailFlagsApplied);
    }

    [Fact]
    public void DeadStock_IsExempt()
    {
        // Locally held, zero 90-day sales → dead stock: its markdown is a deliberate clearing price.
        var decision = _calc.Reprice(Deep(ksStock: 50, qty90: 0), 75m, Reason);

        Assert.DoesNotContain(GuardrailFlags.DiscountOptimized, decision.GuardrailFlagsApplied);
        Assert.True(decision.FinalPrice <= 75m);
    }

    [Fact]
    public void ClearanceLock_IsExempt_PinHolds()
    {
        var decision = _calc.Reprice(Deep(clearanceLockPrice: 75m), 75m, Reason);

        Assert.Equal(75m, decision.FinalPrice);
        Assert.DoesNotContain(GuardrailFlags.DiscountOptimized, decision.GuardrailFlagsApplied);
    }

    [Fact]
    public void NoVote_LivePriceIsTrimmed_ReasonAndFlagRecorded()
    {
        // No algorithms → no vote → the live-price audit. This is where nearly every deep campaign price is.
        var decision = _calc.Decide(Deep(), Array.Empty<IPricingAlgorithm>());

        Assert.Equal(79.50m, decision.FinalPrice);
        Assert.True(decision.Changed);
        Assert.Contains(GuardrailFlags.DiscountOptimized, decision.ReasonCodes);
        Assert.Contains(GuardrailFlags.DiscountOptimized, decision.GuardrailFlagsApplied);
    }

    [Fact]
    public void NoVote_FeatureOff_LeftExactlyAsItWas()
    {
        var ctx = TestData.Ctx(oldPrice: 100m, currentPrice: 75m, pptcv: 40m, ksStock: 10, qty90: 12,
            band: TestData.Band(marginFloorPct: 10m));

        var decision = _calc.Decide(ctx, Array.Empty<IPricingAlgorithm>());

        Assert.Equal(75m, decision.FinalPrice);
        Assert.False(decision.Changed);
        Assert.Empty(decision.ReasonCodes);
    }

    [Fact]
    public void Trimmed_ThenRoundedOntoTheBandsCharmGrid()
    {
        // GjirafaMall's .99 charm: the trimmed 79.50 must still land on a .99 price, above today's 75.
        var band = TestData.Band(marginFloorPct: 10m, rounding: RoundingConvention.GjmCharm, roundingEnabled: true);
        var decision = _calc.Reprice(Deep(band: band), 75m, Reason);

        Assert.Equal(0.99m, decision.FinalPrice % 1m);
        Assert.True(decision.FinalPrice > 75m);
        Assert.True(decision.FinalPrice <= 100m);
        Assert.Contains(GuardrailFlags.DiscountOptimized, decision.GuardrailFlagsApplied);
    }

    // ---------------------------------------------------------------- the impact view

    [Fact]
    public void Impact_ComparesProfitAtTodaysPriceWithTheTrimmedPrice_OverTheSameUnits()
    {
        var skus = new[]
        {
            // 25% off → 79.50; 10 units; cost 50.
            new DiscountImpactSku("A", "a", 100m, 75m, 100m, 50m, Units: 10, IsExempt: false, "1,2"),
            // Same discount but exempt (dead stock / pinned) — counted, never moved.
            new DiscountImpactSku("B", "b", 100m, 75m, 100m, 50m, Units: 4, IsExempt: true, null),
            // 15% off: in the 0–20% row, which trims nothing.
            new DiscountImpactSku("C", "c", 100m, 85m, 100m, 50m, Units: 3, IsExempt: false, null),
            // 65% off → wants 41.50, but the anchor is 38: stops there.
            new DiscountImpactSku("D", "d", 100m, 35m, 38m, 30m, Units: 2, IsExempt: false, null),
        };

        var r = DiscountOptimizationImpact.Compute(skus, Ranges, vatRatePct: 18m,
            layerSkus: 10, layerRevenue: 8_200m, layerProfit: 1_000m);

        var twenties = r.Ranges[1];
        Assert.Equal(1, twenties.SkusInRange);
        Assert.Equal(1, twenties.SkusExempt);
        Assert.Equal(1, twenties.SkusAffected);
        Assert.Equal(10, twenties.Units);
        // Extra profit = 4.50 more per unit × 10 units, VAT taken out.
        Assert.Equal(Math.Round(45m / 1.18m, 6), Math.Round(twenties.ExtraProfit, 6));

        var top = r.Ranges[5];
        Assert.Equal(1, top.SkusAffected);
        Assert.Equal(1, top.SkusCappedAtAnchor);

        Assert.Equal(0, r.Ranges[0].SkusAffected);   // 0–20% untouched
        Assert.Equal(2, r.Total.SkusAffected);
        Assert.Equal(20m, r.SkuSharePct);             // 2 of the layer's 10 priced SKUs
        Assert.Equal(10m, r.RevenueSharePct);         // (75×10 + 35×2) = 820 of 8,200
        Assert.Equal("A", r.Skus[0].Sku);             // biggest gain first
        Assert.Equal(38m, r.Skus.Single(s => s.Sku == "D").NewPrice);
        Assert.True(r.Total.ProfitChangePct > 0m);
    }
}
