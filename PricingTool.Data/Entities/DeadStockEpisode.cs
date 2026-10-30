using PricingTool.Core.Domain;

namespace PricingTool.Data.Entities;

/// <summary>
/// One continuous dead-stock spell of one SKU on one layer: opened the first run-day the run flags the
/// SKU dead (<see cref="ProposedPrice.DeadStockStage"/> &gt; 0), carried while it stays dead (a day with no
/// run continues the spell), closed the first run-day it is no longer dead, with the reason logged.
///
/// This is the dead-stock history. The pile on any date, what entered and left it between two dates, and
/// why each SKU left are all answered from these rows, so neither old runs nor old snapshot days have to
/// be kept for the clearance analyses. Written at the end of every successful run
/// (<c>DeadStockEpisodeService</c>); never retention-trimmed.
/// </summary>
public class DeadStockEpisode
{
    public long Id { get; set; }

    public int LayerId { get; set; }

    public string Sku { get; set; } = "";

    /// <summary>First run-day the SKU was flagged dead (the snapshot date of that run).</summary>
    public DateTime EnteredDate { get; set; }

    public DeadStockStage EntryStage { get; set; }

    /// <summary>Local warehouse units on the entry day.</summary>
    public int EntryLocalStock { get; set; }

    /// <summary>All-in unit cost (PPTCV) on the entry day.</summary>
    public decimal? EntryPptcv { get; set; }

    /// <summary>Selling price on the entry day.</summary>
    public decimal? EntryPrice { get; set; }

    /// <summary>The deepest age tier the spell reached.</summary>
    public DeadStockStage MaxStage { get; set; }

    /// <summary>Last run-day the SKU was still flagged dead.</summary>
    public DateTime LastSeenDate { get; set; }

    public DeadStockStage LastStage { get; set; }

    /// <summary>Local warehouse units on <see cref="LastSeenDate"/> — what is still sitting there.</summary>
    public int LastLocalStock { get; set; }

    public decimal? LastPptcv { get; set; }

    public decimal? LastPrice { get; set; }

    /// <summary>First run-day the SKU was no longer dead; null while the spell is open.</summary>
    public DateTime? ExitedDate { get; set; }

    public DeadStockExitReason ExitReason { get; set; }

    /// <summary>Local warehouse units on the exit day (null when delisted — no pull row).</summary>
    public int? ExitLocalStock { get; set; }

    /// <summary>Units sold in the trailing 90 days as of the exit day (null when delisted).</summary>
    public int? ExitQty90 { get; set; }
}
