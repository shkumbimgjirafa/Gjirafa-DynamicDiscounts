namespace PricingTool.Core.Domain;

/// <summary>
/// One month of a SKU's realized net sales — <see cref="NetSales"/> is VAT-exclusive revenue
/// (SUM of SR_ProductsData.NetoPrice) — for the historic monthly net-sales chart on the SKU details page.
/// </summary>
public readonly record struct SkuMonthlyNetSales(int Year, int Month, decimal NetSales, int Units);
