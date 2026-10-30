using PricingTool.Agent.Alpha;
using PricingTool.Agent.Storage;
using PricingTool.NopIntegration;

namespace PricingTool.Web.Models;

public sealed record LayerAutonomy(int LayerId, string Name, AutonomyMode Mode);

/// <summary>The Agent page: the switches a person holds, and what the agents did lately.</summary>
public class AgentStatusViewModel
{
    public KillSwitchState KillSwitch { get; init; } = KillSwitchState.Running;
    public IReadOnlyList<LayerAutonomy> Layers { get; init; } = Array.Empty<LayerAutonomy>();
    public PublisherOptions Publisher { get; init; } = new();
    public int Waiting { get; init; }
    public IReadOnlyList<AgentPass> RecentPasses { get; init; } = Array.Empty<AgentPass>();
    public IReadOnlyDictionary<int, string> LayerNames { get; init; } = new Dictionary<int, string>();

    public string LayerName(int layerId) => LayerNames.TryGetValue(layerId, out var n) ? n : $"Layer {layerId}";
}
