using PricingTool.Agent.Campaigns;
using PricingTool.Agent.Storage;
using PricingTool.Agent.Tools;

namespace PricingTool.Agent.Backtest;

/// <summary>
/// Campaign candidates as of a past day, from the booked sales lines alone (the engine's runs of that time weren't kept): for each
/// SKU that sold on normal days in the window, its average VAT-inclusive unit price there, its pre-discount price where the line
/// booked one, its booked unit cost incl. VAT, its rate per normal day, and whether it is Gjirafa50-listed. Stock and the
/// dead-stock lane aren't known as of then: every SKU counts as in stock and none as dead stock (said in the result).
/// </summary>
public sealed class AsOfCampaignCandidateSource : ICampaignCandidateSource
{
    private readonly PricingDataReader _reader;
    public AsOfCampaignCandidateSource(PricingDataReader reader) => _reader = reader;

    public Task<IReadOnlyList<CampaignCandidate>> LoadAsync(int layerId, DateOnly from, DateOnly to, CancellationToken ct = default, bool everyInStock = false) =>
        _reader.GetAsOfCandidatesAsync(layerId, from, to, ct);
}

/// <summary>The Për ty reproduction test's verdict (plan section 5): the agents' plan against the hand-set one of 6 Oct 2026.</summary>
public sealed record ReproductionScore(decimal PlanMarginPct, decimal NormalMarginPct, decimal KeptPct, bool MarginWithin, bool KeptWithin, string Detail)
{
    public bool Passed => MarginWithin && KeptWithin;
}

public static class PerTyReproduction
{
    /// <summary>The hand-set plan it is scored against: band targets at 75 % of normal, forecast effective margin 15.1 % (GM3 10.2 %).</summary>
    public const decimal HandMarginPct = 15.1m, HandKeptPct = 75m, MarginTolerancePt = 0.5m, KeptLowPct = 70m, KeptHighPct = 80m;
    /// <summary>The brief the user gave on 6 Oct: GM3 ≥ 10 %, about 14.9 % effective margin.</summary>
    public const decimal BriefMinMarginPct = 14.9m;
    public static readonly DateOnly DataTo = new(2026, 10, 7), Start = new(2026, 10, 14), End = new(2026, 10, 18);
    /// <summary>The hand plan's own base: Mall's own SKUs (not Gjirafa50-listed), booked margin, the 30 days to 6 Oct. Its
    /// analysis of 7 Oct put it at 20.5 % (so 15.1 / 20.5 ≈ 74 % kept); the kept share is reported on both bases.</summary>
    public static readonly DateOnly HandBaseFrom = new(2026, 9, 6);
    public const decimal HandBaseNotedPct = 20.5m;

    /// <summary>
    /// Scores a plan: its expected margin (GP over net revenue, VAT excluded, every SKU in scope) against the hand plan's 15.1 %, and
    /// how much of the same SKUs' normal margin (at their normal-day price, weighted by normal-day revenue) it keeps.
    /// </summary>
    public static ReproductionScore Score(decimal planMarginPct, decimal normalMarginPct)
    {
        var kept = normalMarginPct > 0 ? Math.Round(100m * planMarginPct / normalMarginPct, 0) : 0m;
        var marginOk = Math.Abs(planMarginPct - HandMarginPct) <= MarginTolerancePt;
        var keptOk = kept >= KeptLowPct && kept <= KeptHighPct;
        return new ReproductionScore(planMarginPct, normalMarginPct, kept, marginOk, keptOk,
            $"expected margin {planMarginPct:0.0}% vs the hand plan's {HandMarginPct}% ({(marginOk ? "within" : "outside")} ±{MarginTolerancePt} pt); " +
            $"keeps {kept:0}% of the SKUs' normal margin {normalMarginPct:0.0}% vs the hand plan's {HandKeptPct}% ({(keptOk ? "within" : "outside")} {KeptLowPct}–{KeptHighPct}%)");
    }

    /// <summary>The normal margin of the SKUs a plan prices: (price − cost) / price at the normal-day price, weighted by normal-day revenue.</summary>
    public static decimal NormalMarginPct(IEnumerable<CampaignCandidate> inScope)
    {
        decimal revenue = 0, gp = 0;
        foreach (var c in inScope.Where(c => c.CurrentPrice > 0 && c.UnitsPerDay > 0))
        {
            revenue += c.UnitsPerDay * c.CurrentPrice;
            gp += c.UnitsPerDay * (c.CurrentPrice - c.Cost);
        }
        return revenue > 0 ? Math.Round(100m * gp / revenue, 1) : 0m;
    }

    /// <summary>The brief as the user gave it, for the agents to plan with data cut off at <see cref="DataTo"/>.</summary>
    public static CampaignBrief Brief(int layerId, DateTime nowUtc) => new()
    {
        LayerId = layerId, Name = "Për ty, Oct 2026 (reproduction test, data to 7 Oct)", Kind = "calendar", ScopeKind = "all",
        StartDate = Start, EndDate = End, GoalType = "margin", GoalValue = BriefMinMarginPct, MinMarginPct = BriefMinMarginPct, MaxDepthPct = 25m,
        Exclusions = "[]", Status = BriefStatus.Proposed, IsTest = true, CreatedUtc = nowUtc,
        Rationale = "The user's brief of 2026-10-06: Për ty, GjirafaMall KS, 14-18 Oct 2026, GM3 >= 10 % (GM3 = effective margin - EUR 2.10 per order " +
                    "- marketing, so about 14.9 % effective margin). Reproduction test: data cut off at 7 Oct.",
    };
}
