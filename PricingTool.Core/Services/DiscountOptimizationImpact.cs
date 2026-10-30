using PricingTool.Core.Domain;

namespace PricingTool.Core.Services;

/// <summary>Which quantity the impact view multiplies each SKU's per-unit profit by.</summary>
public enum ImpactUnitsBasis
{
    /// <summary>Units sold in the last 30 days — "if they keep selling as they do now".</summary>
    Sold30Days = 30,
    /// <summary>Units sold in the last 90 days.</summary>
    Sold90Days = 90,
    /// <summary>Units held in the local warehouse — "if all the stock sells".</summary>
    LocalStock = 0,
}

/// <summary>One SKU's inputs to the impact view. Prices and cost are the layer's VAT-inclusive currency.</summary>
public sealed record DiscountImpactSku(
    string Sku,
    string? ProductName,
    decimal OldPrice,
    decimal CurrentPrice,
    decimal AnchorPrice,
    decimal Cost,
    int Units,
    bool IsExempt,
    string? ActiveDiscountIds);

/// <summary>One SKU the trim would move. Profits are VAT-exclusive, over the chosen units basis.</summary>
public sealed record DiscountImpactSkuRow(
    string Sku,
    string? ProductName,
    string Range,
    decimal OldPrice,
    decimal CurrentPrice,
    decimal NewPrice,
    decimal DiscountBeforePct,
    decimal DiscountAfterPct,
    decimal Cost,
    int Units,
    decimal CurrentProfit,
    decimal NewProfit,
    bool CappedAtAnchor,
    string? ActiveDiscountIds)
{
    public decimal ExtraProfit => NewProfit - CurrentProfit;
    public decimal PriceChangePct => CurrentPrice > 0m ? (NewPrice / CurrentPrice - 1m) * 100m : 0m;
}

/// <summary>Accumulated impact for one range (or the total across ranges).</summary>
public sealed class DiscountImpactLine
{
    public required string Label { get; init; }
    /// <summary>The range's trim share in percent; null on the total line.</summary>
    public decimal? TrimPct { get; init; }

    /// <summary>Eligible SKUs whose current discount falls in the range (moved or not).</summary>
    public int SkusInRange { get; internal set; }
    /// <summary>SKUs in the range the trim skips on purpose: dead stock, clearance pins, synced, new, held.</summary>
    public int SkusExempt { get; internal set; }
    /// <summary>SKUs whose price the trim actually raises.</summary>
    public int SkusAffected { get; internal set; }
    /// <summary>Of the affected, how many the no-raise ceiling (anchor) stopped short of the full trim.</summary>
    public int SkusCappedAtAnchor { get; internal set; }
    public int Units { get; internal set; }

    /// <summary>VAT-inclusive revenue of the affected SKUs at today's price — the "how much is touched" base.</summary>
    public decimal RevenueNow { get; internal set; }
    public decimal RevenueNew { get; internal set; }
    /// <summary>VAT-exclusive gross profit of the affected SKUs at today's price and at the trimmed price.</summary>
    public decimal CurrentProfit { get; internal set; }
    public decimal NewProfit { get; internal set; }

    internal decimal SumDiscountBefore { get; set; }
    internal decimal SumDiscountAfter { get; set; }
    internal decimal SumPriceChangePct { get; set; }

    public decimal ExtraProfit => NewProfit - CurrentProfit;
    /// <summary>Extra profit as a % of what the affected SKUs earn today; null when they earn nothing.</summary>
    public decimal? ProfitChangePct => CurrentProfit > 0m ? ExtraProfit / CurrentProfit * 100m : null;
    public decimal? AvgDiscountBeforePct => SkusAffected > 0 ? SumDiscountBefore / SkusAffected : null;
    public decimal? AvgDiscountAfterPct => SkusAffected > 0 ? SumDiscountAfter / SkusAffected : null;
    /// <summary>Revenue-weighted price move; a plain average when no affected SKU has units on the basis.</summary>
    public decimal? AvgPriceChangePct => RevenueNow > 0m
        ? (RevenueNew / RevenueNow - 1m) * 100m
        : SkusAffected > 0 ? SumPriceChangePct / SkusAffected : null;
    /// <summary>Margin of the affected SKUs, (price − cost) / price, today and at the trimmed price.</summary>
    public decimal? MarginNowPct { get; internal set; }
    public decimal? MarginAfterPct { get; internal set; }
}

/// <summary>The whole impact view: per range, the total, the affected SKUs, and the layer it sits in.</summary>
public sealed record DiscountImpactResult(
    IReadOnlyList<DiscountImpactLine> Ranges,
    DiscountImpactLine Total,
    IReadOnlyList<DiscountImpactSkuRow> Skus,
    int LayerSkus,
    decimal LayerRevenue,
    decimal LayerProfit)
{
    /// <summary>Affected SKUs as a % of the layer's priced SKUs.</summary>
    public decimal? SkuSharePct => LayerSkus > 0 ? (decimal)Total.SkusAffected / LayerSkus * 100m : null;
    /// <summary>Affected revenue as a % of the layer's revenue on the same units basis.</summary>
    public decimal? RevenueSharePct => LayerRevenue > 0m ? Total.RevenueNow / LayerRevenue * 100m : null;
    /// <summary>Extra profit as a % of the layer's whole profit on the same units basis.</summary>
    public decimal? ExtraOfLayerProfitPct => LayerProfit > 0m ? Total.ExtraProfit / LayerProfit * 100m : null;
}

