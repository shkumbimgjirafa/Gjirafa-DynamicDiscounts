namespace PricingTool.Agent.Storage;

/// <summary>Campaign calendar statuses, as stored in ai.CampaignCalendar.</summary>
public static class CampaignStatus
{
    public const string Proposed = "proposed";
    public const string Planned = "planned";
    public const string Live = "live";
    public const string Done = "done";
    /// <summary>Taken off the calendar by a person before it started (kept, never deleted): nothing of it is planned or published.</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>
/// One campaign on one layer (plan section 5, "CampaignCalendar"): its dates, type, scope and goal, and where it came
/// from. The shops' own campaigns are synced from the campaign history (<c>agent.CampaignHistory</c>); the campaign
/// agent adds proposed mini-campaigns. Dates are local shop days, inclusive.
/// </summary>
public class CampaignCalendarEntry
{
    public long Id { get; set; }
    public int LayerId { get; set; }
    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }

    /// <summary>main / mini.</summary>
    public string Type { get; set; } = "main";

    /// <summary>all / category / brand, and the category or brand when not all.</summary>
    public string ScopeKind { get; set; } = "all";
    public string? ScopeValue { get; set; }

    /// <summary>gross-profit / margin / units, and its value (EUR, percent or units). Null when nobody set one.</summary>
    public string? GoalType { get; set; }
    public decimal? GoalValue { get; set; }

    /// <summary>Who set the goal when a person did (it is then binding: the campaign agent's briefs take it as given).</summary>
    public string? GoalSetBy { get; set; }

    /// <summary>proposed / planned / live / done (see <see cref="CampaignStatus"/>).</summary>
    public string Status { get; set; } = CampaignStatus.Planned;

    /// <summary>The campaign history row it was synced from (hk.CampaignWindows), if any.</summary>
    public int? SourceCampaignId { get; set; }

    /// <summary>Who added it: the campaign history (the shop's own campaign), Nova, a person.</summary>
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string? Notes { get; set; }

    /// <summary>What the numbers don't show (a person's or the history's note), e.g. a campaign slowed by warehouse problems.</summary>
    public string? Context { get; set; }

    /// <summary>False when a person says this window isn't what such a campaign normally does: shown with its context, but
    /// left out of the event lift by type and the depth fit, so no goal or plan is anchored on it.</summary>
    public bool Representative { get; set; } = true;

    /// <summary>A person lifted the 25 % depth cap for this campaign (who, where): its depth is bounded by the margin floors and its
    /// margin target instead.</summary>
    public string? DepthCapLiftedBy { get; set; }

    /// <summary>A person asked for every SKU in stock, not only the ones that sold in the last six weeks, and no cap on how many.</summary>
    public bool EverySkuInStock { get; set; }

    /// <summary>A person asked for the engine's discount optimization (the Schedule page's trim of deep displayed discounts) to be off
    /// on this campaign's days: the daily cycle switches the layer's setting off on its first day and back on after.</summary>
    public bool DiscountOptimizationOff { get; set; }

    /// <summary>When the daily cycle switched the layer's discount optimization off for this campaign, so it switches it back on.</summary>
    public DateTime? DiscountOptimizationSwitchedOffUtc { get; set; }
}

/// <summary>Campaign brief statuses, as stored in ai.CampaignBrief.</summary>
public static class BriefStatus
{
    /// <summary>Within the campaign limits: waits for the pricing agent's plan.</summary>
    public const string Proposed = "proposed";
    /// <summary>Breaks a campaign limit nobody can lift (dates, scope, holdout, malformed).</summary>
    public const string Rejected = "rejected";
    /// <summary>Over a cap a person may lift (concurrent mini-campaigns): waits in the exceptions inbox.</summary>
    public const string Held = "held";
    /// <summary>The pricing agent planned it; the brief's thread continues on the plan.</summary>
    public const string Planned = "planned";
    /// <summary>A newer brief for the same calendar campaign replaced it before it was planned.</summary>
    public const string Superseded = "superseded";
}

