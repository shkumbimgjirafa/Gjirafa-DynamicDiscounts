namespace PricingTool.Data.Entities;

/// <summary>Which way a pushed price moved.</summary>
public enum ChangeDirection
{
    Up = 0,
    Down = 1,
}

/// <summary>
/// The intent behind a price change — this fixes the yardstick its success is judged by
/// (a price rise is judged on profit retention; a discount on volume; a clearance on sell-through).
/// </summary>
public enum ChangeIntent
{
    /// <summary>Price raised (or a discount trimmed) to capture more margin per unit.</summary>
    MarginCapture = 0,

    /// <summary>Price cut on a healthy item to drive extra volume.</summary>
    VolumeStimulation = 1,

    /// <summary>Price cut on dead/aging stock to clear inventory; margin is secondary.</summary>
    Clearance = 2,
}

/// <summary>Realized verdict of a price change once its post-window has matured.</summary>
public enum OutcomeVerdict
{
    /// <summary>Post-window has not elapsed yet (or pre-window data is missing) — not yet judged.</summary>
    Pending = 0,
    Win = 1,
    Neutral = 2,
    Backfire = 3,
}

/// <summary>
/// The realized impact of one pushed price change, measured by the metric that matches its
/// intent (see <see cref="ChangeIntent"/>). Anchored on the proposal's PushedUtc (D0): "pre" is
/// the snapshot on/just before D0, "post" is the first snapshot on/after D0 + WindowDays.
///
/// This is a simple per-SKU pre/post comparison — it is correlational, NOT causal (market drift
/// is not separated out). That is the accepted v1 trade-off; the reserved columns at the bottom
/// leave room for a later holdout / category-relative upgrade without a breaking migration.
/// </summary>
public class PriceChangeOutcome
{
    public long Id { get; set; }

    /// <summary>The layer this outcome belongs to.</summary>
    public int LayerId { get; set; }

    /// <summary>The pushed proposal this measures. Nullable so an outcome survives proposal cleanup.</summary>
    public long? ProposedPriceId { get; set; }
    public ProposedPrice? ProposedPrice { get; set; }

    public string Sku { get; set; } = "";

    /// <summary>The run that produced the price change.</summary>
    public long SourceRunId { get; set; }
    public int? PriceBandId { get; set; }

    /// <summary>D0 — when the new price went live (= ProposedPrice.PushedUtc).</summary>
    public DateTime AppliedUtc { get; set; }
    public ChangeDirection Direction { get; set; }
    public ChangeIntent Intent { get; set; }

    public decimal OldPrice { get; set; }
    public decimal NewPrice { get; set; }

    /// <summary>Trailing-7d window length used for the post measurement (days after D0).</summary>
    public int WindowDays { get; set; }

    // Pre run-rates (the 7 days leading up to D0) — filled as soon as the outcome row is created.
    public decimal PreUnitsPerDay { get; set; }
    public decimal? PreMarginPct { get; set; }
    public decimal? PreGrossProfitPerDay { get; set; }

    // Post run-rates (the 7 days after D0) — null until the window matures.
    public decimal? PostUnitsPerDay { get; set; }
    public decimal? PostMarginPct { get; set; }
    public decimal? PostGrossProfitPerDay { get; set; }

    public OutcomeVerdict Verdict { get; set; }
    public string? Note { get; set; }
    public DateTime? MeasuredUtc { get; set; }
    public long? MeasuredOnRunId { get; set; }

    // --- Reserved for a future causal-attribution upgrade (unused in v1 simple pre/post):
    // public decimal? PeerUnitsDeltaPct { get; set; }  // category/band-relative baseline
    // public bool IsHoldout { get; set; }              // SKU deliberately left at old price as a control
}
