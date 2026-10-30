namespace PricingTool.Core.Services;

/// <summary>
/// VAT helpers + margin math.
///
/// IMPORTANT: <c>PPTCV</c> is the all-in, VAT-INCLUSIVE landed cost (purchase + transport + customs +
/// VAT) — the same VAT-inclusive space as the selling prices. So margin is computed directly against
/// the selling price: <c>(price − cost) / price</c>, exactly matching the source <c>GrossMargin</c>
/// (<c>(FinalPrice − PPTCV) / FinalPrice</c>). There is NO VAT stripping in the cost/margin path.
///
/// <see cref="NetFromGross"/>/<see cref="GrossFromNet"/> remain only for converting VAT-exclusive sales
/// REVENUE (e.g. order <c>PriceExclTax</c>) to a gross selling price — never for cost.
/// </summary>
public static class VatMath
{
    public static decimal NetFromGross(decimal grossPrice, decimal vatRatePct) =>
        grossPrice / (1m + vatRatePct / 100m);

    public static decimal GrossFromNet(decimal netPrice, decimal vatRatePct) =>
        netPrice * (1m + vatRatePct / 100m);

    /// <summary>
    /// Margin percent of the selling price: <c>(price − cost) / price</c>. Both the price and the
    /// all-in cost are VAT-inclusive. Null when cost is unknown — never assume zero cost.
    /// </summary>
    public static decimal? MarginPct(decimal price, decimal? unitCost)
    {
        if (unitCost is null) return null;
        if (price <= 0) return null;
        return (price - unitCost.Value) / price * 100m;
    }

    /// <summary>
    /// The lowest selling price that still yields at least <paramref name="marginFloorPct"/> margin on
    /// the price, given the all-in unit cost: <c>cost / (1 − floor/100)</c>.
    /// </summary>
    public static decimal MinGrossPriceForMargin(decimal unitCost, decimal marginFloorPct)
    {
        if (marginFloorPct >= 100m)
            throw new ArgumentOutOfRangeException(nameof(marginFloorPct), "Margin floor must be below 100%.");
        return unitCost / (1m - marginFloorPct / 100m);
    }
}
