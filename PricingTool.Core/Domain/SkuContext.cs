using PricingTool.Core.Options;
using PricingTool.Core.Services;

namespace PricingTool.Core.Domain;

/// <summary>
/// Everything the algorithms can see for one SKU on one run:
/// today's snapshot row, derived metrics, band config and engine options.
///
/// Unit conventions (see README):
///  - AnchorPrice / OldPrice / CurrentPrice / all suggested prices: the LAYER'S display currency
///    (EUR/MKD/ALL), VAT-INCLUSIVE. All money for one SKU is in that single currency — the engine
///    never mixes currencies within a layer (FinalPrice, TierPrice, PPTCV and SR history all match).
///  - Pptcv: the all-in landed unit cost (purchase + transport + customs + VAT), VAT-INCLUSIVE —
///    same space as the prices, so margin = (price - Pptcv) / price. NetN revenue is VAT-EXCLUSIVE.
///  - Discount percentages are decimal fractions (0.39 = 39%).
///  - QtyN is 0 when there were no sales; DiscN is null when there were no sales. 0 and null mean different things.
/// </summary>
public class SkuContext
{
    public required string Sku { get; init; }

    /// <summary>
    /// The pricing ANCHOR: the reference/list price every discount and the guardrail ceiling are
    /// measured from. Sourced from ProductPricing.FinalPrice (falls back to the shelf OldPrice when absent).
    /// </summary>
    public required decimal AnchorPrice { get; init; }

    /// <summary>Display-only shelf/strikethrough price (TierPrice.OldPrice). Carried for the UI; never used in pricing math.</summary>
    public decimal OldPrice { get; init; }

    public required decimal CurrentPrice { get; init; }

    /// <summary>Purchase cost (VAT-exclusive EUR). Null cost SKUs are skipped before algorithms run.</summary>
    public decimal? Pptcv { get; init; }

    /// <summary>
    /// Gross margin percent from the source pricing table (32.24 = 32.24%). Carried for reference/UI only;
    /// pricing math uses <see cref="CurrentMarginPct"/> (PPTCV vs current price), never this. May be null.
    /// </summary>
    public decimal? GrossMarginPct { get; init; }

    /// <summary>Fitted log-log price elasticity for this SKU in this layer; null when missing or not usable.</summary>
    public decimal? Elasticity { get; init; }

    /// <summary>Standard error of the fitted elasticity; null when not fitted. Algorithm 5 acts only when
    /// the coefficient is confidently below −1 (Elasticity + z·ElasticityStdError ≤ −1).</summary>
    public decimal? ElasticityStdError { get; init; }

    public int KsStock { get; init; }
    public int SupplierStock { get; init; }

    /// <summary>True when inside the platform MarkAsNew window — the engine holds the current price (no discount, no change).</summary>
    public bool IsNewProduct { get; init; }

    /// <summary>
    /// Age in days of the oldest unit currently held in our warehouse (from the WMS check-in log), or
    /// null when unknown (no check-in row). Dead-stock requires this to be at or above the band's
    /// <see cref="PriceBandConfig.DeadStockMinStockAgeDays"/> before treating a no-sales SKU
    /// as "dead" — freshly received pre-orders/restocks just arrived and haven't had a chance to sell.
    /// Unknown age is treated as old enough (a genuine fresh arrival always carries a recent check-in).
    /// </summary>
    public int? OldestUnitAgeDays { get; init; }

    public int Qty7 { get; init; }
    public int Qty14 { get; init; }
    public int Qty30 { get; init; }
    public int Qty60 { get; init; }
    public int Qty90 { get; init; }

    public decimal Net7 { get; init; }
    public decimal Net14 { get; init; }
    public decimal Net30 { get; init; }
    public decimal Net60 { get; init; }
    public decimal Net90 { get; init; }

