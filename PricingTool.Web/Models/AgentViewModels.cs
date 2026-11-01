using PricingTool.Agent.Alpha;
using PricingTool.Agent.Storage;
using PricingTool.NopIntegration;
using PricingTool.Web.Services;

namespace PricingTool.Web.Models;

public sealed record LayerAutonomy(int LayerId, string Name, AutonomyMode Mode);

/// <summary>One line of what the agents did: who, when, what, and the reason behind it ("why?"), with a link to its page.
/// Lines with the same <paramref name="Group"/> close together (the cost watcher holding 40 SKUs in one sweep) are shown
/// as one line of <paramref name="Count"/>, with each reason under "why?".</summary>
public sealed record FeedItem(DateTime Utc, string Who, int? LayerId, string What, string? Why, string? Link, string? Group = null, int Count = 1);

/// <summary>The week's numbers (the last 7 days, real passes only): Alpha's floor and target decisions applied and held, and
/// the SKUs the cost watcher held for a person (once per layer and SKU).</summary>
public sealed record WeekNumbers(int DecisionsApplied, int Held, int Publishes, int DiscountsWritten, int AiCalls, decimal AiCostUsd,
    decimal? MeasuredGp30, int LedgerEntries, int CostWatcherSkusHeld = 0);

/// <summary>The Run tab: the current run's report for the layer picked, and the switches a person holds over it.</summary>
public class RunPageModel
{
    public RunReport Report { get; init; } = null!;
    public KillSwitchState KillSwitch { get; init; } = KillSwitchState.Running;
    public AutonomyMode Mode { get; init; }
    public PublisherOptions Publisher { get; init; } = new();
    /// <summary>Whether Run week may run this layer (the daily cycle's layers, the ones with data loaded), and whether one is running.</summary>
    public bool CanRunWeek { get; init; }
    public bool RunWeekRunning { get; init; }
    public WeekNumbers Week { get; init; } = new(0, 0, 0, 0, 0, 0, null, 0);
    /// <summary>The Run week whose price list (CSV) this run left, for the download button; null when it has none.</summary>
    public long? PriceListRunWeekId { get; init; }
}

/// <summary>One line of the Chat tab's thread: a chat message, or something an agent did (folded with its reasons).</summary>
public sealed record ThreadItem(DateTime Utc, ChatMessage? Message, FeedItem? Activity);

/// <summary>The Chat tab: one continuous thread of the agents' work and the chat, and what waits for a person beside it.</summary>
public class ChatPageModel
{
    public IReadOnlyList<ThreadItem> Thread { get; init; } = Array.Empty<ThreadItem>();
    /// <summary>The conversation a new message continues (the newest one).</summary>
    public long? ConversationId { get; init; }
    public bool TurnRunning { get; init; }
    /// <summary>Alpha's problem pass is running (from this page).</summary>
    public bool ProblemsRunning { get; init; }
    public string? Answering { get; init; }
    public PricingTool.Agent.Operations.InboxSnapshot Inbox { get; init; } = new();
    public IReadOnlyList<CampaignRow> ComingUp { get; init; } = Array.Empty<CampaignRow>();
    public IReadOnlyList<ChatRule> Rules { get; init; } = Array.Empty<ChatRule>();
    /// <summary>The goals in force on both layers, measured.</summary>
    public IReadOnlyList<PricingTool.Agent.Operations.GoalStatus> Goals { get; init; } = Array.Empty<PricingTool.Agent.Operations.GoalStatus>();
    public IReadOnlyList<AgentLesson> Lessons { get; init; } = Array.Empty<AgentLesson>();
    public IReadOnlyList<AgentQueryLogEntry> Queries { get; init; } = Array.Empty<AgentQueryLogEntry>();
    public IReadOnlyList<LayerAutonomy> Layers { get; init; } = Array.Empty<LayerAutonomy>();
    public IReadOnlyDictionary<int, string> LayerNames { get; init; } = new Dictionary<int, string>();

    public string LayerName(int layerId) => LayerNames.TryGetValue(layerId, out var n) ? n : $"Layer {layerId}";
}
