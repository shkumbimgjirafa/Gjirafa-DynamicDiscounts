namespace PricingTool.Core.Domain;

/// <summary>
/// Why a dead-stock spell ended — logged on the spell the first run-day its SKU is no longer dead, so the
/// clearance analyses read a recorded reason instead of re-deriving one from old snapshots. Checked in
/// declaration order; the first that applies wins. Byte-backed so EF maps it to tinyint.
/// </summary>
public enum DeadStockExitReason : byte
{
    /// <summary>Still dead — the spell is open.</summary>
    None = 0,

    /// <summary>Dropped out of the layer's catalogue pull (unpublished, deleted or excluded).</summary>
    Delisted = 1,

    /// <summary>Local stock reached zero — sold through, or otherwise gone from the warehouse.</summary>
    Cleared = 2,

    /// <summary>Sold again since it was last seen dead, with stock left.</summary>
    Revived = 3,

    /// <summary>No sale, but local stock rose: new units arrived and reset the freshness gate.</summary>
    Restocked = 4,

    /// <summary>Still listed and in stock with no sale and no new units, but no longer flagged (for example
    /// skipped for a missing cost, or now held as a new product).</summary>
    Other = 5,
}
