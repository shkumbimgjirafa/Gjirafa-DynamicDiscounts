using PricingTool.Core.Domain;
using PricingTool.Core.Services;

namespace PricingTool.Agent.Campaigns;

/// <summary>One SKU the planner may put in a campaign, from the layer's latest engine run and its booked sales.
/// Prices and cost are VAT-inclusive in the layer's currency; <see cref="UnitsPerDay"/> is its normal-day rate.</summary>
public sealed record CampaignCandidate(
    string Sku, int? BandId, string Category, string? Brand, decimal RegularPrice, decimal CurrentPrice, decimal Cost,
    decimal UnitsPerDay, int LocalStock, int SupplierStock, bool DeadStock, bool FollowsGj50);

/// <summary>What a plan does with one SKU: its campaign price (null = not discounted) and what code expects it to sell.</summary>
public sealed record PlanLine(string Sku, string Category, decimal RegularPrice, decimal CurrentPrice, decimal Cost, decimal? CampaignPrice,
    decimal DepthPct, decimal ExpectedUnits, decimal ExpectedGrossProfit, decimal ExpectedNetRevenue, decimal BaselineGrossProfit, bool CampaignHoldout);

public sealed record PlanTotals(int Discounted, int InScope, decimal Units, decimal NetRevenue, decimal GrossProfit, decimal MarginPct,
    decimal GrossProfitWithoutDiscounts, decimal AverageDepthPct);

public sealed record PlanResult(string Strategy, PlanTotals Totals, IReadOnlyList<PlanLine> Lines, IReadOnlyDictionary<string, int> LeftOut,
    bool Feasible, string Feasibility, IReadOnlyList<PlanLine> TopContributors);

/// <summary>The brief as code plans it.</summary>
public sealed record PlanSpec(long CampaignKey, int Days, string GoalType, decimal GoalValue, decimal MinMarginPct, decimal MaxDepthPct,
    decimal VatRatePct, decimal EventLift, IReadOnlyCollection<string> ExcludedBrands);

/// <summary>
/// <c>PlanCampaign</c>'s arithmetic (plan section 5): code computes, the agent chooses among the strategies and explains.
/// For each SKU in scope, at each depth of the grid (5–25 % off the regular price, never past the brief's maximum, charm-
/// rounded down, never below cost, never at or above today's price), expected units over the campaign = normal-day rate ×
/// days × the campaign type's event lift × exp(B × extra depth over today's discount) (<see cref="DepthEffect"/>); gross
/// profit and net revenue follow, VAT excluded. Totals cover every SKU in scope, discounted or not, so they compare with
/// a past campaign's whole result. Three strategies:
/// <list type="bullet">
/// <item>max-gross-profit: each SKU at the depth with the most gross profit, or not discounted when none beats today's price;</item>
/// <item>volume-at-no-loss: each SKU at the deepest depth that keeps its gross profit at least as high as undiscounted;</item>
/// <item>visible: every SKU at least the shallowest depth, deeper where that pays (a campaign shoppers can see).</item>
/// </list>
/// Then the brief's minimum blended margin is enforced (the SKUs costing the most margin per euro of profit are made
/// shallower first), the cap on discounted SKUs keeps the biggest contributors, and a fixed ~10 % of eligible SKUs stay
/// out of every campaign as its holdout (the A/B test of its lift).
/// </summary>
public static class CampaignPlanner
{
    public static readonly string[] Strategies = { "max-gross-profit", "volume-at-no-loss", "visible" };
    private static readonly RoundingService Rounding = new();
    public const int HoldoutPercent = 10;

