namespace PricingTool.Agent.Storage;

/// <summary>One step of one day's cycle (<c>ai.DailyCycleStep</c>). The cycle is idempotent per day: a step
/// that succeeded (or was skipped) for the date is not run again; a failed one is retried on the next start.</summary>
public class DailyCycleStep
{
    public long Id { get; set; }
    public DateOnly CycleDate { get; set; }
    public string Step { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }

    /// <summary>Running / Succeeded / Warning / Skipped / Failed / NotBuilt.</summary>
    public string Status { get; set; } = "";
    public string? Detail { get; set; }
    public string? Error { get; set; }
}

/// <summary>One export of a checked run for the team's push (<c>ai.ExportBatch</c>), and, once the team has
/// pushed it, the <c>PricingTool.Approval</c> that records it as applied and starts its measurement.</summary>
public class ExportBatch
{
    public long Id { get; set; }
    public int LayerId { get; set; }
    public long RunId { get; set; }
    public long CheckId { get; set; }
    public string FilePath { get; set; } = "";
    public int Rows { get; set; }
    public int LeftOutByCheck { get; set; }
    public int LeftOutHoldout { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? PushedUtc { get; set; }
    public string? PushedBy { get; set; }
    public long? ApprovalId { get; set; }

    /// <summary>The ledger's expected gross profit for 30 days at the time of the export (estimated: the
    /// price change on last month's units, no elasticity, VAT excluded).</summary>
    public decimal ExpectedGp30Eur { get; set; }
}

/// <summary>
/// The money ledger (<c>ai.MoneyLedgerEntry</c>), condition 4 of the 10 % bar: per applied set (and later per
/// cost-watcher fix) what was expected at the time and what was measured after 7 and 30 days from the sales
/// lines (booked COGS), against the holdout categories. Conservative: a window not yet complete, or a result
/// that can't be attributed, is null and counts as €0 in every total.
/// </summary>
public class MoneyLedgerEntry
{
    public long Id { get; set; }

    /// <summary>AppliedSet / CostWatcherFix.</summary>
    public string Source { get; set; } = "";
    public long RefId { get; set; }
    public int LayerId { get; set; }
    public DateTime AppliedUtc { get; set; }
    public int Skus { get; set; }

    public decimal? ExpectedEur { get; set; }
    public string? ExpectedBasis { get; set; }

    public decimal? Measured7Eur { get; set; }
    public decimal? Measured30Eur { get; set; }

    /// <summary>How the measurement was made and what it rests on (windows, holdout, figures).</summary>
    public string? Evidence { get; set; }
    public DateTime? MeasuredUtc { get; set; }
}
