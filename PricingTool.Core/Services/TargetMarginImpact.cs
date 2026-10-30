namespace PricingTool.Core.Services;

/// <summary>One price band as the target-impact view needs it: its cost range and its target margin (percent).</summary>
public sealed record TargetBand(int BandId, decimal MinPrice, decimal MaxPrice, decimal? TargetMarginPct)
{
    /// <summary>The band's target as a usable percent, or null when unset/out of range (same test as the guardrail).</summary>
    public decimal? Target => TargetMarginPct is > 0m and < 100m ? TargetMarginPct : null;
}

/// <summary>One SKU's last-30-days sales: its VAT-inclusive unit cost (PPTCV), units sold, VAT-exclusive revenue,
/// and whether the engine prices it (false = held or adopted: clearance pin, dead-stock hold, new product, synced).</summary>
public sealed record SoldSku(decimal Pptcv, int Units, decimal NetRevenue, bool CanReprice = true);

/// <summary>A band's last-30-days sales, as they were and as they would be at the band's target margin.
/// Money is VAT-exclusive. Revenue/Cost cover every SKU that sold; Held* is the part the engine can't move.</summary>
public sealed record TargetBandImpact(
    int BandId, int SkusSold, int Units, decimal Revenue, decimal Cost,
    decimal HeldRevenue, decimal HeldCost, decimal? TargetMarginPct)
{
    public decimal Profit => Revenue - Cost;
    /// <summary>Margin actually earned on everything the band sold — the figure a sales report shows.</summary>
    public decimal? MarginPct => Revenue > 0m ? Profit / Revenue * 100m : null;
    /// <summary>The same units with every repriceable SKU priced to the target (revenue = cost ÷ (1 − target)) and
    /// every held SKU keeping what it actually made. Actual when the band has no target.</summary>
    public decimal RevenueAtTarget => TargetMarginPct is decimal t
        ? (Cost - HeldCost) / (1m - t / 100m) + HeldRevenue
        : Revenue;
    public decimal ProfitAtTarget => RevenueAtTarget - Cost;
    /// <summary>Profit at target minus actual profit; null when the band has no target.</summary>
    public decimal? Impact => TargetMarginPct is null ? null : ProfitAtTarget - Profit;
}

/// <summary>Every band's impact plus the layer totals.</summary>
public sealed record TargetImpactResult(IReadOnlyDictionary<int, TargetBandImpact> Bands)
{
    public decimal Revenue => Bands.Values.Sum(b => b.Revenue);
    public decimal Profit => Bands.Values.Sum(b => b.Profit);
    public decimal? MarginPct => Revenue > 0m ? Profit / Revenue * 100m : null;
    /// <summary>Share of the 30-day sales the engine holds rather than prices — what a target can't move.</summary>
    public decimal? HeldSharePct => Revenue > 0m ? Bands.Values.Sum(b => b.HeldRevenue) / Revenue * 100m : null;
    public decimal RevenueAtTarget => Bands.Values.Sum(b => b.RevenueAtTarget);
    public decimal ProfitAtTarget => Bands.Values.Sum(b => b.ProfitAtTarget);
    /// <summary>The layer's margin if every repriceable SKU in a band with a target had earned that target on the
    /// last 30 days' units (held SKUs and bands without a target keep their actual result) — the sales-weighted
    /// counterpart of the Target margin total.</summary>
    public decimal? MarginAtTargetPct => RevenueAtTarget > 0m ? ProfitAtTarget / RevenueAtTarget * 100m : null;
    public decimal Impact => ProfitAtTarget - Profit;
    public bool AnyTarget => Bands.Values.Any(b => b.TargetMarginPct is not null);
}

/// <summary>
/// "If the last 30 days' sales had been made at each band's target margin, what would the margin be?" —
/// the sales-weighted reading of the Price Bands targets. The blended Target margin total weights each band
/// by list prices across the whole catalogue, so every SKU counts once whether it sells or not; this keeps
/// the units we actually sold and prices each band's sales to its target (revenue = cost ÷ (1 − target)), so a
/// band that sells a lot counts for a lot. Only SKUs the engine prices are moved: a held or adopted price
/// (clearance pin, dead-stock hold, new product, synced to Gjirafa50) keeps what it actually made. Cost stays
/// fixed — same units, same PPTCV — which makes it a margin question rather than a demand one. Profit is
/// VAT-exclusive: PPTCV is VAT-inclusive, so it is divided by (1 + VAT) to sit with the VAT-exclusive revenue.
/// SKUs are bucketed by PPTCV exactly as the engine buckets them: first band with MinPrice ≤ cost &lt; MaxPrice.
/// </summary>
public static class TargetMarginImpact
{
    public static TargetImpactResult Compute(IEnumerable<SoldSku> sold, IReadOnlyList<TargetBand> bands, decimal vatRatePct)
    {
        var vat = 1m + vatRatePct / 100m;
        var acc = bands.ToDictionary(b => b.BandId, _ => new Acc());
        foreach (var s in sold)
        {
            if (s.Units <= 0) continue;
            var band = bands.FirstOrDefault(b => s.Pptcv >= b.MinPrice && s.Pptcv < b.MaxPrice);
            if (band is null) continue;
            var a = acc[band.BandId];
            var cost = s.Units * s.Pptcv / vat;
            a.Skus++;
            a.Units += s.Units;
            a.Revenue += s.NetRevenue;
            a.Cost += cost;
            if (!s.CanReprice)
            {
                a.HeldRevenue += s.NetRevenue;
                a.HeldCost += cost;
            }
        }
        return new TargetImpactResult(bands.ToDictionary(
            b => b.BandId,
            b =>
            {
                var a = acc[b.BandId];
                return new TargetBandImpact(b.BandId, a.Skus, a.Units, a.Revenue, a.Cost, a.HeldRevenue, a.HeldCost, b.Target);
            }));
    }

    private sealed class Acc
    {
        public int Skus, Units;
        public decimal Revenue, Cost, HeldRevenue, HeldCost;
    }
}
