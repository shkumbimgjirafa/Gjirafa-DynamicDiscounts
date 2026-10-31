namespace PricingTool.Agent.Storage;

/// <summary>
/// A goal the pricing manager set in the chat with Nova (<c>ai.Goal</c>): for one layer, a month or a week, gross profit in euros
/// (booked, VAT excluded) or a margin in percent. A newer goal for the same layer, period and kind replaces it (<see cref="RetiredUtc"/>).
/// The Run tab measures progress against it from the booked sales lines; Nova and Alpha read it with their weekly inputs.
/// </summary>
public class GoalRecord
{
    public long Id { get; set; }
    public int LayerId { get; set; }
    /// <summary><see cref="GoalKinds.Month"/> or <see cref="GoalKinds.Week"/>; the period runs <see cref="Start"/>..<see cref="End"/> inclusive.</summary>
    public string Period { get; set; } = "";
    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }
    /// <summary><see cref="GoalKinds.GrossProfit"/> (euros) or <see cref="GoalKinds.Margin"/> (percent).</summary>
    public string Kind { get; set; } = "";
    public decimal Value { get; set; }
    public string SetBy { get; set; } = "";
    /// <summary>The person's words it came from.</summary>
    public string Instruction { get; set; } = "";
    public long? MessageId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? RetiredUtc { get; set; }
    public string? RetiredBy { get; set; }
}

public static class GoalKinds
{
    public const string Month = "month", Week = "week";
    public const string GrossProfit = "gross-profit", Margin = "margin";
}
