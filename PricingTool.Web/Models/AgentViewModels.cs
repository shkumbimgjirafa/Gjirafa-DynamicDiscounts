using PricingTool.Agent.Alpha;
using PricingTool.Agent.Storage;
using PricingTool.NopIntegration;

namespace PricingTool.Web.Models;

public sealed record LayerAutonomy(int LayerId, string Name, AutonomyMode Mode);

/// <summary>One line of "What they did": who, when, what, and the reason behind it ("why?"), with a link to its page.</summary>
public sealed record FeedItem(DateTime Utc, string Who, int? LayerId, string What, string? Why, string? Link);

/// <summary>This week's numbers on the Agent tab (the last 7 days, real passes only).</summary>
public sealed record WeekNumbers(int DecisionsApplied, int Held, int Publishes, int DiscountsWritten, int AiCalls, decimal AiCostUsd,
    decimal? MeasuredGp30, int LedgerEntries);

/// <summary>The Agent page: the switches a person holds, and what the agents did lately.</summary>
public class AgentStatusViewModel
{
    public KillSwitchState KillSwitch { get; init; } = KillSwitchState.Running;
    public IReadOnlyList<LayerAutonomy> Layers { get; init; } = Array.Empty<LayerAutonomy>();
    public PublisherOptions Publisher { get; init; } = new();
    public int Waiting { get; init; }
    public IReadOnlyList<AgentPass> RecentPasses { get; init; } = Array.Empty<AgentPass>();
    /// <summary>Per pass: applied decisions that changed a floor or target (a pass's Applied count includes its keeps).</summary>
    public IReadOnlyDictionary<long, int> Changed { get; init; } = new Dictionary<long, int>();
    public IReadOnlyDictionary<int, string> LayerNames { get; init; } = new Dictionary<int, string>();
    public WeekNumbers Week { get; init; } = new(0, 0, 0, 0, 0, 0, null, 0);
    public IReadOnlyList<FeedItem> Feed { get; init; } = Array.Empty<FeedItem>();
    public IReadOnlyList<CampaignRow> ComingUp { get; init; } = Array.Empty<CampaignRow>();
    public IReadOnlyList<AgentQueryLogEntry> Queries { get; init; } = Array.Empty<AgentQueryLogEntry>();
    public IReadOnlyList<AgentLesson> Lessons { get; init; } = Array.Empty<AgentLesson>();
    /// <summary>The chat shown (the latest conversation unless one is picked) and the recent ones to switch to.</summary>
    public long? ConversationId { get; init; }
    public IReadOnlyList<ChatMessage> Chat { get; init; } = Array.Empty<ChatMessage>();
    public IReadOnlyList<(long Id, DateTime StartedUtc, string First)> Conversations { get; init; } = Array.Empty<(long, DateTime, string)>();
    public IReadOnlyList<ChatRule> Rules { get; init; } = Array.Empty<ChatRule>();
    /// <summary>The latest Run week per layer, with its steps; whether one is running in this process.</summary>
    public IReadOnlyList<(RunWeek Week, IReadOnlyList<RunWeekStep> Steps)> RunWeeks { get; init; } = Array.Empty<(RunWeek, IReadOnlyList<RunWeekStep>)>();
    public bool RunWeekRunning { get; init; }

    public string LayerName(int layerId) => LayerNames.TryGetValue(layerId, out var n) ? n : $"Layer {layerId}";
}
