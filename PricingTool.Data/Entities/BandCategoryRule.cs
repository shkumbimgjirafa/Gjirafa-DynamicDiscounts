namespace PricingTool.Data.Entities;

/// <summary>
/// A per-category override of a band's margin floor and/or target (hackathon build, plan section 5
/// "Per-category floors"). Null = use the band's own value. Written by the pricing agent within its
/// safety limits; the band's hand-set values in <see cref="PriceBand"/> stay untouched, so they remain
/// the baseline and the holdout's reference.
/// </summary>
public class BandCategoryRule
{
    public long Id { get; set; }
    public int LayerId { get; set; }
    public int BandId { get; set; }

    /// <summary>The layer's decision category: GjirafaMall main group, Gjirafa50 L1 category.</summary>
    public string Category { get; set; } = "";

    public decimal? MarginFloorPct { get; set; }
    public decimal? TargetMarginPct { get; set; }

    public DateTime UpdatedUtc { get; set; }

    /// <summary>Who set it: Alpha, a person, ...</summary>
    public string UpdatedBy { get; set; } = "";

    /// <summary>The agent decision (ai.AgentDecision) that set the current values, if any.</summary>
    public long? AgentDecisionId { get; set; }
}