    /// <summary>Historical weighted-average discount actually given on sold items. Null = no sales in window.</summary>
    public decimal? Disc7 { get; init; }
    public decimal? Disc14 { get; init; }
    public decimal? Disc30 { get; init; }
    public decimal? Disc60 { get; init; }
    public decimal? Disc90 { get; init; }

    /// <summary>Launch date is not available in the v1 dataset; algorithms must tolerate null.</summary>
    public DateTime? LaunchDateUtc { get; init; }

    public DateTime SnapshotDateUtc { get; init; }

    /// <summary>
    /// Consecutive no-sale CALENDAR days (advanced by the days elapsed between snapshots, so it means the
    /// same thing at any run cadence) — the tool's own no-movement aging counter. Drives the markdown
    /// pacing clock. Maintained on <c>SkuDeadStockState.ZeroSaleStreakDays</c>.
    /// </summary>
    public int ZeroSaleStreakDays { get; init; }

    /// <summary>
    /// Consecutive no-sale CALENDAR days that drive the dead-stock age STAGE (see
    /// <see cref="DeadStockStage"/>). Transitioned like <see cref="ZeroSaleStreakDays"/> but NOT
    /// backfilled from a SKU's pre-tracking history — so an ongoing SKU re-earns deeper stages by
    /// continued observation; a SKU FIRST SEEN already stale (no 90-day sales + old WMS stock) seeds its
    /// counter from that stock age instead of restarting at Stage 1. Maintained on <c>SkuDeadStockState.DeadObservedDays</c>.
    /// Replaced <see cref="OldestUnitAgeDays"/> as the staging signal (WMS age now only gates freshness).
    /// </summary>
    public int DeadObservedDays { get; init; }

    /// <summary>
    /// Persisted dead-stock markdown pacing target from previous runs, as a discount FRACTION of the
    /// anchor (0.25 = -25%), or null when the SKU has no active paced markdown. Maintained by the
    /// orchestrator on <c>SkuDeadStockState</c>; advanced by <see cref="Algorithms.DeadStockMarkdownPacer"/>.
    /// </summary>
    public decimal? DeadStockPacedTarget { get; init; }

    /// <summary>
    /// The zero-sale streak value at which the live price was first observed at/beyond
    /// <see cref="DeadStockPacedTarget"/> (the pacing "period clock" start), or null while the
    /// proposal is still waiting to be applied. Persisted alongside the target.
    /// </summary>
    public int? DeadStockPacedCaughtUpStreak { get; init; }

    /// <summary>
    /// The below-margin-floor price the dead-stock lane drove this SKU to while it was dead, or null when
    /// the lane never took it below the floor. Persisted on <c>SkuDeadStockState.BelowFloorTunnelPrice</c>
    /// and — unlike the pacing fields — NOT cleared by a sale, because a sale is exactly the event the
    /// guardrail's "tunnel price that started selling again is held, never raised" rule has to survive.
    /// It is the PROVENANCE that rule tests: without it, a price that merely fell under a floor that MOVED
    /// (a PPTCV rise, a stale anchor) looks identical to a deliberate clearance markdown.
    /// </summary>
    public decimal? BelowFloorTunnelPrice { get; init; }

    public required PriceBandConfig Band { get; init; }
    public required PricingEngineOptions Options { get; init; }

    /// <summary>This layer's VAT rate percent (18 for KS/MK, 20 for AL). Reconciles VAT-incl prices with VAT-excl cost.</summary>
    public decimal VatRatePct { get; init; } = 18m;

    /// <summary>Per-SKU override that disables psychological rounding even when the band enables it.</summary>
    public bool RoundingDisabledForSku { get; init; }

    /// <summary>
    /// When true (set from the layer), the algorithms are switched off: the pricing pipeline uses the
    /// current price as its baseline and only the margin-floor clamp and rounding may change it.
    /// </summary>
    public bool FloorAndRoundingOnly { get; init; }

