using PricingTool.Agent.Storage;

namespace PricingTool.Web.Models;

/// <summary>One calendar campaign with its latest brief, plan, publish and measured result.</summary>
public sealed record CampaignRow(CampaignCalendarEntry Entry, CampaignBrief? Brief, CampaignPlan? Plan, PublishBatch? Batch, CampaignResult? Result)
{
    /// <summary>Published to the shop (sandbox in the hackathon), not rolled back, and not live yet: the veto window.</summary>
    public bool CanStop(DateOnly today) => Plan?.Status == PlanStatus.Published && Batch is { RolledBackUtc: null } && Entry.StartDate > today;

    /// <summary>A plan that waits for a person: held (short of a goal a person set, or not accepted after two rounds), or a stretch.</summary>
    public bool Waits => Plan is { } p && (p.Status == PlanStatus.Held || p.WaitsForPerson);

    /// <summary>A plan a person approved where nothing published it (the web app before 31 Oct): publishing it is one click.</summary>
    public bool CanPublish => Plan is { Status: PlanStatus.Accepted, PublishBatchId: null, WaitsForPerson: false, ReviewedBy: not null };

    /// <summary>Where the campaign stands, one word for the calendar's colours: done, live, published, waits, stopped, accepted, briefed or new.</summary>
    public string State(DateOnly today) =>
        Entry.EndDate < today ? "done"
        : Plan?.Status == PlanStatus.Published && Batch is { RolledBackUtc: null } ? (Entry.StartDate <= today ? "live" : "published")
        : Waits ? "waits"
        : Plan?.Status == PlanStatus.Stopped || Plan?.Status == PlanStatus.Published && Batch?.RolledBackUtc is not null ? "stopped"
        : Plan?.Status == PlanStatus.Accepted ? "accepted"
        : Brief is not null ? "briefed" : "new";

    /// <summary>The same, in a sentence for the campaign's card.</summary>
    public string StateText(DateOnly today) => State(today) switch
    {
        "done" => Result is null ? "Over; no result measured yet." : $"Over; measured{(Result.Provisional ? " (provisional: late cancellations still post)" : "")}.",
        "live" => "Live now: its discounts are on the shelf (the sandbox).",
        "published" => $"Published to the sandbox: goes live {Entry.StartDate:ddd dd MMM}. You can stop it until then.",
        "waits" => Plan!.Status == PlanStatus.Held
            ? $"Plan #{Plan.Id} waits for you: approve it as it is, or ask Nova to plan it differently."
            : $"Plan #{Plan.Id} went ahead as a stretch and waits for your approval.",
        "stopped" => "Stopped before it went live: nothing is published. Ask Nova to plan it again.",
        "accepted" => CanPublish
            ? $"Plan #{Plan!.Id} was approved by {Plan.ReviewedBy} but not published: publish it to the sandbox, or ask Nova to plan it differently."
            : $"Plan #{Plan!.Id} accepted, not published yet.",
        "briefed" => $"Nova's brief #{Brief!.Id} is {Brief.Status}; not planned yet.",
        _ => "No brief yet: Nova writes one in its weekly pass, or ask it here.",
    };
}

/// <summary>A campaign drawn on one week of the calendar: from a day column across some days, on its own lane.</summary>
public sealed record CalendarBar(CampaignRow Row, int StartColumn, int Span, int Lane, bool ContinuesBefore, bool ContinuesAfter);

/// <summary>One Monday-to-Sunday week of the calendar with the campaigns on it.</summary>
public sealed record CalendarWeek(DateOnly Monday, IReadOnlyList<CalendarBar> Bars)
{
    public int Lanes => Bars.Count == 0 ? 0 : Bars.Max(b => b.Lane) + 1;
}

/// <summary>
/// The Campaigns tab: six weeks of the calendar, the lists (live, coming up, done), and beside them the campaign picked on the
/// calendar with its conversation with Nova (or, with none picked, the conversation for planning a new one).
/// </summary>
public class CampaignsViewModel
{
    public DateOnly Today { get; init; }
    public DateOnly From { get; init; }
    public IReadOnlyList<CalendarWeek> Weeks { get; init; } = Array.Empty<CalendarWeek>();
    public IReadOnlyList<CampaignRow> Live { get; init; } = Array.Empty<CampaignRow>();
    public IReadOnlyList<CampaignRow> ComingUp { get; init; } = Array.Empty<CampaignRow>();
    public IReadOnlyList<CampaignRow> Done { get; init; } = Array.Empty<CampaignRow>();
    public IReadOnlyDictionary<int, string> LayerNames { get; init; } = new Dictionary<int, string>();

    /// <summary>The campaign picked on the calendar, if any.</summary>
    public CampaignRow? Selected { get; init; }
    /// <summary>The selected campaign's planning thread (Nova, Alpha, code, people), the newest last.</summary>
    public IReadOnlyList<CampaignMessage> PlanningThread { get; init; } = Array.Empty<CampaignMessage>();
    /// <summary>Whether a brief for the selected campaign can still be planned (it starts after the lead time).</summary>
    public bool CanReplan { get; init; }
    public int LeadDays { get; init; }
    public int MinDays { get; init; }
    public int MaxMiniDays { get; init; }

    /// <summary>The conversation with Nova on this tab: about the selected campaign, or about a new one.</summary>
    public IReadOnlyList<ChatMessage> Conversation { get; init; } = Array.Empty<ChatMessage>();
    public bool TurnRunning { get; init; }
    /// <summary>A turn runs elsewhere (the Chat tab or another campaign): one at a time.</summary>
    public bool OtherTurnRunning { get; init; }
    public string? Answering { get; init; }

    public string LayerName(int layerId) => LayerNames.TryGetValue(layerId, out var n) ? n : $"Layer {layerId}";

    /// <summary>A short layer name for the calendar's bars.</summary>
    public static string LayerShort(int layerId) => layerId switch { 1 => "Mall", 4 => "Gj50", _ => $"L{layerId}" };
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
