using PricingTool.Core.Services;

namespace PricingTool.Tests;

/// <summary>
/// The Price Bands "Target impact" column: the last 30 days' units of each band, priced to the band's target
/// margin (revenue = cost ÷ (1 − target)), vs what they actually made — and the layer margin that implies.
/// </summary>
public class TargetMarginImpactTests
{
    private static readonly TargetBand[] Bands =
    {
        new(1, 0m, 50m, TargetMarginPct: 30m),
        new(2, 50m, 500m, TargetMarginPct: null),   // no target: keeps its actual result
    };

    [Fact]
    public void PricesTheSameUnitsToTheTarget_AndBlendsByWhatSold()
    {
        var sold = new[]
        {
            // Band 1: PPTCV 11.80 incl. 18% VAT = 10.00 ex-VAT; 10 units → cost 100, sold for 150 (33.3% margin).
            new SoldSku(11.80m, 10, 150m),
            // Band 2: cost 100 ex-VAT, sold for 200 (50% margin), no target.
            new SoldSku(118m, 1, 200m),
            new SoldSku(20m, 0, 0m),        // no units: ignored
        };

        var r = TargetMarginImpact.Compute(sold, Bands, vatRatePct: 18m);

        var b1 = r.Bands[1];
        Assert.Equal(100m, Math.Round(b1.Cost, 6));
        Assert.Equal(150m, b1.Revenue);
        Assert.Equal(33.3m, Math.Round(b1.MarginPct!.Value, 1));
        // At 30%: revenue = 100 / 0.7 = 142.857, profit 42.857 → 7.14 LESS than the 50 actually made.
        Assert.Equal(-7.14m, Math.Round(b1.Impact!.Value, 2));

        Assert.Null(r.Bands[2].Impact);                         // no target, no impact
        Assert.Equal(42.9m, Math.Round(r.MarginPct!.Value, 1)); // actual: 150 profit on 350
        // At target: (42.857 + 100) / (142.857 + 200) = 41.7% — weighted by what each band sold.
        Assert.Equal(41.7m, Math.Round(r.MarginAtTargetPct!.Value, 1));
        Assert.True(r.AnyTarget);
    }

    [Fact]
    public void HeldSkus_CountInTheActualMargin_ButTheTargetDoesntMoveThem()
    {
        var sold = new[]
        {
            new SoldSku(11.80m, 10, 150m),                     // repriceable: cost 100, made 50
            new SoldSku(11.80m, 10, 120m, CanReprice: false),  // clearance-pinned: cost 100, made 20
        };

        var r = TargetMarginImpact.Compute(sold, Bands, vatRatePct: 18m);
        var b1 = r.Bands[1];

        // The actual margin covers everything that sold — the figure the sales report shows: 70 on 270.
        Assert.Equal(25.93m, Math.Round(b1.MarginPct!.Value, 2));
        // At 30% only the repriceable SKU moves (to 142.857); the pinned one keeps its 120.
        Assert.Equal(Math.Round(100m / 0.7m + 120m, 4), Math.Round(b1.RevenueAtTarget, 4));
        Assert.Equal(-7.14m, Math.Round(b1.Impact!.Value, 2));
        Assert.Equal(44.4m, Math.Round(r.HeldSharePct!.Value, 1));   // 120 of 270 can't be moved
    }

    [Fact]
    public void BandEdgesFollowTheEngine_MinInclusive_MaxExclusive()
    {
        // Bands key off PPTCV exactly like BandConfigProvider.FindBand: 49.99 is band 1, 50.00 is band 2.
        var r = TargetMarginImpact.Compute(
            new[] { new SoldSku(49.99m, 1, 100m), new SoldSku(50m, 1, 100m) }, Bands, vatRatePct: 18m);

        Assert.Equal(1, r.Bands[1].SkusSold);
        Assert.Equal(1, r.Bands[2].SkusSold);
    }
}