    /// <summary>
    /// When true, the two "slow lanes" are frozen for this run: the dead-stock pacer neither engages a
    /// fresh markdown nor steps deeper (it re-emits its already-applied target, so the price is held),
    /// and its persisted pacing state is not advanced. Set by the orchestrator on the daily runs that fall
    /// between 15-day slow-lane ticks (<see cref="PricingEngineOptions.SlowLaneCadenceDays"/>). The elastic
    /// lane is frozen separately by holding its fitted coefficient between fits, so it needs no per-run
    /// gate here. False on a slow-lane-due run and for a never-ticked layer.
    /// </summary>
    public bool SlowLanesFrozen { get; init; }

    /// <summary>
    /// When set, this SKU belongs to an ACTIVE approved clearance batch and still has locally-held stock
    /// to clear, so the guardrail PINS its price at this approved pushed price — never raised back toward
    /// the anchor, and never deepened past it by the dead-stock lane — until the local stock clears
    /// (release) or a Manager ends the batch. Null when the SKU is not under an active clearance lock.
    /// The pin is still bounded by the margin floor below and the no-raise ceiling above, so it can
    /// never breach the floor or exceed the anchor (see <c>GuardrailService</c>).
    /// </summary>
    public decimal? ClearanceLockPrice { get; init; }

    /// <summary>
    /// The layer's discount-optimization table when the layer has the feature switched ON (Schedule page),
    /// else null. A price whose displayed discount off <see cref="OldPrice"/> falls in one of these ranges has
    /// that discount trimmed by the range's share (see <see cref="DiscountOptimizer"/>) — raised, bounded by
    /// the no-raise ceiling, never applied to dead stock or a pinned/held price.
    /// </summary>
    public IReadOnlyList<DiscountTrimRange>? DiscountTrimRanges { get; init; }
    // ---- Derived metrics -------------------------------------------------

    public int TotalStock => KsStock + SupplierStock;

    /// <summary>
    /// True when we KNOW the oldest on-hand unit is younger than the dead-stock minimum age — freshly
    /// received stock (e.g. a pre-order/restock) that hasn't had a fair chance to sell yet. Unknown age
    /// (no WMS check-in row) is treated as NOT fresh, so coverage gaps fall back to prior behaviour.
    /// </summary>
    public bool IsFreshlyStocked =>
        OldestUnitAgeDays is int age && age < Band.DeadStockMinStockAgeDays;

    /// <summary>
    /// The dead-stock no-sale window (<see cref="Band"/>.DeadStockNoSaleWindowDays) snapped to the
    /// nearest source sales window {7, 14, 30, 60, 90} — the source only ships these discrete windows,
    /// so an off-grid configured value resolves to the closest one actually backed by data.
    /// </summary>
    public int DeadStockWindowDays => Band.DeadStockNoSaleWindowDays switch
    {
        <= 10 => 7,    // midpoint 7↔14
        <= 22 => 14,   // midpoint 14↔30
        <= 45 => 30,   // midpoint 30↔60
        <= 75 => 60,   // midpoint 60↔90
        _ => 90,
    };

    /// <summary>Units sold within the (snapped) dead-stock no-sale window. Zero ⇒ no movement in the window.</summary>
    public int QtyInDeadStockWindow => DeadStockWindowDays switch
    {
        7 => Qty7,
        14 => Qty14,
        30 => Qty30,
        60 => Qty60,
        _ => Qty90,
    };

    /// <summary>
    /// THE dead-stock definition (the gate <c>DeadStockMarkdownAlgorithm</c> acts on): zero sales
    /// across the band's entire no-sale window, positive locally-held stock (supplier-only stock is
    /// not ours to clear), not freshly stocked (a just-arrived pre-order/restock hasn't had a chance
    /// to sell), and not platform-new (the new-product hard rule holds those at the current price, so
    /// the engine can never treat them as dead — mirroring <c>GuardrailService.IsLocalDeadStock</c>).
    /// </summary>
    public bool IsDeadStock =>
        QtyInDeadStockWindow == 0 && KsStock > 0 && !IsFreshlyStocked && !IsNewProduct;

