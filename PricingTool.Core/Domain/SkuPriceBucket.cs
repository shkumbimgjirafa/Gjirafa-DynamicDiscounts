namespace PricingTool.Core.Domain;

/// <summary>
/// One weekly bucket of a SKU's realized sales, for the on-demand price→gross-profit scatter on the SKU
/// details page. <see cref="UnitPrice"/> is the VAT-inclusive, volume-weighted realized price for the
/// week (SUM(BrutoPrice)/SUM(Qty)) — the same quantity the elasticity fit buckets.
/// <see cref="RealizedProfit"/> is the actual VAT-net gross profit earned that week — SUM(Margin), i.e.
/// SUM(NetoPrice − Cogs) — using the real per-order landed cost recorded on each line, NOT today's PPTCV.
/// </summary>
public readonly record struct SkuPriceBucket(int WeekIndex, decimal UnitPrice, int Units, decimal RealizedProfit);
