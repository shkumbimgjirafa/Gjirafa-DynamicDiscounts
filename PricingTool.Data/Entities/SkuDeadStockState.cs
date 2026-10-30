namespace PricingTool.Data.Entities;

/// <summary>
/// Running dead-stock aging counter — ONE row per (layer, SKU), advanced on every pricing run by the
/// CALENDAR DAYS elapsed since the last snapshot (a no-sale gap extends it; a sale resets it). Replaces
/// the old 180-day scan-and-count over DailySnapshots (<c>SnapshotService.GetDeadStockStatesAsync</c>):
/// the scan was the only consumer that required retaining 180 days of per-SKU history, so folding it into
/// a stored counter is what lets snapshot retention drop to a small window.
///
/// <see cref="ZeroSaleStreakDays"/> measures consecutive no-sale calendar days ending at
/// <see cref="LastSnapshotDate"/> — the same meaning at any run cadence (see
/// <c>SnapshotService.NextDeadStockState</c>). <see cref="PrevStreakDays"/> and
/// <see cref="PrevSnapshotDate"/> hold the value and date as of the snapshot BEFORE
/// <see cref="LastSnapshotDate"/> so a same-UTC-date re-pull recomputes the same number (re-pulls replace
/// the day; the counter must too).
/// </summary>
public class SkuDeadStockState
{
    public long Id { get; set; }

    /// <summary>The layer (Brand + Country) this counter belongs to.</summary>
    public int LayerId { get; set; }

    public string Sku { get; set; } = "";

    /// <summary>Consecutive no-sale CALENDAR days ending at <see cref="LastSnapshotDate"/>.</summary>
    public int ZeroSaleStreakDays { get; set; }

    /// <summary>The streak as of the latest date strictly before <see cref="LastSnapshotDate"/>;
    /// used to make a same-day re-pull idempotent.</summary>
    public int PrevStreakDays { get; set; }

    /// <summary>
    /// Consecutive no-sale CALENDAR days that drive the dead-stock AGE STAGE (see
    /// <c>SkuContext.DeadStockStage</c>). Transitioned identically to <see cref="ZeroSaleStreakDays"/>
    /// (a no-sale gap extends it by the days elapsed, any sale resets it), but — unlike the streak — it is
    /// NOT backfilled from history: it starts at 0 for every SKU when the column is added, so at rollout
    /// every dead SKU begins at Stage 1 and re-earns the deeper stages by observation. Because a SKU only
    /// qualifies as dead once <c>Qty90 == 0</c> (~90 consecutive no-sale days), the stage thresholds are
    /// read against this counter directly (Stage 2 at 180, Stage 3 at 360, Stage 4 at 1080 — total no-sale
    /// days). This replaced the old WMS-physical-age stage signal, which never survived across runs.
    /// </summary>
    public int DeadObservedDays { get; set; }

    /// <summary>The <see cref="DeadObservedDays"/> value as of the latest date strictly before
    /// <see cref="LastSnapshotDate"/>; makes a same-UTC-date re-pull idempotent (mirrors
    /// <see cref="PrevStreakDays"/>).</summary>
    public int PrevDeadObservedDays { get; set; }

    /// <summary>The most recent snapshot date folded into the counter.</summary>
    public DateTime LastSnapshotDate { get; set; }

    /// <summary>
    /// The snapshot date folded in immediately BEFORE <see cref="LastSnapshotDate"/>. The counters
    /// advance by the calendar days elapsed between consecutive snapshots (not +1 per run), so they stay
    /// in real days at any run cadence (daily → +1; every 15 days → +15). This date lets a same-UTC-date
    /// re-pull recompute the same elapsed delta as the run it replaces instead of double-counting.
    /// </summary>
    public DateTime PrevSnapshotDate { get; set; }

    /// <summary>
    /// Paced dead-stock markdown target as a PERCENT discount off the anchor (25.0 = -25%), or null
    /// when no paced markdown is active. Written by the pricing run (from
    /// <c>DeadStockMarkdownPacer</c>), carried through snapshot upserts, cleared when the streak
    /// resets (a sale) or the SKU stops qualifying as dead.
    /// </summary>
    public decimal? MarkdownTargetPct { get; set; }

    /// <summary>
    /// The streak value at which the live shop price was first observed at/beyond
    /// <see cref="MarkdownTargetPct"/> — the pacing period clock. Null while the proposal is still
    /// waiting to be reflected in the shop.
    /// </summary>
    public int? MarkdownCaughtUpStreak { get; set; }

    /// <summary>
    /// PROVENANCE for the dead-stock "tunnel": the below-margin-floor price this SKU carried while the
    /// dead-stock lane owned it, or null when the lane never took it below the floor. This is what lets
    /// <c>GuardrailService</c> keep holding such a price once the SKU starts SELLING again — the hold
    /// used to be inferred from "old local stock + selling + below floor", which also matches a SKU that
    /// merely fell under a floor that MOVED (a PPTCV rise, a stale anchor), silently pinning a genuine
    /// below-cost breach instead of repairing it.
    ///
    /// Deliberately NOT reset by the snapshot transition the way <see cref="MarkdownTargetPct"/> is: a
    /// sale is precisely the event the hold has to survive. The pricing run maintains it — set while the
    /// SKU is dead and its priced result sits below the margin floor, cleared once the price is back
    /// at/above the floor or the local stock is gone (a later restock starts fresh).
    /// </summary>
    public decimal? BelowFloorTunnelPrice { get; set; }
}
