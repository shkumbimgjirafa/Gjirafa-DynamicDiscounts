using PricingTool.Agent.Campaigns;
using PricingTool.Agent.Tools;

namespace PricingTool.Agent.Backtest;

/// <summary>Lever 7 on one past campaign: the planner's depth per SKU against the depth the campaign used.</summary>
public sealed record CampaignDepthGain(int CampaignId, string Name, int Days, string Strategy, decimal B, decimal ActualGp, decimal PlannedGp,
    int SkusScored, int SkusChanged, decimal ActualDepthPct, decimal PlannedDepthPct)
{
    /// <summary>The uplift curve's scale in this case: 1 as fitted (leave-one-out), 0.5 and 2 for the sensitivity.</summary>
    public decimal BScale { get; init; } = 1m;
    public bool Representative { get; init; } = true;
    /// <summary>Ended less than 14 days ago: late cancellations still post.</summary>
    public bool Provisional { get; init; }

    public decimal Gain => PlannedGp - ActualGp;
    public decimal GainPerDay => Days > 0 ? Math.Round(Gain / Days, 0) : 0;
}

/// <summary>
/// Backtest lever 7 (plan section 5): a past campaign replayed on its own sales lines with the planner setting each SKU's depth
/// instead of one blanket level, **keeping the campaign as deep on average as it was** (revenue-weighted): the uplift curve is
/// fitted within campaigns, so it can say which SKUs a discount pays on, but not what a shallower campaign does to the event
/// itself; a replay that took the discount away would claim the event lift for free.
/// Depth is against the SKU's normal-day unit price before the campaign (GjirafaMall stopped booking discounts in Nov 2025, so
/// depth is read from prices on both layers). Units at another depth follow the uplift curve from what the SKU actually sold:
/// units × exp(B × (planned − actual depth)), deeper capped at the deepest the fit has seen. Never under the booked unit cost,
/// never past the depth cap. Each SKU starts at its most profitable grid depth; while the campaign is shallower on average than
/// it was, the SKU whose next deeper step costs the least gross profit per point of weighted depth goes one step deeper.
/// SKUs with no normal-day sales before, or none in the campaign, keep what happened. "reallocate" allows any grid depth;
/// "visible" at least 5 % off on every SKU. Estimated.
/// </summary>
public static class CampaignDepthBacktest
{
    public static readonly decimal[] Grid = { 0m, 5m, 10m, 15m, 20m, 25m };
    public static readonly string[] Strategies = { "reallocate", "visible" };

    public static CampaignDepthGain Score(CampaignWindow w, IEnumerable<ReplaySkuRow> rows, DepthEffect depth, string strategy, decimal maxDepthPct = 25m,
        decimal bScale = 1m)
    {
        var b = (depth.B ?? 0m) * bScale;
        var days = w.EndDate.DayNumber - w.StartDate.DayNumber + 1;
        decimal actual = 0, unscored = 0;
        var skus = new List<(decimal ActualDepth, decimal Weight, List<(decimal Depth, decimal Gp)> Options, int Choice)>();
        foreach (var r in rows.Where(r => r.CampaignUnits > 0))
        {
            actual += r.CampaignGp;
            if (r.NormalUnits <= 0 || r.NormalNet <= 0 || r.CampaignNet <= 0) { unscored += r.CampaignGp; continue; }
            var normalPrice = r.NormalNet / r.NormalUnits;
            var unitCost = (r.CampaignNet - r.CampaignGp) / r.CampaignUnits;
            var actualDepth = 100m * (1m - r.CampaignNet / r.CampaignUnits / normalPrice);
            var options = new List<(decimal Depth, decimal Gp)>();
            foreach (var d in Grid.Where(d => d <= maxDepthPct && (strategy != "visible" || d >= 5m)))
            {
                var price = normalPrice * (1m - d / 100m);
                if (price < unitCost) continue;
                var delta = d - actualDepth;
                var multiplier = (decimal)Math.Exp((double)(b * (delta > 0 ? Math.Min(delta, depth.MaxDepthPct) : delta) / 100m));
                options.Add((d, r.CampaignUnits * multiplier * (price - unitCost)));
            }
            if (options.Count == 0) { unscored += r.CampaignGp; continue; }
            skus.Add((actualDepth, r.CampaignNet, options, options.IndexOf(options.MaxBy(o => o.Gp))));
        }

        var totalWeight = skus.Sum(s => s.Weight);
        var target = totalWeight > 0 ? skus.Sum(s => s.ActualDepth * s.Weight) / totalWeight : 0m;
        decimal Average() => totalWeight > 0 ? skus.Sum(s => s.Options[s.Choice].Depth * s.Weight) / totalWeight : 0m;
        var average = Average();
        while (average < target)
        {
            var best = -1;
            var bestCost = decimal.MaxValue;
            for (var i = 0; i < skus.Count; i++)
            {
                var s = skus[i];
                if (s.Choice + 1 >= s.Options.Count) continue;
                var (now, next) = (s.Options[s.Choice], s.Options[s.Choice + 1]);
                var cost = (now.Gp - next.Gp) / ((next.Depth - now.Depth) * s.Weight);
                if (cost < bestCost) (best, bestCost) = (i, cost);
            }
            if (best < 0) break;
            var chosen = skus[best];
            average += (chosen.Options[chosen.Choice + 1].Depth - chosen.Options[chosen.Choice].Depth) * chosen.Weight / totalWeight;
            skus[best] = chosen with { Choice = chosen.Choice + 1 };
        }

        var planned = unscored + skus.Sum(s => s.Options[s.Choice].Gp);
        var changed = skus.Count(s => Math.Abs(s.Options[s.Choice].Depth - s.ActualDepth) >= 2.5m);
        return new CampaignDepthGain(w.CampaignId, w.Name, days, strategy, b, Math.Round(actual, 0), Math.Round(planned, 0), skus.Count, changed,
            Math.Round(target, 1), Math.Round(Average(), 1)) { BScale = bScale };
    }
}