    /// <summary>
    /// The age tier of a dead-stock SKU, driving how deep the guardrail's cost floor goes
    /// (see <see cref="Domain.DeadStockStage"/> for the labels and defaults). Driven by the tool's own
    /// snapshot-tracked <see cref="DeadObservedDays"/> (consecutive no-sale days), NOT the WMS physical
    /// age: a SKU only qualifies as dead once <c>Qty90 == 0</c> (~90 no-sale days), so at the default
    /// thresholds Stage 1 spans up to 180 total no-sale days, Stage 2 to 360, Stage 3 to 1080, then
    /// Stage 4. The counter is not backfilled from pre-tracking history, so an ongoing SKU re-earns the
    /// deeper (cheaper) floors by continued observation (a SKU first seen already stale seeds from its WMS
    /// stock age instead of restarting at Stage 1). Boundaries are inclusive at the
    /// lower edge. The orchestrator stamps this onto each proposal so stages stay queryable per run.
    /// (The <see cref="PriceBandConfig.DeadStockStage2AgeDays"/>/3/4 thresholds keep their historical
    /// names but now measure observed no-sale days, not WMS age.)
    /// </summary>
    public DeadStockStage DeadStockStage =>
        !IsDeadStock ? DeadStockStage.None
        : DeadObservedDays >= Band.DeadStockStage4AgeDays ? DeadStockStage.Stage4
        : DeadObservedDays >= Band.DeadStockStage3AgeDays ? DeadStockStage.Stage3
        : DeadObservedDays >= Band.DeadStockStage2AgeDays ? DeadStockStage.Stage2
        : DeadStockStage.Stage1;

    /// <summary>Today's discount as a fraction of the anchor price, clamped to [0, 1).</summary>
    public decimal CurrentDiscountFraction =>
        AnchorPrice <= 0 ? 0 : Math.Clamp((AnchorPrice - CurrentPrice) / AnchorPrice, 0m, 0.99m);

    public decimal Velocity7 => Qty7 / 7m;
    public decimal Velocity14 => Qty14 / 14m;
    public decimal Velocity30 => Qty30 / 30m;
    public decimal Velocity60 => Qty60 / 60m;
    public decimal Velocity90 => Qty90 / 90m;

    /// <summary>Daily velocity with recent windows weighted higher (7d 50%, 14d 30%, 30d 20%).</summary>
    public decimal WeightedDailyVelocity =>
        0.5m * Velocity7 + 0.3m * Velocity14 + 0.2m * Velocity30;

    /// <summary>Projected days until total stock sells out at the weighted velocity. Null when velocity is zero.</summary>
    public decimal? DaysToSellout =>
        WeightedDailyVelocity > 0 ? TotalStock / WeightedDailyVelocity : null;

    /// <summary>
    /// Projected days until our LOCALLY-HELD (KS) stock sells out at the weighted velocity. Null when
    /// velocity is zero. Sell-through / markdown logic uses THIS, not total stock — supplier stock is
    /// not ours to clear and is volatile (a supplier can add thousands of units overnight), so it must
    /// not inflate "days of stock" and trigger markdowns.
    /// </summary>
    public decimal? DaysToSelloutLocal =>
        WeightedDailyVelocity > 0 ? KsStock / WeightedDailyVelocity : null;

    /// <summary>
    /// Margin percent at the current selling price computed from PPTCV (VAT reconciled). Null without cost.
    /// This is THE margin signal every advisor uses — measured on the same PPTCV/current-price basis as the
    /// margin-floor guardrail, so "within Npp of the floor" comparisons are apples-to-apples.
    /// </summary>
    public decimal? CurrentMarginPct => VatMath.MarginPct(CurrentPrice, Pptcv);

    /// <summary>Price that applies the given discount fraction to the anchor price.</summary>
    public decimal PriceAtDiscount(decimal discountFraction) =>
        AnchorPrice * (1 - Math.Clamp(discountFraction, 0m, 0.99m));
}
