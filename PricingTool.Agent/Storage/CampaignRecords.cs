namespace PricingTool.Agent.Storage;

/// <summary>Campaign calendar statuses, as stored in ai.CampaignCalendar.</summary>
public static class CampaignStatus
{
    public const string Proposed = "proposed";
    public const string Planned = "planned";
    public const string Live = "live";
    public const string Done = "done";
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

    /// <summary>proposed / planned / live / done (see <see cref="CampaignStatus"/>).</summary>
    public string Status { get; set; } = CampaignStatus.Planned;

    /// <summary>The campaign history row it was synced from (hk.CampaignWindows), if any.</summary>
    public int? SourceCampaignId { get; set; }

    /// <summary>Who added it: the campaign history (the shop's own campaign), Nova, a person.</summary>
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string? Notes { get; set; }
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
