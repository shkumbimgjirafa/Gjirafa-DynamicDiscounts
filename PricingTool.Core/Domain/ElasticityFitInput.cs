namespace PricingTool.Core.Domain;

/// <summary>
/// Per-SKU regression inputs for the weekly elasticity fit, aggregated set-based in SQL from the
/// transaction history (one row per SKU). The log-sums let the OLS slope be computed without
/// pulling every weekly bucket into memory. x = ln(realized unit price), y = ln(units).
/// </summary>
public record ElasticityFitInput(
    string Sku,
    int Observations,            // number of weekly buckets
    int DistinctPricePoints,
    decimal MinPrice,
    decimal MaxPrice,
    double AvgPrice,
    double StdPrice,
    double SumLnPrice,
    double SumLnUnits,
    double SumLnPriceSq,
    double SumLnUnitsSq,
    double SumLnPriceLnUnits)
{
    public decimal PriceRangeRatio => MinPrice > 0 ? MaxPrice / MinPrice : 0m;
    public decimal PriceCv => AvgPrice > 0 ? (decimal)(StdPrice / AvgPrice) : 0m;
}