/// <summary>
/// What the campaign agent (Nova) asks for (plan section 5, "CampaignBrief"): scope, dates, goal and constraints, with
/// its rationale. For a calendar campaign coming up, or a mini-campaign it proposes. Code checks it against the
/// campaign limits before the pricing agent plans it.
/// </summary>
public class CampaignBrief
{
    public long Id { get; set; }
    public long PassId { get; set; }
    public int LayerId { get; set; }
    /// <summary>The calendar entry it is for (a proposed mini-campaign gets one when accepted).</summary>
    public long? CalendarId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>calendar / mini.</summary>
    public string Kind { get; set; } = "";
    public string ScopeKind { get; set; } = "all";
    public string? ScopeValue { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string GoalType { get; set; } = "";
    public decimal GoalValue { get; set; }
    public decimal MinMarginPct { get; set; }
    public decimal MaxDepthPct { get; set; }
    /// <summary>JSON array of exclusions (brands, SKUs, sub-categories) in the agent's words.</summary>
    public string Exclusions { get; set; } = "[]";
    public string Rationale { get; set; } = "";
    public string EvidenceRefs { get; set; } = "[]";
    public string Status { get; set; } = BriefStatus.Proposed;
    public string? Limit { get; set; }
    public string? LimitDetail { get; set; }
    public long? AiCallLogId { get; set; }
    public bool IsTest { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// A finished campaign's result (plan section 5, "CampaignResult"): the numbers code measured (actual vs goal and vs
/// the weeks before) and what the campaign agent takes from them; it feeds the next plan.
/// </summary>
public class CampaignResult
{
    public long Id { get; set; }
    public long CalendarId { get; set; }
    public int LayerId { get; set; }
    public long? PassId { get; set; }
    /// <summary>The measurement, as get_campaign_results returns it (JSON).</summary>
    public string Measured { get; set; } = "{}";
    public string? Lesson { get; set; }
    /// <summary>Ended within the last 14 days: late cancellations still post.</summary>
    public bool Provisional { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>Campaign plan statuses, as stored in ai.CampaignPlan.</summary>
public static class PlanStatus
{
    public const string Proposed = "proposed";
    public const string Accepted = "accepted";
    /// <summary>Nova still wanted changes after the last round: a person decides.</summary>
    public const string Held = "held";
    public const string Superseded = "superseded";
    public const string Published = "published";
    /// <summary>A person stopped it in the veto window, before it went live: its publish rolled back.</summary>
    public const string Stopped = "stopped";

    /// <summary>The plan that stands for a brief: its latest plan that isn't superseded, else its latest. A re-plan nobody
    /// took up (superseded) doesn't replace the plan a person published.</summary>
    public static CampaignPlan? Standing(IEnumerable<CampaignPlan> plans) =>
        plans.Where(p => p.Status != Superseded).MaxBy(p => p.Id) ?? plans.MaxBy(p => p.Id);
}

/// <summary>
/// The pricing agent's plan for a brief (plan section 5, "PlanCampaign"): the strategy it chose among those code computed,
/// code's expected totals and feasibility against the goal, and its explanation. One per round of the brief's thread.
/// </summary>
public class CampaignPlan
{
    public long Id { get; set; }
    public long BriefId { get; set; }
    public long? CalendarId { get; set; }
    public int LayerId { get; set; }
    public int Round { get; set; }
    public string Strategy { get; set; } = "";
    public int DiscountedSkus { get; set; }
    public int SkusInScope { get; set; }
    public int HoldoutSkus { get; set; }
    public decimal ExpectedUnits { get; set; }
    public decimal ExpectedNetRevenue { get; set; }
    public decimal ExpectedGrossProfit { get; set; }
    public decimal ExpectedMarginPct { get; set; }
    /// <summary>The same SKUs over the same days with the campaign's traffic but no discounts.</summary>
    public decimal GrossProfitWithoutDiscounts { get; set; }
    public decimal AverageDepthPct { get; set; }
    public bool Feasible { get; set; }
    public string Feasibility { get; set; } = "";
    /// <summary>JSON: SKUs left out, by reason.</summary>
    public string LeftOut { get; set; } = "{}";
    /// <summary>JSON: every strategy's totals, as code computed them for the agent.</summary>
    public string Alternatives { get; set; } = "[]";
    public string Explanation { get; set; } = "";
    public string Basis { get; set; } = "";
    public string Status { get; set; } = PlanStatus.Proposed;
    public long? PublishBatchId { get; set; }
    /// <summary>reachable / stretch / not-reachable (see Campaigns.GoalVerdicts).</summary>
    public string GoalVerdict { get; set; } = "";
    /// <summary>A plan that went ahead as a stretch on a goal a person set, until that person approves it.</summary>
    public bool WaitsForPerson { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedUtc { get; set; }
    public bool IsTest { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>One discounted SKU of a plan: its campaign price and what code expects it to sell.</summary>
public class CampaignPlanLine
{
    public long Id { get; set; }
    public long PlanId { get; set; }
    public string Sku { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal RegularPrice { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal Cost { get; set; }
    public decimal CampaignPrice { get; set; }
    public decimal DepthPct { get; set; }
    public decimal ExpectedUnits { get; set; }
    public decimal ExpectedGrossProfit { get; set; }
}

/// <summary>One message in a brief's thread (plan section 5: "Every message is stored as a thread"): Nova, Alpha, code or a person.</summary>
public class CampaignMessage
{
    public long Id { get; set; }
    public long BriefId { get; set; }
    public long? PlanId { get; set; }
    public int Round { get; set; }
    public string From { get; set; } = "";
    public string Text { get; set; } = "";
    public long? AiCallLogId { get; set; }
    public DateTime CreatedUtc { get; set; }
}
