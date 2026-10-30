namespace PricingTool.Agent.Storage;

/// <summary>One message of a chat with the agents (plan section 5, "AgentChat"): a person, Nova, Alpha or code.</summary>
public class ChatMessage
{
    public long Id { get; set; }
    public long ConversationId { get; set; }
    /// <summary>user, Nova, Alpha or code.</summary>
    public string From { get; set; } = "";
    /// <summary>The signed-in person who wrote it (user messages only).</summary>
    public string? Author { get; set; }
    public string Text { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public long? AiCallLogId { get; set; }
    /// <summary>The campaign brief this message created, if any.</summary>
    public long? BriefId { get; set; }
    /// <summary>The rule this message created, if any.</summary>
    public long? RuleId { get; set; }
}

/// <summary>
/// A person's instruction in the chat, turned into a structured rule that code enforces (plan section 5: "Instructions … each becomes
/// a structured rule (layer, scope, constraint, dates) that SafetyLimits enforces in code"). Rules only tighten: they keep a brand or a
/// category out of campaigns, or raise the minimum margin campaigns keep, between two dates. Listed on the Agent tab; removable there.
/// </summary>
public class ChatRule
{
    public long Id { get; set; }
    /// <summary>Null: every layer.</summary>
    public int? LayerId { get; set; }
    /// <summary>See <see cref="ChatRuleKinds"/>.</summary>
    public string Kind { get; set; } = "";
    /// <summary>The brand or category (exclusions), or the margin in percent (min-campaign-margin).</summary>
    public string Value { get; set; } = "";
    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
    /// <summary>The person's words it came from.</summary>
    public string Instruction { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime? RemovedUtc { get; set; }
    public string? RemovedBy { get; set; }

    public bool ActiveOn(DateOnly from, DateOnly to) => RemovedUtc is null && FromDate <= to && ToDate >= from;
}

public static class ChatRuleKinds
{
    public const string ExcludeBrand = "exclude-brand", ExcludeCategory = "exclude-category", MinCampaignMargin = "min-campaign-margin";
    public static readonly string[] All = { ExcludeBrand, ExcludeCategory, MinCampaignMargin };
}

/// <summary>One stored run of the whole backtest (see Backtest.BacktestSnapshotBuilder), shown on the results page and as CSV.</summary>
public class BacktestSnapshotRecord
{
    public long Id { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string CreatedBy { get; set; } = "";
    public int Seconds { get; set; }
    public string Json { get; set; } = "{}";
}
