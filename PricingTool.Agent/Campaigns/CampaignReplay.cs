using PricingTool.Agent.Tools;

namespace PricingTool.Agent.Campaigns;

/// <summary>One SKU in one past campaign: its sales on the normal days of the planner's rate window, and in the campaign.</summary>
public sealed record ReplaySkuRow(int CampaignId, string Sku, decimal NormalUnits, decimal NormalNet, decimal NormalGp,
    decimal CampaignUnits, decimal CampaignNet, decimal CampaignGp);

/// <summary>A past campaign replayed with the planner's own arithmetic, against what it really made.</summary>
public sealed record CampaignReplayResult(int CampaignId, string Name, string Type, DateOnly Start, int Days, int NormalDays, bool Representative,
    decimal ActualGp, decimal SeenActualGp, decimal TailGp, int TailSkus, int SeenSkus, decimal PredictedSeenGp, decimal? EventLift)
{
    /// <summary>The share of the campaign's gross profit from SKUs with no normal-day sales in the rate window: the planner can't see them.</summary>
    public decimal TailSharePct => ActualGp > 0 ? Math.Round(100m * TailGp / ActualGp, 1) : 0;
    /// <summary>Actual over predicted on the SKUs the planner sees.</summary>
    public decimal? SeenRatio => PredictedSeenGp > 0 ? Math.Round(SeenActualGp / PredictedSeenGp, 2) : null;
    /// <summary>The whole campaign's actual gross profit over the planner's prediction: the calibration factor.</summary>
    public decimal? TotalRatio => PredictedSeenGp > 0 ? Math.Round(ActualGp / PredictedSeenGp, 2) : null;
    /// <summary>Enough normal days before it to read a rate, and a person didn't mark it unrepresentative.</summary>
    public bool Usable => Representative && NormalDays >= CampaignReplay.MinNormalDays && TotalRatio is not null;
}

/// <summary>How far the planner's arithmetic lands from what past campaigns really made (median of the usable replays).</summary>
public sealed record CampaignCalibration(decimal Factor, int Campaigns, decimal Low, decimal High, decimal TailSharePct, string Basis);

/// <summary>
/// Backtest lever 7's measuring stick (plan section 5, "Backtester"): each finished campaign is replayed the way the planner
/// would have planned it a week before (no look-ahead in the sales: the rate comes from the normal days of the 28 days that end
/// 14 days before planning), at the depth the campaign actually used, and scored on its booked sales lines (booked COGS).
/// Expected units per SKU = normal-day rate × days × the campaign type's event lift × what the extra depth buys; gross profit
/// at the campaign's own unit economics. The event lift and the depth fit come from the whole campaign history, the replayed
/// campaign included (in-sample: said wherever the factor is shown).
/// </summary>
public static class CampaignReplay
{
    public const int LeadDays = 7, LagDays = 14, RateDays = 28, MinNormalDays = 14, MinCampaigns = 3;

    /// <summary>The planner's rate window for a campaign starting on <paramref name="start"/>, planned <see cref="LeadDays"/> days before.</summary>
    public static DateWindow RateWindow(DateOnly start) => DateWindow.Ending(start.AddDays(-LeadDays), RateDays, LagDays);

    public static CampaignReplayResult Replay(CampaignWindow w, int normalDays, IEnumerable<ReplaySkuRow> rows, DepthEffect depth, bool representative)
    {
        var days = w.EndDate.DayNumber - w.StartDate.DayNumber + 1;
        var type = CampaignMath.TypeOf(w.Name);
        var lift = depth.EventLift(type);
        decimal actual = 0, seenActual = 0, tail = 0, predicted = 0;
        int tailSkus = 0, seenSkus = 0;
        foreach (var r in rows)
        {
            actual += r.CampaignGp;
            if (r.NormalUnits <= 0 || normalDays <= 0)
            {
                if (r.CampaignUnits > 0) { tail += r.CampaignGp; tailSkus++; }
                continue;
            }
            seenSkus++;
            seenActual += r.CampaignGp;
            var sold = r.CampaignUnits > 0;
            var unitGp = sold ? r.CampaignGp / r.CampaignUnits : r.NormalGp / r.NormalUnits;
            var extraDepth = sold && r.NormalNet > 0
                ? Math.Clamp(100m * (1m - r.CampaignNet / r.CampaignUnits / (r.NormalNet / r.NormalUnits)), 0m, 80m) : 0m;
            predicted += r.NormalUnits / normalDays * days * (lift ?? 1m) * (depth.DepthMultiplier(extraDepth) ?? 1m) * unitGp;
        }
        return new CampaignReplayResult(w.CampaignId, w.Name, type, w.StartDate, days, normalDays, representative, Math.Round(actual, 0), Math.Round(seenActual, 0),
            Math.Round(tail, 0), tailSkus, seenSkus, Math.Round(predicted, 0), lift);
    }

    /// <summary>The median factor over the usable replays, or null with fewer than <see cref="MinCampaigns"/>.</summary>
    public static CampaignCalibration? Calibrate(IEnumerable<CampaignReplayResult> results)
    {
        var usable = results.Where(r => r.Usable).ToList();
        if (usable.Count < MinCampaigns) return null;
        var ratios = usable.Select(r => r.TotalRatio!.Value).OrderBy(x => x).ToList();
        var tails = usable.Select(r => r.TailSharePct).OrderBy(x => x).ToList();
        var factor = Median(ratios);
        return new CampaignCalibration(factor, usable.Count, ratios[0], ratios[^1], Median(tails),
            $"median of {usable.Count} past campaigns replayed with the planner's arithmetic (actual GP / planned GP, x{ratios[0]:0.00}..x{ratios[^1]:0.00}); " +
            $"a median {Median(tails):0}% of a campaign's GP came from SKUs with no normal-day sales in the weeks before, which no rate can see; " +
            "event lift and depth fit in-sample; estimated");
    }

    private static decimal Median(IReadOnlyList<decimal> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : Math.Round((sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2m, 2);
}
