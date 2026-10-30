namespace PricingTool.Data.Entities;

/// <summary>
/// A FROZEN, point-in-time record of one dead-stock clearance window — sealed when the NEXT clearance
/// batch is approved (see <see cref="Services.DeadStockWindowService"/>). The live dead-stock dashboard
/// only ever shows the current (newest) <see cref="ApprovedBatch"/>, whose "sold since push" figures keep
/// growing; this table preserves each PRIOR batch's final result so the team can track the trend across
/// windows and judge whether the dead-stock backlog is actually being worked down.
///
/// One row per closed window (per <see cref="ApprovedBatch"/>, per layer). Stores the raw aggregate
/// components — not derived percentages — mirroring <see cref="DailyLayerKpi"/>, so a VAT-rate or formula
/// change needs no rebuild. The open figures reproduce the dashboard's "Approved batch" card, the close
/// figures its "Sold since push" card, both read off the batch's <see cref="ApprovedBatchItem"/>s.
/// </summary>
public class DeadStockWindowLog
{
    public long Id { get; set; }

    /// <summary>The layer (Brand + Country) this window belongs to.</summary>
    public int LayerId { get; set; }

    /// <summary>The approved batch whose window this row seals. Unique — a batch is sealed at most once.</summary>
    public long ApprovedBatchId { get; set; }

    /// <summary>The sealed batch's human label (denormalised for display), e.g.
    /// "Dead-stock clearance — pushed 2026-07-17".</summary>
    public string BatchLabel { get; set; } = "";

    /// <summary>When the window opened = the sealed batch's <see cref="ApprovedBatch.PushedUtc"/>.</summary>
    public DateTime WindowOpenedUtc { get; set; }

    /// <summary>When the window logically closed = the SUPERSEDING (next) batch's PushedUtc — the moment the
    /// next clearance batch was approved. See <see cref="CreatedUtc"/> for when the figures were actually read.</summary>
    public DateTime WindowClosedUtc { get; set; }

    /// <summary>Window length in calendar days (opened → closed).</summary>
    public int WindowDays { get; set; }

    /// <summary>The newer batch whose approval closed this window. Null if the window was sealed by a
    /// fallback (e.g. age-out) rather than a successor.</summary>
    public long? SupersededByBatchId { get; set; }

    // ---- Open state: the batch as pushed (immutable batch-at-push figures) — the "Approved batch" card. ----

    /// <summary>SKUs approved as dead stock in this batch (row count).</summary>
    public int OpenSkuCount { get; set; }

    /// <summary>Σ local-warehouse stock at push across the batch — the units we set out to clear.</summary>
    public long OpenStockUnits { get; set; }

    /// <summary>Σ (Pptcv · StockAtPush) — capital (all-in / VAT-inclusive cost) tied up at push.</summary>
    public decimal OpenStockCost { get; set; }

    // ---- Close state: sold-since-push as of the seal — the "Sold since push" card. ----

    /// <summary>SKUs that moved at least one unit since push (SoldUnits &gt; 0).</summary>
    public int MovedSkuCount { get; set; }

    /// <summary>Σ units sold since push (valid orders, both platforms of the country).</summary>
    public long SoldUnits { get; set; }

    /// <summary>Σ net value sold since push (VAT-exclusive).</summary>
    public decimal SoldNet { get; set; }

    /// <summary>SKUs fully CLEARED — had local stock at push, now zero (StockAtPush &gt; 0 AND CurrentStock == 0).</summary>
    public int ClearedSkuCount { get; set; }

    /// <summary>Σ StockAtPush over the cleared SKUs — units freed from the warehouse.</summary>
    public long ClearedStockUnits { get; set; }

    /// <summary>Σ (Pptcv · StockAtPush) over the cleared SKUs — capital freed at cost (VAT-inclusive).</summary>
    public decimal ClearedStockCost { get; set; }

    /// <summary>When this row was written — the figures above are as-of this instant.</summary>
    public DateTime CreatedUtc { get; set; }

    public string? Notes { get; set; }

    /// <summary>Per-SKU detail for the SKUs that moved or cleared during the window.</summary>
    public List<DeadStockWindowClearedItem> Items { get; set; } = new();
}
