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
    /// <summary>Where the conversation lives: null = the Chat tab; <see cref="ChatTopics"/> for the Campaigns tab.</summary>
    public string? Topic { get; set; }
}

/// <summary>
/// The Campaigns tab's conversations with Nova: one per campaign ("campaign:&lt;calendar id&gt;"), and one for planning a new
/// campaign ("campaign:new"), which moves to the campaign it creates. The Chat tab shows only messages without a topic.
/// </summary>
public static class ChatTopics
{
    public const string NewCampaign = "campaign:new";
    private const string CampaignPrefix = "campaign:";

    public static string Campaign(long calendarId) => CampaignPrefix + calendarId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The calendar id a topic is about, or null (the Chat tab, or a new campaign).</summary>
    public static long? CalendarIdOf(string? topic) =>
        topic is not null && topic.StartsWith(CampaignPrefix, StringComparison.Ordinal)
        && long.TryParse(topic.AsSpan(CampaignPrefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : null;
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
    /// <summary>A team discount whose name matches the value (a pattern: <c>*</c> any text, <c>&lt;sku&gt;</c> the SKU, else a name
    /// prefix) is never ended by the agents, even when it sells under the floor (the user's decision, 31 Oct).</summary>
    public const string ProtectDiscount = "protect-discount";
    /// <summary>A shop's effective margin (gross profit over net revenue) on the rule's days: before each day's publish code raises
    /// the products under the lowest margin that gets the shop there, never above a regular price (the user's request, 31 Oct).
    /// One layer per rule.</summary>
    public const string MarginTarget = "margin-target";
    public static readonly string[] All = { ExcludeBrand, ExcludeCategory, MinCampaignMargin, ProtectDiscount, MarginTarget };

    /// <summary>A rule's value as a percent ("19.5", "19.5%"), or null.</summary>
    public static decimal? Percent(string value) =>
        decimal.TryParse(value.Trim().TrimEnd('%', ' '), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
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
