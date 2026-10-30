namespace PricingTool.Data.Entities;

/// <summary>
/// Per-(layer, date) rollup of the dashboard's daily KPI inputs — ONE row per pricing run, written
/// from that day's pull and NEVER retention-trimmed. The Home dashboard reads this instead of a
/// GROUP BY over all of DailySnapshots, so the earliest rows preserve the true pre-tool baseline even
/// after snapshot retention deletes the underlying days (the sliding-baseline fix). Stores the raw
/// aggregate components (not a derived margin %) so a VAT-rate or formula change needs no rebuild.
///
/// Reproduces exactly the four aggregates HomeController computed by hand: Σ Net7, Σ Qty7, and — over
/// SKUs whose cost (Pptcv) is known — Σ Net7 (the costable revenue base) and Σ Pptcv·Qty7 (their cost).
/// </summary>
public class DailyLayerKpi
{
    public long Id { get; set; }

    /// <summary>The layer (Brand + Country) this rollup belongs to.</summary>
    public int LayerId { get; set; }

    /// <summary>The snapshot date this rollup summarises (one row per layer per date).</summary>
    public DateTime Date { get; set; }

    /// <summary>Σ trailing-7d net revenue (VAT-exclusive) across the layer's SKUs.</summary>
    public decimal Net7 { get; set; }

    /// <summary>Σ trailing-7d units across the layer's SKUs (bigint — full-catalog scale).</summary>
    public long Qty7 { get; set; }

    /// <summary>Σ Net7 over SKUs whose cost (Pptcv) is known — the costable revenue base for margin.</summary>
    public decimal CostedNet7 { get; set; }

    /// <summary>Σ Pptcv·Qty7 over SKUs whose cost is known — all-in (VAT-inclusive) cost of those units.</summary>
    public decimal Cost7 { get; set; }

    /// <summary>Number of snapshot rows (SKUs) rolled up for this layer/date.</summary>
    public int SkuCount { get; set; }
}
