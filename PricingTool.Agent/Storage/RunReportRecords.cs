namespace PricingTool.Agent.Storage;

/// <summary>
/// One published run's report (<c>ai.RunReport</c>): what the run changed, what reached the shelf, what it is expected to earn,
/// and Alpha's account of it. Written by code right after a publish; it keeps the run's numbers after retention deletes its
/// proposals, so a run can be compared with the ones before it.
/// </summary>
public class RunReportRecord
{
    public long Id { get; set; }
    public int LayerId { get; set; }
    public long RunId { get; set; }
    /// <summary>The regular publish batch the report is about (one report per batch).</summary>
    public long BatchId { get; set; }
    public long? RunWeekId { get; set; }
    /// <summary>The publish before it, of another run: the comparison.</summary>
    public long? PreviousBatchId { get; set; }
    public DateTime PublishedUtc { get; set; }
    public DateTime CreatedUtc { get; set; }

    /// <summary>The run's priced SKUs (not skipped), those whose price changed, raised and lowered, and those changed in a
    /// holdout category (never published).</summary>
    public int PricedSkus { get; set; }
    public int Changed { get; set; }
    public int Raised { get; set; }
    public int Lowered { get; set; }
    public int HoldoutChanged { get; set; }
    /// <summary>Prices the run checker held back (its warns).</summary>
    public int HeldByCheck { get; set; }

    /// <summary>The prices the publish was given, those a shopper now pays at our price (written or already there), and
    /// the SKUs it left out, by reason (JSON: kind → SKUs).</summary>
    public int Requested { get; set; }
    public int OnShelf { get; set; }
    public int LeftOut { get; set; }
    public string? LeftOutByKind { get; set; }

    /// <summary>Estimated: each written price change × the SKU's last 30 days' units, VAT excluded, no volume response.</summary>
    public decimal? ExpectedGp30Eur { get; set; }
    public int ExpectedSkus { get; set; }

    /// <summary>Alpha's account of the run: the pass it rests on, a headline, the report and what to watch (JSON list).
    /// <see cref="AlphaStatus"/>: written, failed (why), skipped (why), or not asked.</summary>
    public long? AlphaPassId { get; set; }
    public string? Headline { get; set; }
    public string? AlphaText { get; set; }
    public string? AlphaWatch { get; set; }
    public string AlphaStatus { get; set; } = "";
    public long? AiCallLogId { get; set; }
}

/// <summary>One category × band cell of a published run (<c>ai.RunReportCell</c>): the floor and target it used (Alpha's
/// for the category when it set one, else the band's), and the SKUs it priced and changed.</summary>
public class RunReportCellRecord
{
    public long Id { get; set; }
    public long ReportId { get; set; }
    public string Category { get; set; } = "";
    public int BandId { get; set; }
    public decimal? FloorPct { get; set; }
    public decimal? TargetPct { get; set; }
    public bool ByAlpha { get; set; }
    public bool Holdout { get; set; }
    public int Skus { get; set; }
    public int Changed { get; set; }
    public int Up { get; set; }
    public int Down { get; set; }
}
