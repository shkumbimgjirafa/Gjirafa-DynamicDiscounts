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