    public static PlanResult Plan(string strategy, PlanSpec spec, IReadOnlyList<CampaignCandidate> candidates, DepthEffect depth,
        IReadOnlyDictionary<int, PriceBandConfig> bands, int maxSkus, IReadOnlyDictionary<string, int> leftOutBefore)
    {
        var leftOut = new Dictionary<string, int>(leftOutBefore);
        void Out(string why) => leftOut[why] = leftOut.GetValueOrDefault(why) + 1;
        var vat = 1m + spec.VatRatePct / 100m;
        var grid = CampaignMath.DepthGrid.Where(d => d <= spec.MaxDepthPct + 0.001m).ToList();

        var lines = new List<PlanLine>();
        var options = new Dictionary<string, List<PlanLine>>();
        var undiscounted = new Dictionary<string, PlanLine>();
        foreach (var c in candidates)
        {
            var baseUnits = c.UnitsPerDay * spec.Days * spec.EventLift;
            var baseGp = baseUnits * (c.CurrentPrice - c.Cost) / vat;
            var none = new PlanLine(c.Sku, c.Category, c.RegularPrice, c.CurrentPrice, c.Cost, null, 0, baseUnits, baseGp, baseUnits * c.CurrentPrice / vat, baseGp, false);
            undiscounted[c.Sku] = none;
            if (spec.ExcludedBrands.Contains(c.Brand ?? "", StringComparer.OrdinalIgnoreCase)) { Out("excluded by the brief"); lines.Add(none); continue; }
            if (IsHoldout(spec.CampaignKey, c.Sku)) { Out("campaign holdout (10 %)"); lines.Add(none with { CampaignHoldout = true }); continue; }

            var currentDepth = c.RegularPrice > 0 ? 1m - c.CurrentPrice / c.RegularPrice : 0m;
            var rounding = c.BandId is { } b && bands.TryGetValue(b, out var band) && band.RoundingEnabled ? band.Rounding : RoundingConvention.None;
            var at = new List<PlanLine>();
            foreach (var d in grid)
            {
                var raw = c.RegularPrice * (1m - d / 100m);
                // The engine's own charm rounding (GjmCharm, Gj50Charm, …), bounded by cost and a cent under today's price.
                var upper = c.CurrentPrice - 0.01m;
                if (upper < c.Cost) continue;
                var price = rounding == RoundingConvention.None
                    ? Math.Floor(raw * 100m) / 100m
                    : Rounding.Apply(Math.Clamp(raw, c.Cost, upper), rounding, new PriceBounds(c.Cost, upper)).Price;
                if (price >= c.CurrentPrice || price < c.Cost || price <= 0) continue;
                var extraPp = 100m * ((1m - price / c.RegularPrice) - currentDepth);
                var units = baseUnits * (depth.DepthMultiplier(extraPp) ?? 1m);
                at.Add(new PlanLine(c.Sku, c.Category, c.RegularPrice, c.CurrentPrice, c.Cost, price, Math.Round(100m * (1m - price / c.RegularPrice), 1),
                    units, units * (price - c.Cost) / vat, units * price / vat, baseGp, false));
            }
            if (at.Count == 0) { Out("no grid depth below today's price and above cost"); lines.Add(none); continue; }
            options[c.Sku] = at;

            var pick = strategy switch
            {
                "max-gross-profit" => at.Where(l => l.ExpectedGrossProfit > baseGp).MaxBy(l => l.ExpectedGrossProfit),
                "volume-at-no-loss" => at.Where(l => l.ExpectedGrossProfit >= baseGp).MaxBy(l => l.DepthPct),
                "visible" => at.Where(l => l.ExpectedGrossProfit > baseGp).MaxBy(l => l.ExpectedGrossProfit) ?? at.MinBy(l => l.DepthPct),
                _ => throw new ArgumentException($"Unknown strategy '{strategy}'."),
            };
            if (pick is null) Out("no depth beats today's price on gross profit");
            lines.Add(pick ?? none);
        }

        // The brief's minimum blended margin: make the costliest discounts shallower until it holds.
        for (var guard = 0; Margin(lines) < spec.MinMarginPct && guard < 100_000; guard++)
        {
            var step = lines.Select((l, i) => (l, i)).Where(x => x.l.CampaignPrice is not null)
                .Select(x =>
                {
                    var back = options[x.l.Sku].Where(o => o.DepthPct < x.l.DepthPct).MaxBy(o => o.DepthPct) ?? undiscounted[x.l.Sku];
                    var gpLoss = x.l.ExpectedGrossProfit - back.ExpectedGrossProfit;
                    return (x.i, back, cost: gpLoss / Math.Max(0.01m, x.l.DepthPct - back.DepthPct));
                }).OrderBy(x => x.cost).FirstOrDefault();
            if (step.back is null) break;                                      // nothing discounted left to make shallower
            if (step.back.CampaignPrice is null) Out("made undiscounted to hold the minimum margin");
            lines[step.i] = step.back;
        }

        // The cap on discounted SKUs: the biggest contributors stay.
        var discounted = lines.Where(l => l.CampaignPrice is not null).OrderByDescending(l => l.ExpectedGrossProfit).ToList();
        foreach (var l in discounted.Skip(maxSkus))
        {
            lines[lines.IndexOf(l)] = undiscounted[l.Sku];
            Out($"over the cap of {maxSkus} discounted SKUs");
        }

        var totals = Totals(lines, vat);
        var (feasible, why) = Feasibility(spec, totals);
        return new PlanResult(strategy, totals, lines, leftOut, feasible, why,
            lines.Where(l => l.CampaignPrice is not null).OrderByDescending(l => l.ExpectedGrossProfit).Take(10).ToList());
    }

