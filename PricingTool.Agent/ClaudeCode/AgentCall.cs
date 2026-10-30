using System.Text.Json;

namespace PricingTool.Agent.ClaudeCode;

public enum AgentCallStatus
{
    Succeeded,
    /// <summary>The kill switch (<c>Agent:Enabled</c>) is off; no call was made.</summary>
    Disabled,
    /// <summary>The answer was not JSON, or did not match the schema, on both attempts.</summary>
    InvalidOutput,
    /// <summary>Claude Code failed: it could not start, exited with an error, or reported one.</summary>
    CliError,
    /// <summary>No answer within the timeout; the process tree was killed.</summary>
    TimedOut,
}

/// <summary>One question to an agent.</summary>
/// <param name="Role">Who is asking; picks the model and labels the log row.</param>
/// <param name="Purpose">Short label for the log, e.g. "floor-target L4".</param>
/// <param name="Prompt">The question and its data. Sent on stdin, never on the command line.</param>
/// <param name="SystemPromptFile">The agent's fixed rules. Replaces Claude Code's own system prompt.</param>
public sealed record AgentCallRequest(AgentRole Role, string Purpose, string Prompt, string SystemPromptFile)
{
    /// <summary>JSON Schema for the answer. Passed as <c>--json-schema</c> and checked again in code.
    /// Null = free text (returned as a JSON string).</summary>
    public string? JsonSchema { get; init; }

    /// <summary>More files appended to the system prompt, e.g. the data dictionary. The result is identical
    /// on every call, so it stays prompt-cached.</summary>
    public IReadOnlyList<string> AppendSystemPromptFiles { get; init; } = Array.Empty<string>();

    /// <summary>Give the agent our read-only data tools and <c>query_data</c> (PricingTool.AgentTools over
    /// MCP, only those tools allowed). Every query it runs is logged under this call's tag.</summary>
    public bool UseDataTools { get; init; }

    /// <summary>Any other MCP config file (<c>--mcp-config</c>). Other MCP servers are never loaded
    /// (<c>--strict-mcp-config</c> is always on).</summary>
    public string? McpConfigFile { get; init; }

    /// <summary>Tools the agent may call without a prompt (<c>--allowedTools</c>), e.g. our MCP tools.</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();

    /// <summary>Overrides <see cref="AgentOptions.MaxBudgetUsdPerCall"/> for this call.</summary>
    public decimal? MaxBudgetUsd { get; init; }
}

/// <summary>
/// What came back. Only <see cref="AgentCallStatus.Succeeded"/> carries an output, already checked
/// against the schema; on any other status the caller holds or blocks, and never publishes.
/// </summary>
public sealed record AgentCallResult(AgentCallStatus Status, JsonElement? Output, string? Error, IReadOnlyList<long> CallLogIds)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public bool Succeeded => Status == AgentCallStatus.Succeeded;

    /// <summary>The tag on this call's AiCallLog rows and on the data-tool calls it made (AgentQueryLog).</summary>
    public string? CallTag { get; init; }

    public T? Deserialize<T>(JsonSerializerOptions? options = null) =>
        Output is { } output ? output.Deserialize<T>(options ?? Web) : default;
}
