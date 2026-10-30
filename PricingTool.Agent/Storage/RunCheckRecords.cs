namespace PricingTool.Agent.Storage;

/// <summary>One pre-publish check of an engine run (<c>ai.AgentRunCheck</c>): the verdict that gates its export
/// and publish (pass / warn / block), how it was reached, and what it cost.</summary>
public class AgentRunCheck
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public int LayerId { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public int DurationMs { get; set; }

    /// <summary>pass / warn / block, computed by code from the rows' final outcomes. A failed check is a block.</summary>
    public string Verdict { get; set; } = "";

    /// <summary>What the model answered for the run, for comparison (code's verdict is the one that counts).</summary>
    public string? ModelVerdict { get; set; }

    /// <summary>Succeeded, or Failed (no valid answer: the run is blocked).</summary>
    public string Status { get; set; } = "";
    public string? Error { get; set; }

    public int RowsChecked { get; set; }
    public int RowsFlagged { get; set; }
    public int RowsSentToModel { get; set; }
    public int Blocks { get; set; }
    public int Warns { get; set; }
    public int Fixed { get; set; }

    public string? CallTag { get; set; }
    public long? AiCallLogId { get; set; }

    /// <summary>Rules that couldn't run (e.g. no earlier run to compare costs with) and other notes.</summary>
    public string? Notes { get; set; }

    /// <summary>Checks of injected test rows: labelled, never in the time sheet.</summary>
    public bool IsTest { get; set; }
}

/// <summary>One flagged price of a checked run (<c>ai.AgentRunCheckRow</c>) with the numbers code computed and
/// what the rules, the model and finally code made of it.</summary>
public class AgentRunCheckRow
{
    public long Id { get; set; }
    public long CheckId { get; set; }
    public long ProposalId { get; set; }
    public string Sku { get; set; } = "";
    public int? BandId { get; set; }
    public string? BandName { get; set; }
    public decimal NewPrice { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal? Cost { get; set; }
    public decimal? MarginPct { get; set; }
    public decimal? FloorPct { get; set; }
    public decimal ChangePct { get; set; }
    public int? Stock { get; set; }

    /// <summary>Rule codes that fired, comma-separated (below-cost, off-charm, ...).</summary>
    public string Flags { get; set; } = "";
    public string RuleSeverity { get; set; } = "";

    /// <summary>Null when the row was not sent to the model (over the cap: it keeps its rule severity).</summary>
    public string? ModelSeverity { get; set; }
    public string? ModelReason { get; set; }
    public decimal? FixPrice { get; set; }
    public bool FixAccepted { get; set; }

    /// <summary>block / warn / fixed / info: what the export does with the row.</summary>
    public string FinalSeverity { get; set; } = "";
    public string Detail { get; set; } = "";
}
