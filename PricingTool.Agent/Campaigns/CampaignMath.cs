namespace PricingTool.Agent.Campaigns;

/// <summary>Sales of one window around a past campaign: units, net revenue and gross profit EUR VAT excl. (booked cost);
/// discount and the pre-discount value VAT incl. <c>Days</c> counts the window's days that weren't campaign days (all of
/// them for the campaign itself); 0 when the window isn't usable (before the data starts, or right after a campaign).</summary>
public sealed record SegmentSales(decimal Units, decimal NetRevenue, decimal GrossProfit, decimal Discount, decimal PreDiscountGross, int Days,
    int Lines = 0, int DiscountedLines = 0)
{
    public static readonly SegmentSales Empty = new(0, 0, 0, 0, 0, 0);

    /// <summary>
    /// Below this share of lines with a discount on them, the lines don't record discounts and depth is unknown. On
    /// GjirafaMall from Nov 2025 only about 5 % of lines carry DiscountGiven (80–90 % before, Gjirafa50 unchanged): the
    /// discount stopped being booked on the line, so depth from the lines isn't comparable across that break.
    /// </summary>
    public const decimal MinDiscountedLineShare = 0.2m;

    public bool DepthRecorded => Lines == 0 || DiscountedLines >= MinDiscountedLineShare * Lines;
    public decimal UnitsPerDay => Days > 0 ? Units / Days : 0;
    public decimal GpPerDay => Days > 0 ? GrossProfit / Days : 0;
    public decimal? MarginPct => NetRevenue != 0 ? 100m * GrossProfit / NetRevenue : null;
    /// <summary>The discount given as a share of the pre-discount value, in percent.</summary>
    public decimal? DepthPct => PreDiscountGross > 0 && DepthRecorded ? 100m * Discount / PreDiscountGross : null;
    public SegmentSales Plus(SegmentSales o) => new(Units + o.Units, NetRevenue + o.NetRevenue, GrossProfit + o.GrossProfit,
        Discount + o.Discount, PreDiscountGross + o.PreDiscountGross, Math.Max(Days, o.Days), Lines + o.Lines, DiscountedLines + o.DiscountedLines);
}

/// <summary>The four windows of one past campaign: the campaign, the 28 days before it, and the same two windows a year
/// earlier (364 days back), each without other campaigns' days.</summary>
public sealed record CampaignSegments(SegmentSales Campaign, SegmentSales Before, SegmentSales CampaignLastYear, SegmentSales BeforeLastYear);

/// <summary>What one past campaign did, for one category or the whole layer (see <see cref="CampaignMath.Read"/>).</summary>
public sealed record CampaignEffect(
    decimal UnitsPerDayBefore, decimal UnitsPerDayCampaign, decimal? RawUplift, decimal? SeasonFactor, decimal? Uplift,
    decimal? DepthBeforePct, decimal? DepthCampaignPct, decimal? ExtraDepthPp,
    decimal? MarginBeforePct, decimal? MarginCampaignPct, decimal GpPerDayBefore, decimal GpPerDayCampaign, bool Thin);

/// <summary>The uplift curve: units multiplier = exp(B × extra depth as a fraction), fitted through (0, 1), and never
/// read beyond the deepest extra depth a campaign in the fit actually went (<see cref="MaxDepthPct"/>).</summary>
public sealed record UpliftCurve(decimal? B, int Points, IReadOnlyList<(decimal DepthPct, decimal Uplift)> Grid, string Basis, decimal MaxDepthPct = 0)
{
    public decimal? UpliftAt(decimal depthPct) => B is { } b ? (decimal)Math.Exp((double)(b * Math.Min(depthPct, MaxDepthPct) / 100m)) : null;
}

/// <summary>
/// The campaign arithmetic (plan section 5, "get_campaign_history"): how much volume each depth of discount bought, per
/// category. Pure, so the planner and the tests read the same numbers.
/// </summary>
public static class CampaignMath
{
    /// <summary>A campaign row with fewer units than this (in the campaign, or in the 28 days before) is thin.</summary>
    public const int MinUnits = 10;
    public static readonly decimal[] DepthGrid = { 5, 10, 15, 20, 25 };

    /// <summary>
    /// Units per day in the campaign against the 28 days before (raw uplift), divided by the same ratio a year earlier
    /// (the season factor) when both of last year's windows sold at least <see cref="MinUnits"/> units; otherwise the
    /// raw uplift stands and <see cref="CampaignEffect.SeasonFactor"/> is null. Depth is the discount given over the
    /// pre-discount value, so "extra depth" is how much deeper the campaign went than normal days.
    /// </summary>
    public static CampaignEffect Read(CampaignSegments s)
    {
        var c = s.Campaign;
        var b = s.Before;
        decimal? raw = b.UnitsPerDay > 0 && b.Days > 0 ? c.UnitsPerDay / b.UnitsPerDay : null;
        decimal? season = s.CampaignLastYear.Units >= MinUnits && s.BeforeLastYear.Units >= MinUnits && s.BeforeLastYear.UnitsPerDay > 0
                                                               && s.CampaignLastYear.Days > 0
            ? s.CampaignLastYear.UnitsPerDay / s.BeforeLastYear.UnitsPerDay
            : null;
        decimal? uplift = raw is { } r ? season is { } f && f > 0 ? r / f : r : null;
        decimal? extra = c.DepthPct is { } dc && b.DepthPct is { } db ? dc - db : null;
        return new CampaignEffect(b.UnitsPerDay, c.UnitsPerDay, raw, season, uplift, b.DepthPct, c.DepthPct, extra,
            b.MarginPct, c.MarginPct, b.GpPerDay, c.GpPerDay, c.Units < MinUnits || b.Units < MinUnits);
    }

    /// <summary>
    /// Least squares of ln(uplift) on extra depth through the origin (no extra depth, no extra units): B = Σ d·ln u / Σ d².
    /// Uses campaigns that went at least half a point deeper than normal days and aren't thin. Fewer than three such
    /// campaigns gives no fit (B null).
    /// </summary>
    public static UpliftCurve Fit(IEnumerable<CampaignEffect> effects, string basis)
    {
        var points = effects.Where(e => !e.Thin && e.ExtraDepthPp is > 0.5m && e.Uplift is > 0m)
            .Select(e => (D: (double)(e.ExtraDepthPp!.Value / 100m), LnU: Math.Log((double)e.Uplift!.Value))).ToList();
        if (points.Count < 3)
            return new UpliftCurve(null, points.Count, Array.Empty<(decimal, decimal)>(), basis + $"; {points.Count} usable campaigns, too few to fit (3 needed)");
        var bValue = points.Sum(p => p.D * p.LnU) / points.Sum(p => p.D * p.D);
        var b = Math.Round((decimal)bValue, 4);
        var maxDepth = Math.Round((decimal)points.Max(p => p.D) * 100m, 1);
        var curve = new UpliftCurve(b, points.Count, Array.Empty<(decimal, decimal)>(),
            basis + $"; {points.Count} campaigns, extra depth up to {maxDepth:0.#} pt (deeper is read as {maxDepth:0.#})", maxDepth);
        return curve with { Grid = DepthGrid.Select(d => (d, Math.Round(curve.UpliftAt(d)!.Value, 2))).ToList() };
    }
}
