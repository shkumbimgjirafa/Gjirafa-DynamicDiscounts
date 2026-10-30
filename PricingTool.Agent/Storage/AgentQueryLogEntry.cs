namespace PricingTool.Agent.Storage;

/// <summary>
/// One row per data-tool call an agent made (<c>ai.AgentQueryLog</c>): its own SQL through
/// <c>query_data</c>, or one of the fixed tools with its arguments. This is "what it looked at" on the
/// Agent tab, and the evidence behind each decision.
/// </summary>
public class AgentQueryLogEntry
{
    public long Id { get; set; }
    public DateTime CalledAtUtc { get; set; }

    /// <summary><see cref="AgentRole"/> as text.</summary>
    public string Agent { get; set; } = "";

    /// <summary>Ties the row to the AI call that made it (<see cref="AiCallLogEntry.CallTag"/>).</summary>
    public string? CallTag { get; set; }

    /// <summary>query_data, or the fixed tool's name (get_band_performance, ...).</summary>
    public string Tool { get; set; } = "";

    /// <summary>The SQL as the agent wrote it, or the fixed tool's arguments as JSON.</summary>
    public string Request { get; set; } = "";

    /// <summary>False when the guardrails refused it before it ran.</summary>
    public bool Accepted { get; set; }

    public int? RowCount { get; set; }

    /// <summary>True when more rows existed than the cap returned.</summary>
    public bool Truncated { get; set; }

    public int DurationMs { get; set; }

    /// <summary>Why it was refused, or the SQL error the agent got back.</summary>
    public string? Error { get; set; }
}
