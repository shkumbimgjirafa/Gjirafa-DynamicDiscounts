namespace PricingTool.Agent.Storage;

/// <summary>Outcome statuses, as stored in ai.AgentOutcome.</summary>
public static class OutcomeStatus
{
    /// <summary>Applied, but no pushed export priced with it yet: its prices haven't reached customers.</summary>
    public const string NotLive = "NotLive";
    /// <summary>Live; the after-window isn't complete yet.</summary>
    public const string Waiting = "Waiting";
    /// <summary>Measured; re-measured daily while late cancellations still post.</summary>
    public const string Provisional = "Provisional";
    /// <summary>Measured for the last time.</summary>
    public const string Final = "Final";
}

/// <summary>
/// The measured outcome of one applied floor/target decision (plan section 5, "OutcomeTracker"): band × category
/// gross profit per day and units per day in the N days after its prices went live against the N days before, with
/// the holdout categories as the seasonal control (expected = before × the holdout's after/before ratio). Booked
/// cost from the sales lines, EUR VAT excl., normal days only (campaign days left out of both windows).
/// </summary>
public class AgentOutcome
{
    public long Id { get; set; }
    public long DecisionId { get; set; }
    public int LayerId { get; set; }
    public int BandId { get; set; }
    public string Category { get; set; } = "";
    public string Status { get; set; } = OutcomeStatus.NotLive;

    /// <summary>When its prices reached customers: the first pushed export of a run that priced with the decision's rule.</summary>
    public DateTime? LiveFromUtc { get; set; }
    public long? LiveExportBatchId { get; set; }
    public int WindowDays { get; set; }

    public decimal? GpPerDayBefore { get; set; }
    public decimal? GpPerDayAfter { get; set; }
    public decimal? UnitsPerDayBefore { get; set; }
    public decimal? UnitsPerDayAfter { get; set; }
    public decimal? HoldoutGpRatio { get; set; }
    public decimal? HoldoutUnitsRatio { get; set; }
    public decimal? ExpectedGpPerDayAfter { get; set; }

    /// <summary>(after − expected) / expected, in percent.</summary>
    public decimal? GpVsExpectedPct { get; set; }
    public decimal? UnitsVsExpectedPct { get; set; }

    /// <summary>gain / neutral / drop / thin (not attributable) / null (not measured).</summary>
    public string? Verdict { get; set; }

    /// <summary>What code did: rolled-back, rollback-skipped (the rule changed since), rollback-waits (kill switch).</summary>
    public string? Action { get; set; }
    public DateTime? ActionUtc { get; set; }
    public string? Evidence { get; set; }
    public DateTime? MeasuredUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// A band × category the outcome tracker paused after rolling a change back (plan section 5, "Auto-pause and
/// rollback"). While it is active, every change the pricing agent proposes there is held for a person; a person
/// releases it in the exceptions inbox.
/// </summary>
public class CategoryPause
{
    public long Id { get; set; }
    public int LayerId { get; set; }
    public int BandId { get; set; }
    public string Category { get; set; } = "";
    public long OutcomeId { get; set; }
    public string Reason { get; set; } = "";
    public DateTime PausedUtc { get; set; }
    public DateTime? ReleasedUtc { get; set; }
    public string? ReleasedBy { get; set; }
}
