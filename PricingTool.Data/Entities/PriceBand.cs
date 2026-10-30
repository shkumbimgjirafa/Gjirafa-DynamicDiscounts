namespace PricingTool.Data.Entities;

/// <summary>
/// Admin-editable price band. A product's band is determined by its PPTCV (cost)
/// (MinPrice inclusive, MaxPrice exclusive). Seeded boundaries are PLACEHOLDERS —
/// bands 2–7 must be confirmed before go-live.
/// </summary>
public class PriceBand
{
    public int Id { get; set; }

    /// <summary>The layer this band belongs to — bands are per-layer (thresholds differ by currency).</summary>
    public int LayerId { get; set; }

    public string Name { get; set; } = "";
    public decimal MinPrice { get; set; }
    public decimal MaxPrice { get; set; }

    /// <summary>Hard minimum margin in percent of the VAT-exclusive selling price.</summary>
    public decimal MarginFloorPct { get; set; }

    /// <summary>
    /// Optional blended margin GOAL for this band, in percent (e.g. 20 = 20%). When set, the
    /// post-pass margin-target guardrail nudges the band's overall margin toward this value by
    /// re-pricing only the SKUs no algorithm moved (the "free" remainder) — it never overrides an
    /// algorithm's conclusion, and stays within the margin floor and the anchor. Null = off (the
    /// guardrail never runs for this band). Distinct from <see cref="MarginFloorPct"/>: the floor is a
    /// hard per-SKU minimum; this is an aggregate target for the band.
    /// </summary>
    public decimal? TargetMarginPct { get; set; }

    // ---- Dead-stock progressive markdown (Algorithm 7), per band.
    /// <summary>
    /// Dead-stock DEFINITION for this band: a locally-held SKU with ZERO sales over this many days is
    /// "dead" and eligible for markdown. Backed by the source's fixed sales windows {7, 14, 30, 60, 90}
    /// days — an off-grid value is snapped to the nearest available window. Default 90.
    /// </summary>
    public int DeadStockNoSaleWindowDays { get; set; } = 90;

    /// <summary>
    /// Dead-stock freshness gate: the oldest on-hand unit must be at least this many days old before a
    /// no-sales SKU counts as dead. Stops freshly-received pre-orders/restocks — which have zero sales
    /// only because they just arrived — from being marked down prematurely. A SKU with no WMS check-in
    /// record (unknown age) is treated as old enough. Default 90 — the 2026-07 stock-age sales analysis
    /// showed ~90% of stock sales happen within 90 days of check-in, so younger stock still sells naturally.
    /// </summary>
    public int DeadStockMinStockAgeDays { get; set; } = 90;

    /// <summary>Minimum opening dead-stock markdown, in percent (e.g. 10 = the first paced drop is at least 10% off).</summary>
    public decimal DeadStockStartDiscountPct { get; set; } = 10m;

    /// <summary>Stage-1 ("3–6 months") markdown step, in percentage points per period (e.g. 5 = +5pp).</summary>
    public decimal DeadStockStepDiscountPct { get; set; } = 5m;

    /// <summary>Stage-1 ("3–6 months") pacing period, in no-sale snapshot days at the applied price (e.g. 14).</summary>
    public int DeadStockPeriodDays { get; set; } = 14;

    /// <summary>
    /// Stage-1 ("3–6 months") dead-stock floor as a percent of cost. Default 100: the markdown may
    /// pierce the margin floor but not price below PPTCV while the stock is 90–180 days old. Older
    /// stock falls through to the deeper stage-2/3/4 floors below.
    /// </summary>
    public decimal DeadStockFloorCostPct { get; set; } = 100m;

    /// <summary>Observed no-sale days (SkuContext.DeadObservedDays, snapshot-tracked — not WMS age)
    /// where dead stock enters stage 2, "6–12 months". Default 180.</summary>
    public int DeadStockStage2AgeDays { get; set; } = 180;

    /// <summary>Stage-2 ("6–12 months") floor as a percent of cost. Default 80 — may clear below cost.</summary>
    public decimal DeadStockStage2FloorCostPct { get; set; } = 80m;

    /// <summary>Stage-2 ("6–12 months") markdown step, in percentage points per period. Default 5pp.</summary>
    public decimal DeadStockStage2StepDiscountPct { get; set; } = 5m;

    /// <summary>Stage-2 ("6–12 months") pacing period, in no-sale snapshot days at the applied price. Default 14.</summary>
    public int DeadStockStage2PeriodDays { get; set; } = 14;

    /// <summary>Observed no-sale days where dead stock enters stage 3, "12–36 months". Default 360.</summary>
    public int DeadStockStage3AgeDays { get; set; } = 360;

    /// <summary>Stage-3 ("12–36 months") floor as a percent of cost. Default 60.</summary>
    public decimal DeadStockStage3FloorCostPct { get; set; } = 60m;

    /// <summary>Stage-3 ("12–36 months") markdown step, in percentage points per period. Default 5pp.</summary>
    public decimal DeadStockStage3StepDiscountPct { get; set; } = 5m;

    /// <summary>Stage-3 ("12–36 months") pacing period, in no-sale snapshot days at the applied price. Default 14.</summary>
    public int DeadStockStage3PeriodDays { get; set; } = 14;

    /// <summary>Observed no-sale days where dead stock enters stage 4, "36 months+". Default 1080.</summary>
    public int DeadStockStage4AgeDays { get; set; } = 1080;

    /// <summary>Stage-4 ("36 months+") floor as a percent of cost. Default 40 — the deepest clearance floor.</summary>
    public decimal DeadStockStage4FloorCostPct { get; set; } = 40m;

    /// <summary>Stage-4 ("36 months+") markdown step, in percentage points per period. Default 5pp.</summary>
    public decimal DeadStockStage4StepDiscountPct { get; set; } = 5m;

    /// <summary>Stage-4 ("36 months+") pacing period, in no-sale snapshot days at the applied price. Default 14.</summary>
    public int DeadStockStage4PeriodDays { get; set; } = 14;

    /// <summary>Stored as PricingTool.Core.Domain.RoundingConvention.</summary>
    public int RoundingConvention { get; set; }

    public bool RoundingEnabled { get; set; } = true;
    public int SortOrder { get; set; }

    public List<BandAlgorithmSetting> AlgorithmSettings { get; set; } = new();
}
