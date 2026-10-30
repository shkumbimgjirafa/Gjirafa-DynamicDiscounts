namespace PricingTool.Core.Domain;

/// <summary>Per-algorithm toggle and weight inside one band. Weight is 0..100.</summary>
public record BandAlgorithmConfig(bool Enabled, int Weight);

/// <summary>
/// Runtime view of a price band: boundaries, guardrails, rounding and algorithm settings.
/// A SKU belongs to the band whose [MinPrice, MaxPrice) range contains its PPTCV (cost).
/// </summary>
public class PriceBandConfig
{
    public int BandId { get; init; }
    public string Name { get; init; } = "";
    public decimal MinPrice { get; init; }
    public decimal MaxPrice { get; init; }

    /// <summary>Hard minimum margin, in percent of the VAT-exclusive selling price (e.g. 12 = 12%).</summary>
    public decimal MarginFloorPct { get; init; }

    /// <summary>
    /// Optional blended margin GOAL for this band, in percent (e.g. 20 = 20%). Null = off. Read by the
    /// post-pass margin-target guardrail, which re-prices only the algorithm-silent SKUs in the band to
    /// pull its overall margin toward this value, bounded by the margin floor and the anchor.
    /// </summary>
    public decimal? TargetMarginPct { get; init; }

    // ---- Dead-stock progressive markdown (Algorithm 7), per band. Defaults: 10% start, +5pp per
    // 14 days, min stock age 90, floor at 100% of cost (never below cost). The 90-day age and the
    // at-cost floor follow the 2026-07 stock-age sales analysis: ~90% of stock sales happen within
    // 90 days of check-in, so younger stock still sells naturally and below-cost clearing isn't
    // justified. See DeadStockMarkdownAlgorithm / GuardrailService.DeadStockFloor.

    /// <summary>
    /// Dead-stock DEFINITION: zero sales over this many days marks a locally-held SKU as dead.
    /// Snapped to the nearest source sales window {7, 14, 30, 60, 90} (see <see cref="SkuContext.DeadStockWindowDays"/>).
    /// </summary>
    public int DeadStockNoSaleWindowDays { get; init; } = 90;

    /// <summary>
    /// Dead-stock freshness gate: the oldest on-hand unit must be at least this many days old before a
    /// no-sales SKU is treated as dead (see <see cref="SkuContext.IsFreshlyStocked"/>).
    /// </summary>
    public int DeadStockMinStockAgeDays { get; init; } = 90;

    /// <summary>
    /// Minimum opening markdown for dead stock, in percent (e.g. 10 = the first paced drop is at least
    /// 10% off the anchor). The opening drop is the deeper of this and the live discount + one step
    /// (see <c>DeadStockMarkdownPacer</c>). Band-level: it gates only the very first drop, not the pace.
    /// </summary>
    public decimal DeadStockStartDiscountPct { get; init; } = 10m;

    /// <summary>Stage-1 ("3–6 months") markdown step, in percentage points per period (default 5pp). See <see cref="StepDiscountPctForStage"/>.</summary>
    public decimal DeadStockStepDiscountPct { get; init; } = 5m;

    /// <summary>
    /// Stage-1 ("3–6 months") pacing period: zero-sale snapshot days the SHOP PRICE must sit at the
    /// paced target before the next step (e.g. 14 = once a drop is applied and two more weeks pass
    /// without a sale, deepen again — calendar-accurate at the 24h run cadence). See <see cref="PeriodDaysForStage"/>.
    /// </summary>
    public int DeadStockPeriodDays { get; init; } = 14;

    /// <summary>
    /// Stage-1 ("3–6 months") dead-stock markdown floor as a percent of cost (100 = may run below
    /// the margin floor but never below PPTCV). Stage 1 spans from <see cref="DeadStockMinStockAgeDays"/>
    /// up to <see cref="DeadStockStage2AgeDays"/>; older stock gets the deeper stage-2/3/4 floors.
    /// </summary>
    public decimal DeadStockFloorCostPct { get; init; } = 100m;

