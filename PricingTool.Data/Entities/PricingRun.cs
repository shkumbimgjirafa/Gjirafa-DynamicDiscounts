namespace PricingTool.Data.Entities;

public enum RunStatus
{
    Running = 0,
    Succeeded = 1,
    SucceededWithErrors = 2,
    Failed = 3,
}

/// <summary>Wrapper record for one pricing run (architecture rule 5) — failures and partial runs stay visible.</summary>
public class PricingRun
{
    public long Id { get; set; }

    /// <summary>The layer this run priced.</summary>
    public int LayerId { get; set; }

    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public RunStatus Status { get; set; }
    public string TriggeredBy { get; set; } = "scheduler";
    public bool IsOnDemand { get; set; }

    /// <summary>SKUs in the pulled dataset.</summary>
    public int SkuCount { get; set; }

    /// <summary>Proposals written (including unchanged and skipped rows).</summary>
    public int ProposalCount { get; set; }

    /// <summary>SKUs excluded by policy (missing cost / missing price / no band).</summary>
    public int SkippedCount { get; set; }

    public int ErrorCount { get; set; }
    public string? ErrorMessage { get; set; }

    public List<ProposedPrice> Proposals { get; set; } = new();
}
