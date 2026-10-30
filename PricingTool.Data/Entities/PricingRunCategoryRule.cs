namespace PricingTool.Data.Entities;

/// <summary>
/// The per-category floor and target (<see cref="BandCategoryRule"/>) a pricing run priced against, captured once
/// per run like <see cref="PricingRunBandTarget"/> (no foreign key to the run, so it outlives retention). The run
/// checker reads it to judge each price against the floor that produced it, not the band's.
/// </summary>
public class PricingRunCategoryRule
{
    public long Id { get; set; }

    /// <summary>The run (plain indexed column, deliberately no FK).</summary>
    public long PricingRunId { get; set; }

    public int LayerId { get; set; }
    public int BandId { get; set; }
    public string Category { get; set; } = "";

    /// <summary>The floor and target the run used for this band × category (the rule's value, or the band's where
    /// the rule leaves one null).</summary>
    public decimal MarginFloorPct { get; set; }
    public decimal? TargetMarginPct { get; set; }

    /// <summary>The run's SKUs that priced against this rule.</summary>
    public int Skus { get; set; }

    public DateTime CapturedUtc { get; set; }
}
