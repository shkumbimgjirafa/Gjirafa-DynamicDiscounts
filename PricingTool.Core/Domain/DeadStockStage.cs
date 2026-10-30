namespace PricingTool.Core.Domain;

/// <summary>
/// How long the tool has OBSERVED a dead-stock SKU go without a sale (snapshot-tracked
/// <c>SkuContext.DeadObservedDays</c>, NOT WMS physical age), bucketed into the tiers that drive the
/// guardrail's cost floor (see <c>GuardrailService.DeadStockFloor</c>) and each tier's own markdown
/// step/period. The thresholds, floors, steps and periods are all per-band (<see cref="PriceBandConfig"/>);
/// a SKU only qualifies as dead once it has ~90 no-sale days (Qty90 == 0), so at the defaults:
///  - <see cref="Stage1"/> "3–6 months"   (up to 180 no-sale days) — floor 100% of cost,
///  - <see cref="Stage2"/> "6–12 months"  (180–360 no-sale days)   — floor 80% of cost,
///  - <see cref="Stage3"/> "12–36 months" (360–1080 no-sale days)  — floor 60% of cost,
///  - <see cref="Stage4"/> "36 months+"   (1080+ no-sale days)     — floor 40% of cost.
/// The counter is counted forward, not backfilled from a SKU's pre-tracking no-sale history, so an
/// ongoing SKU re-earns the deeper floors by observation. The one exception is a SKU FIRST OBSERVED
/// already stale (no 90-day sales and genuinely old WMS on-hand stock): it seeds from that stock age
/// instead of restarting at Stage 1, so late-onboarded or re-listed old inventory is staged correctly
/// (see SnapshotService.NextDeadStockState). Byte-backed so EF maps it to tinyint and SqlBulkCopy writes it as a byte.
/// </summary>
public enum DeadStockStage : byte
{
    /// <summary>Not dead stock (or not evaluated — rows persisted before stages existed).</summary>
    None = 0,

    /// <summary>"3–6 months" — dead, below the stage-2 no-sale-days threshold. Where every SKU starts (observed-dead 0).</summary>
    Stage1 = 1,

    /// <summary>"6–12 months" — observed no-sale days at or past the band's stage-2 threshold (default 180).</summary>
    Stage2 = 2,

    /// <summary>"12–36 months" — observed no-sale days at or past the band's stage-3 threshold (default 360).</summary>
    Stage3 = 3,

    /// <summary>"36 months+" — observed no-sale days at or past the band's stage-4 threshold (default 1080).</summary>
    Stage4 = 4,
}