    /// <summary>Observed no-sale days (see <c>SkuContext.DeadObservedDays</c>) where dead stock enters
    /// stage 2, "6–12 months" (default 180). Measures how long the tool has SEEN the SKU not sell, not
    /// its WMS physical age.</summary>
    public int DeadStockStage2AgeDays { get; init; } = 180;

    /// <summary>Stage-2 ("6–12 months") floor as a percent of cost (default 80 — may clear below cost).</summary>
    public decimal DeadStockStage2FloorCostPct { get; init; } = 80m;

    /// <summary>Stage-2 ("6–12 months") markdown step, in percentage points per period (default 5pp).</summary>
    public decimal DeadStockStage2StepDiscountPct { get; init; } = 5m;

    /// <summary>Stage-2 ("6–12 months") pacing period, in zero-sale snapshot days at the applied price (default 14).</summary>
    public int DeadStockStage2PeriodDays { get; init; } = 14;

    /// <summary>Observed no-sale days where dead stock enters stage 3, "12–36 months" (default 360).</summary>
    public int DeadStockStage3AgeDays { get; init; } = 360;

    /// <summary>Stage-3 ("12–36 months") floor as a percent of cost (default 60).</summary>
    public decimal DeadStockStage3FloorCostPct { get; init; } = 60m;

    /// <summary>Stage-3 ("12–36 months") markdown step, in percentage points per period (default 5pp).</summary>
    public decimal DeadStockStage3StepDiscountPct { get; init; } = 5m;

    /// <summary>Stage-3 ("12–36 months") pacing period, in zero-sale snapshot days at the applied price (default 14).</summary>
    public int DeadStockStage3PeriodDays { get; init; } = 14;

    /// <summary>Observed no-sale days where dead stock enters stage 4, "36 months+" (default 1080).</summary>
    public int DeadStockStage4AgeDays { get; init; } = 1080;

    /// <summary>Stage-4 ("36 months+") floor as a percent of cost (default 40 — the deepest clearance).</summary>
    public decimal DeadStockStage4FloorCostPct { get; init; } = 40m;

    /// <summary>Stage-4 ("36 months+") markdown step, in percentage points per period (default 5pp).</summary>
    public decimal DeadStockStage4StepDiscountPct { get; init; } = 5m;

    /// <summary>Stage-4 ("36 months+") pacing period, in zero-sale snapshot days at the applied price (default 14).</summary>
    public int DeadStockStage4PeriodDays { get; init; } = 14;

    /// <summary>The markdown step (pp) for a dead SKU's current age stage — see <see cref="Domain.DeadStockStage"/>. None/Stage1 fall back to the stage-1 step.</summary>
    public decimal StepDiscountPctForStage(DeadStockStage stage) => stage switch
    {
        DeadStockStage.Stage4 => DeadStockStage4StepDiscountPct,
        DeadStockStage.Stage3 => DeadStockStage3StepDiscountPct,
        DeadStockStage.Stage2 => DeadStockStage2StepDiscountPct,
        _ => DeadStockStepDiscountPct,
    };

    /// <summary>The pacing period (zero-sale days) for a dead SKU's current age stage. None/Stage1 fall back to the stage-1 period.</summary>
    public int PeriodDaysForStage(DeadStockStage stage) => stage switch
    {
        DeadStockStage.Stage4 => DeadStockStage4PeriodDays,
        DeadStockStage.Stage3 => DeadStockStage3PeriodDays,
        DeadStockStage.Stage2 => DeadStockStage2PeriodDays,
        _ => DeadStockPeriodDays,
    };

    public RoundingConvention Rounding { get; init; } = RoundingConvention.None;

    public bool RoundingEnabled { get; init; } = true;

    /// <summary>Keyed by algorithm code. Algorithms missing from the map are treated as disabled.</summary>
    public IReadOnlyDictionary<string, BandAlgorithmConfig> Algorithms { get; init; } =
        new Dictionary<string, BandAlgorithmConfig>();

    public bool Contains(decimal pptcv) => pptcv >= MinPrice && pptcv < MaxPrice;

    public BandAlgorithmConfig GetAlgorithm(string code) =>
        Algorithms.TryGetValue(code, out var cfg) ? cfg : new BandAlgorithmConfig(false, 0);
}
