namespace PricingTool.Agent.Storage;

/// <summary>
/// One row per Claude Code call attempt, success or failure (<c>ai.AiCallLog</c>). This is the measured
/// AI-cost line of the financial sheet, and the count of real calls. Only what the call cost and
/// returned is kept; the prompt itself is not stored.
/// </summary>
public class AiCallLogEntry
{
    public long Id { get; set; }
    public DateTime CalledAtUtc { get; set; }

    /// <summary><see cref="AgentRole"/> as text: Alpha / Nova / RunChecker / Chat.</summary>
    public string Agent { get; set; } = "";

    /// <summary>What the call was for, e.g. "floor-target L4" or "isolation-probe".</summary>
    public string Purpose { get; set; } = "";

    /// <summary>1 for the first try, 2 for the one retry.</summary>
    public int Attempt { get; set; }

    /// <summary>The model alias we asked for (<c>--model</c>).</summary>
    public string Model { get; set; } = "";

    /// <summary>The model ids Claude Code reported using (<c>modelUsage</c> keys), comma-separated.</summary>
    public string? ModelIds { get; set; }

    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheCreationTokens { get; set; }

    /// <summary>Our wall clock for the attempt, process start to exit.</summary>
    public int LatencyMs { get; set; }

    /// <summary>Claude Code's own <c>duration_ms</c> and <c>duration_api_ms</c>.</summary>
    public int? DurationMs { get; set; }
    public int? DurationApiMs { get; set; }
    public int? NumTurns { get; set; }

    /// <summary><c>total_cost_usd</c> as Claude Code reports it: API-list-price equivalent. The calls are
    /// billed to the team's subscription, so this is a measured usage figure, not an invoice.</summary>
    public decimal? CostUsd { get; set; }

    public bool Success { get; set; }

    /// <summary><see cref="ClaudeCode.AgentCallStatus"/> as text.</summary>
    public string Status { get; set; } = "";

    /// <summary>Why it failed (schema errors, CLI error, timeout), truncated.</summary>
    public string? Error { get; set; }

    public int PromptChars { get; set; }

    /// <summary>The answer as returned (the JSON, or the raw text when it didn't parse), truncated.</summary>
    public string? Output { get; set; }
}
