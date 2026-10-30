namespace PricingTool.Data.Entities;

/// <summary>
/// One fitted price-elasticity coefficient per (layer, SKU), refreshed by the periodic fit job (every ElasticityFitCadenceDays, default 15).
/// <see cref="IsUsable"/> encodes the quality + plausibility gate; the pricing run loads only usable
/// rows, and Algorithm 5 acts only on the ELASTIC ones (Coefficient &lt; −1). Non-usable rows are
/// kept for analysis/diagnostics.
/// </summary>
public class SkuElasticity
{
    public long Id { get; set; }

    /// <summary>The layer (Brand + Country) this coefficient was fitted for.</summary>
    public int LayerId { get; set; }

    public string Sku { get; set; } = "";

    /// <summary>The OLS slope of ln(units) on ln(price) — the constant price elasticity of demand.</summary>
    public decimal Coefficient { get; set; }

    /// <summary>Standard error of the slope. Algorithm 5 acts only when the coefficient is confidently
    /// below −1 (Coefficient + z·StandardError ≤ −1), so noisy near-unit-elastic fits are excluded.</summary>
    public decimal StandardError { get; set; }

    public decimal Intercept { get; set; }
    public decimal R2 { get; set; }
    public int ObservationCount { get; set; }       // weekly buckets used
    public int DistinctPricePoints { get; set; }
    public decimal PriceCv { get; set; }            // coefficient of variation of the weekly prices
    public bool IsUsable { get; set; }
    public DateTime FittedAtUtc { get; set; }
}
