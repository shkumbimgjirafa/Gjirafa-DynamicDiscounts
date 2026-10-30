namespace PricingTool.Agent.Tools;

/// <summary>The agents' data tools as Claude Code sees them: MCP server "pricing", tools mcp__pricing__*.</summary>
public static class AgentToolNames
{
    public const string Server = "pricing";

    public const string GetBandPerformance = "get_band_performance";
    public const string GetCostChanges = "get_cost_changes";
    public const string GetStockHealth = "get_stock_health";
    public const string GetPastDecisions = "get_past_decisions";
    public const string GetElasticitySummary = "get_elasticity_summary";
    public const string GetCurrentConfig = "get_current_config";
    public const string QueryData = "query_data";

    public static readonly IReadOnlyList<string> All = new[]
    {
        GetBandPerformance, GetCostChanges, GetStockHealth, GetPastDecisions, GetElasticitySummary, GetCurrentConfig, QueryData,
    };

    /// <summary>The ids passed to <c>--allowedTools</c>: these and nothing else.</summary>
    public static readonly IReadOnlyList<string> AllowedToolIds = All.Select(t => $"mcp__{Server}__{t}").ToArray();
}

/// <summary>Who is calling a data tool: the agent, and the AI call it belongs to.</summary>
public sealed record AgentToolContext(AgentRole Agent, string? CallTag);
