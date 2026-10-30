using PricingTool.Agent.Storage;

namespace PricingTool.Web.Models;

/// <summary>One calendar campaign with its latest brief, plan, publish and measured result.</summary>
public sealed record CampaignRow(CampaignCalendarEntry Entry, CampaignBrief? Brief, CampaignPlan? Plan, PublishBatch? Batch, CampaignResult? Result)
{
    /// <summary>Published to the shop (sandbox in the hackathon), not rolled back, and not live yet: the veto window.</summary>
    public bool CanStop(DateOnly today) => Plan?.Status == PlanStatus.Published && Batch is { RolledBackUtc: null } && Entry.StartDate > today;
}

/// <summary>The Campaigns tab: the calendar, live / coming up / done, each with its brief, plan and result.</summary>
public class CampaignsViewModel
{
    public DateOnly Today { get; init; }
    public IReadOnlyList<CampaignRow> Live { get; init; } = Array.Empty<CampaignRow>();
    public IReadOnlyList<CampaignRow> ComingUp { get; init; } = Array.Empty<CampaignRow>();
    public IReadOnlyList<CampaignRow> Done { get; init; } = Array.Empty<CampaignRow>();
    public IReadOnlyDictionary<int, string> LayerNames { get; init; } = new Dictionary<int, string>();

    public string LayerName(int layerId) => LayerNames.TryGetValue(layerId, out var n) ? n : $"Layer {layerId}";
}

/// <summary>One campaign's card: every brief and plan for it, the agents' thread, the plan's biggest lines and the result.</summary>
public class CampaignDetailViewModel
{
    public CampaignCalendarEntry Entry { get; init; } = new();
    public string LayerName { get; init; } = "";
    public DateOnly Today { get; init; }
    public IReadOnlyList<CampaignBrief> Briefs { get; init; } = Array.Empty<CampaignBrief>();
    public IReadOnlyList<CampaignPlan> Plans { get; init; } = Array.Empty<CampaignPlan>();
    public IReadOnlyDictionary<long, PublishBatch> Batches { get; init; } = new Dictionary<long, PublishBatch>();
    public IReadOnlyList<CampaignMessage> Thread { get; init; } = Array.Empty<CampaignMessage>();
    public IReadOnlyList<CampaignPlanLine> TopLines { get; init; } = Array.Empty<CampaignPlanLine>();
    public CampaignResult? Result { get; init; }
    public IReadOnlyList<CampaignPastWindow> PastWindows { get; init; } = Array.Empty<CampaignPastWindow>();
}

/// <summary>A past window of the same campaign type, for the card's "what it did before".</summary>
public sealed record CampaignPastWindow(string Name, DateOnly Start, DateOnly End, string? Context, bool Representative);
