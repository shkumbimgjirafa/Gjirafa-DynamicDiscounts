using PricingTool.Core.Domain;

namespace PricingTool.Core.Abstractions;

/// <summary>
/// On-demand per-SKU sales history from the transaction source (GjirafaTranslations.dbo.SR_ProductsData),
/// scoped to a layer by its (PlatformId, CompanyId) pair and computed live when the SKU details page
/// opens — nothing is stored. Two views: weekly price buckets (for the price→gross-profit scatter) and
/// monthly net-sales totals (for the historic net-sales chart). The demo implementation returns nothing.
/// </summary>
public interface ISkuSalesHistoryReader
{
    /// <summary>Weekly buckets (VAT-incl unit price + units) over a trailing window — same bucketing as the elasticity fit.</summary>
    Task<IReadOnlyList<SkuPriceBucket>> GetWeeklyBucketsAsync(
        int srPlatformId, int srCompanyId, string productCode, int windowDays, CancellationToken ct = default);

    /// <summary>Monthly totals of net sales (VAT-exclusive revenue = SUM(NetoPrice)) and units over a trailing window of months.</summary>
    Task<IReadOnlyList<SkuMonthlyNetSales>> GetMonthlyNetSalesAsync(
        int srPlatformId, int srCompanyId, string productCode, int monthsBack, CancellationToken ct = default);
}
