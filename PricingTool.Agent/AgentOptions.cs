namespace PricingTool.Agent;

/// <summary>Who made an AI call. Stored as text in <c>ai.AiCallLog.Agent</c>.</summary>
public enum AgentRole
{
    /// <summary>Alpha, the pricing agent (floors, targets, campaign plans).</summary>
    Alpha,
    /// <summary>Nova, the campaign agent.</summary>
    Nova,
    /// <summary>The run checker: pass / hold / fix on flagged engine rows.</summary>
    RunChecker,
    /// <summary>The Agent tab chat.</summary>
    Chat,
}

/// <summary>
/// Settings for the agents' Claude calls (section "Agent"). The calls go through the team's Claude
/// subscription by running Claude Code headless (<c>claude -p</c>), so there is no API key here.
/// </summary>
public class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>The kill switch. Off: no AI call is made and every caller gets
    /// <see cref="ClaudeCode.AgentCallStatus.Disabled"/>. Off unless configured.</summary>
    public bool Enabled { get; set; }

    /// <summary>The Claude Code executable: a name on PATH or a full path.</summary>
    public string ClaudeExecutable { get; set; } = "claude";

    /// <summary>
    /// Working folder for the <c>claude</c> process. Must be outside the repo, so Claude Code finds no
    /// CLAUDE.md there or above it. Empty = %LOCALAPPDATA%\PricingToolAgent\claude-runtime.
    /// </summary>
    public string RuntimeDirectory { get; set; } = "";

    /// <summary>Wall-clock limit per attempt; the process tree is killed when it passes.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>Passed as <c>--max-budget-usd</c> on every attempt (API-equivalent dollars).</summary>
    public decimal MaxBudgetUsdPerCall { get; set; } = 2m;

    /// <summary>Model alias per role (<c>--model</c>): Alpha and Nova reason, the checker runs on many rows.</summary>
    public string AlphaModel { get; set; } = "opus";
    public string NovaModel { get; set; } = "opus";
    public string RunCheckerModel { get; set; } = "sonnet";
    public string ChatModel { get; set; } = "opus";

    public string ModelFor(AgentRole role) => role switch
    {
        AgentRole.Alpha => AlphaModel,
        AgentRole.Nova => NovaModel,
        AgentRole.RunChecker => RunCheckerModel,
        AgentRole.Chat => ChatModel,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    public string ResolveRuntimeDirectory() =>
        string.IsNullOrWhiteSpace(RuntimeDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PricingToolAgent", "claude-runtime")
            : RuntimeDirectory;
}
