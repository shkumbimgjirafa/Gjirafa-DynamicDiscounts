namespace PricingTool.Data.Entities;

/// <summary>
/// A batch of prices a Manager approved and pushed live OUTSIDE the tool (the manual
/// export → spreadsheet → platform flow). The engine never sees that push — every
/// <see cref="ProposedPrice"/> stays <c>Pending</c> — so this table is the tool's record of
/// what was ACTUALLY pushed. It anchors the dead-stock dashboard's clearance tracking to the
/// approved SKU set, rather than to the latest run's full auto-flagged dead-stock population.
/// </summary>
public class ApprovedBatch
{
    public long Id { get; set; }

    /// <summary>The layer (Brand + Country) this batch was pushed for.</summary>
    public int LayerId { get; set; }

    /// <summary>Human label, e.g. "Dead-stock clearance — pushed 2026-07-17".</summary>
    public string Label { get; set; } = "";

    /// <summary>When the batch went live in the shop — the clearance-tracking clock starts here.</summary>
    public DateTime PushedUtc { get; set; }

    /// <summary>When this batch record was imported into the tool.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>SKUs in the batch (denormalised for quick display).</summary>
    public int SkuCount { get; set; }

    /// <summary>Last time <see cref="ApprovedBatchItem.SoldUnits"/>/<see cref="ApprovedBatchItem.SoldNet"/>
    /// were refreshed from the live source. Null until the first refresh.</summary>
    public DateTime? SalesRefreshedUtc { get; set; }

    /// <summary>The pricing run the batch was exported from, when known. Null when imported from a
    /// spreadsheet whose source run isn't in this database.</summary>
    public long? SourceRunId { get; set; }

    /// <summary>
    /// When true, the pricing engine ENFORCES this batch as a clearance lock: every member SKU that
    /// still has locally-held stock is pinned at its <see cref="ApprovedBatchItem.PushedPrice"/> (never
    /// raised, never deepened) so a single sale can't bounce the price back up before the stock clears.
    /// Set false to end the lock and let normal pricing resume. Defaults true for a newly pushed batch.
    ///
    /// <para>This flag means "a Manager deliberately ENDED this clearance" and nothing else. Do NOT clear
    /// it when pushing the next batch: supersession is not a release. A member that sold under the
    /// markdown drops out of the dead-stock population precisely because it sold, so it is absent from
    /// the next batch — deactivating the old one strands it with stock still on the shelf and hands it
    /// straight back to the margin-floor audit, which proposes raising the clearance price. The engine
    /// already retires a finished clearance on its own: per SKU it reads the newest batch that still
    /// contains it, only within the tracking window, and releases as soon as local stock hits 0.</para>
    /// </summary>
    public bool IsActive { get; set; } = true;

    public string? Notes { get; set; }

    public List<ApprovedBatchItem> Items { get; set; } = new();
}