/// <summary>
/// "What would this trim table do to today's prices?" — the deliberately simple view on the Price Bands
/// tab: for every SKU whose live discount falls in a range with a trim, the trimmed price (via
/// <see cref="DiscountOptimizer"/>, capped at the no-raise ceiling max(anchor, current) exactly as the
/// engine caps it), and the profit at today's price vs at the trimmed price over the SAME units. It assumes
/// customers don't react — the same units sell at the higher price — so it is the ceiling, not a forecast.
/// Profit is VAT-exclusive: prices and PPTCV are both VAT-inclusive, so the gap is divided by (1 + VAT).
/// Before charm rounding, which the engine applies afterwards.
/// </summary>
public static class DiscountOptimizationImpact
{
    public static DiscountImpactResult Compute(
        IEnumerable<DiscountImpactSku> skus,
        IReadOnlyList<DiscountTrimRange> ranges,
        decimal vatRatePct,
        int layerSkus,
        decimal layerRevenue,
        decimal layerProfit)
    {
        var vat = 1m + vatRatePct / 100m;
        var lines = ranges.Select(r => new DiscountImpactLine { Label = r.Label, TrimPct = r.TrimPct }).ToList();
        var total = new DiscountImpactLine { Label = "Total" };
        var rows = new List<DiscountImpactSkuRow>();
        // Margin accumulators: VAT-exclusive revenue of the affected SKUs, now and after.
        var netNow = new decimal[lines.Count];
        var netNew = new decimal[lines.Count];
        decimal totNetNow = 0m, totNetNew = 0m;

        foreach (var s in skus)
        {
            if (DiscountOptimizer.DisplayedDiscountPct(s.OldPrice, s.CurrentPrice) is not decimal before) continue;
            var ix = IndexOf(ranges, before);
            if (ix < 0) continue;
            var line = lines[ix];
            if (s.IsExempt)
            {
                line.SkusExempt++;
                total.SkusExempt++;
                continue;
            }
            line.SkusInRange++;
            total.SkusInRange++;

            if (DiscountOptimizer.TrimmedPrice(s.OldPrice, s.CurrentPrice, ranges) is not decimal target) continue;
            var ceiling = Math.Max(s.AnchorPrice, s.CurrentPrice);
            var newPrice = Math.Min(target, ceiling);
            if (newPrice <= s.CurrentPrice) continue;   // the anchor leaves no room to raise

            var units = Math.Max(s.Units, 0);
            var after = DiscountOptimizer.DisplayedDiscountPct(s.OldPrice, newPrice) ?? 0m;
            var row = new DiscountImpactSkuRow(
                s.Sku, s.ProductName, line.Label, s.OldPrice, s.CurrentPrice, newPrice, before, after, s.Cost, units,
                CurrentProfit: (s.CurrentPrice - s.Cost) * units / vat,
                NewProfit: (newPrice - s.Cost) * units / vat,
                CappedAtAnchor: target > ceiling,
                s.ActiveDiscountIds);
            rows.Add(row);

            foreach (var l in new[] { line, total })
            {
                l.SkusAffected++;
                if (row.CappedAtAnchor) l.SkusCappedAtAnchor++;
                l.Units += units;
                l.RevenueNow += s.CurrentPrice * units;
                l.RevenueNew += newPrice * units;
                l.CurrentProfit += row.CurrentProfit;
                l.NewProfit += row.NewProfit;
                l.SumDiscountBefore += before;
                l.SumDiscountAfter += after;
                l.SumPriceChangePct += row.PriceChangePct;
            }
            netNow[ix] += s.CurrentPrice * units / vat;
            netNew[ix] += newPrice * units / vat;
            totNetNow += s.CurrentPrice * units / vat;
            totNetNew += newPrice * units / vat;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            lines[i].MarginNowPct = netNow[i] > 0m ? lines[i].CurrentProfit / netNow[i] * 100m : null;
            lines[i].MarginAfterPct = netNew[i] > 0m ? lines[i].NewProfit / netNew[i] * 100m : null;
        }
        total.MarginNowPct = totNetNow > 0m ? total.CurrentProfit / totNetNow * 100m : null;
        total.MarginAfterPct = totNetNew > 0m ? total.NewProfit / totNetNew * 100m : null;

        rows.Sort((a, b) => b.ExtraProfit.CompareTo(a.ExtraProfit));
        return new DiscountImpactResult(lines, total, rows, layerSkus, layerRevenue, layerProfit);
    }

    private static int IndexOf(IReadOnlyList<DiscountTrimRange> ranges, decimal discountPct)
    {
        for (var i = 0; i < ranges.Count; i++)
            if (ranges[i].Contains(discountPct)) return i;
        return -1;
    }
}