    /// <summary>A fixed ~10 % of SKUs per campaign (by a stable hash of the campaign and the SKU), so a re-plan keeps the same holdout.</summary>
    public static bool IsHoldout(long campaignKey, string sku)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var ch in $"{campaignKey}:{sku}") h = (h ^ ch) * 16777619;
            return h % 100 < HoldoutPercent;
        }
    }

    private static decimal Margin(IReadOnlyList<PlanLine> lines)
    {
        var net = lines.Sum(l => l.ExpectedNetRevenue);
        return net > 0 ? 100m * lines.Sum(l => l.ExpectedGrossProfit) / net : 100m;
    }

    private static PlanTotals Totals(IReadOnlyList<PlanLine> lines, decimal vat)
    {
        var discounted = lines.Where(l => l.CampaignPrice is not null).ToList();
        var net = lines.Sum(l => l.ExpectedNetRevenue);
        return new PlanTotals(discounted.Count, lines.Count, Math.Round(lines.Sum(l => l.ExpectedUnits), 0), Math.Round(net, 0),
            Math.Round(lines.Sum(l => l.ExpectedGrossProfit), 0), net > 0 ? Math.Round(100m * lines.Sum(l => l.ExpectedGrossProfit) / net, 1) : 0,
            Math.Round(lines.Sum(l => l.BaselineGrossProfit), 0),
            discounted.Count > 0 ? Math.Round(discounted.Average(l => l.DepthPct), 1) : 0);
    }

    private static (bool, string) Feasibility(PlanSpec spec, PlanTotals t)
    {
        var (actual, unit) = spec.GoalType switch
        {
            "gross-profit" => (t.GrossProfit, "EUR gross profit"),
            "units" => (t.Units, "units"),
            "margin" => (t.MarginPct, "% margin"),
            _ => (0m, spec.GoalType),
        };
        if (t.MarginPct < spec.MinMarginPct)
            return (false, $"The blended margin of {t.MarginPct:0.0}% stays under the brief's minimum of {spec.MinMarginPct:0.#}% even with the discounts made shallower.");
        return actual >= spec.GoalValue
            ? (true, $"{spec.GoalValue:N0} {unit} is reachable: the plan expects {actual:N0}.")
            : (false, $"{spec.GoalValue:N0} {unit} is not reachable within the limits: the best this strategy expects is {actual:N0}.");
    }
}
