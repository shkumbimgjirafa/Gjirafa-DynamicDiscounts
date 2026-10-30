using PricingTool.Agent.Storage;

namespace PricingTool.Agent.Campaigns;

/// <summary>The caps code puts on every campaign (plan section 5, "Publishing and safety for campaigns"). Assumed values, to
/// confirm with the commercial owner.</summary>
public class CampaignLimitOptions
{
    /// <summary>The deepest discount any campaign SKU may get, in percent of its regular price.</summary>
    public decimal MaxDepthPct { get; set; } = 25m;

    /// <summary>The most SKUs one campaign may discount.</summary>
    public int MaxSkusPerCampaign { get; set; } = 2000;

    /// <summary>Mini-campaigns proposed, planned or live at once on one layer.</summary>
    public int MaxConcurrentMiniPerLayer { get; set; } = 2;

    /// <summary>A mini-campaign runs at least this many days and at most <see cref="MaxMiniDays"/>.</summary>
    public int MinDays { get; set; } = 2;
    public int MaxMiniDays { get; set; } = 10;

    /// <summary>A campaign starts at least this many days after the brief (time to plan, publish and veto).</summary>
    public int LeadDays { get; set; } = 3;

    /// <summary>The lowest minimum margin a campaign may keep on any SKU, whatever the brief or the campaign agent's review
    /// says: code raises a lower one to this. The cost watcher's 3 % minimum.</summary>
    public decimal MinMarginFloorPct { get; set; } = 3m;
}

/// <summary>A brief as the campaign agent wrote it, with what code needs to judge it.</summary>
public sealed record BriefInput(
    string Kind, long? CalendarId, string ScopeKind, string? ScopeValue, DateOnly Start, DateOnly End,
    string GoalType, decimal GoalValue, decimal MinMarginPct, decimal MaxDepthPct);

public sealed record BriefVerdict(string Status, decimal MaxDepthPct, string? Limit, string? Detail);

/// <summary>
/// Code, not prompt: what a campaign brief may ask for. Hard rules reject (dates outside the calendar entry, a mini
/// over a main campaign, a holdout category, malformed goals); a cap a person may lift holds (too many mini-campaigns at
/// once); the depth cap is applied by clamping, never by trusting the brief.
/// </summary>
public static class CampaignLimits
{
    public static readonly string[] GoalTypes = { "gross-profit", "margin", "units" };
    public static readonly string[] ScopeKinds = { "all", "category", "brand" };

    /// <param name="calendar">The layer's calendar.</param>
    /// <param name="isHoldout">Whether a category is one of the layer's holdout categories.</param>
    public static BriefVerdict Evaluate(BriefInput b, int layerId, DateOnly today, IReadOnlyList<CampaignCalendarEntry> calendar,
        Func<string, bool> isHoldout, CampaignLimitOptions o)
    {
        var depth = Math.Min(b.MaxDepthPct, o.MaxDepthPct);
        BriefVerdict Reject(string limit, string detail) => new(BriefStatus.Rejected, depth, limit, detail);

        if (!GoalTypes.Contains(b.GoalType) || b.GoalValue <= 0)
            return Reject("malformed", $"Goal '{b.GoalType}' {b.GoalValue}: the type must be one of {string.Join(", ", GoalTypes)} and the value positive.");
        if (b.GoalType == "margin" && b.GoalValue >= 100)
            return Reject("malformed", $"A margin goal of {b.GoalValue}% is impossible.");
        if (!ScopeKinds.Contains(b.ScopeKind) || b.ScopeKind != "all" && string.IsNullOrWhiteSpace(b.ScopeValue))
            return Reject("malformed", $"Scope '{b.ScopeKind}' needs a value (a category or a brand), or 'all'.");
        if (b.MaxDepthPct <= 0 || b.MinMarginPct < 0 || b.MinMarginPct >= 100)
            return Reject("malformed", $"Constraints out of range: max depth {b.MaxDepthPct}%, min margin {b.MinMarginPct}%.");
        if (b.End < b.Start)
            return Reject("malformed", $"Ends {b.End:yyyy-MM-dd} before it starts {b.Start:yyyy-MM-dd}.");
        if (b.Start < today.AddDays(o.LeadDays))
            return Reject("lead-time", $"Starts {b.Start:yyyy-MM-dd}; the earliest start is {today.AddDays(o.LeadDays):yyyy-MM-dd} ({o.LeadDays} days to plan, publish and veto).");
        if (b.ScopeKind == "category" && isHoldout(b.ScopeValue!))
            return Reject("holdout", $"'{b.ScopeValue}' is a holdout category: the control group is never discounted by the agents.");

        if (b.Kind == "calendar")
        {
            var entry = calendar.FirstOrDefault(c => c.Id == b.CalendarId && c.LayerId == layerId);
            if (entry is null)
                return Reject("unknown-campaign", $"Calendar entry {b.CalendarId} isn't a campaign on layer {layerId}.");
            if (entry.Status is CampaignStatus.Done)
                return Reject("campaign-over", $"'{entry.Name}' is over.");
            if (b.Start < entry.StartDate || b.End > entry.EndDate)
                return Reject("outside-calendar", $"{b.Start:yyyy-MM-dd}..{b.End:yyyy-MM-dd} is outside '{entry.Name}' ({entry.StartDate:yyyy-MM-dd}..{entry.EndDate:yyyy-MM-dd}).");
        }
        else if (b.Kind == "mini")
        {
            var days = b.End.DayNumber - b.Start.DayNumber + 1;
            if (days < o.MinDays || days > o.MaxMiniDays)
                return Reject("duration", $"{days} days; a mini-campaign runs {o.MinDays}-{o.MaxMiniDays} days.");
            if (b.ScopeKind == "all")
                return Reject("scope", "A mini-campaign is for a category or a brand, not the whole shop.");
            var main = calendar.FirstOrDefault(c => c.LayerId == layerId && c.Type == "main" && c.Status != CampaignStatus.Done
                                                    && c.StartDate <= b.End && c.EndDate >= b.Start);
            if (main is not null)
                return Reject("overlaps-main", $"Overlaps '{main.Name}' ({main.StartDate:yyyy-MM-dd}..{main.EndDate:yyyy-MM-dd}): a main campaign has the shop's attention.");
            var minis = calendar.Count(c => c.LayerId == layerId && c.Type == "mini" && c.Status is CampaignStatus.Proposed or CampaignStatus.Planned or CampaignStatus.Live
                                            && c.StartDate <= b.End && c.EndDate >= b.Start);
            if (minis >= o.MaxConcurrentMiniPerLayer)
                return new BriefVerdict(BriefStatus.Held, depth, "max-concurrent",
                    $"{minis} mini-campaigns already run in those days (cap {o.MaxConcurrentMiniPerLayer} per layer): a person may allow one more.");
        }
        else
            return Reject("malformed", $"Kind '{b.Kind}' must be calendar or mini.");

        return depth < b.MaxDepthPct
            ? new BriefVerdict(BriefStatus.Proposed, depth, "max-depth", $"Max depth {b.MaxDepthPct}% capped at {o.MaxDepthPct}%.")
            : new BriefVerdict(BriefStatus.Proposed, depth, null, null);
    }
}
