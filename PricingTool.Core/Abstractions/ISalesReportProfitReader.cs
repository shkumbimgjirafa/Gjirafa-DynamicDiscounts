using PricingTool.Core.Domain;

namespace PricingTool.Core.Abstractions;

/// <summary>
/// Realized-profit reads straight from GjirafaTranslations.dbo.SR_ProductsData — <c>SUM(Margin)</c>, the
/// actual VAT-net gross profit (<c>NetoPrice − Cogs</c>, using the real landed cost recorded on each order
/// line, not today's PPTCV), scoped to a layer by its (PlatformId, CompanyId) with OrderStatusId IN
/// (10,20,30). Same source and scope the elasticity fit and the SKU-details charts use.
///
/// CANCELLED ORDERS: the sales report's own OrderStatusId trails the shop by days — an order cancelled in
/// nopCommerce keeps sitting at status 30 here until the feed catches up (measured 2026-09-14 on Mall KS:
/// €10.5k of one week's margin, 21% of the bar, was already Cancelled at source but still counted, and the
/// stale value oscillated by thousands within minutes as the feed rewrote rows). So every read below ALSO
/// joins the layer's operational [Order] table and requires the authoritative status to be a valid sale.
/// The rule is one-sided — a line the report already flagged cancelled is never resurrected — so on matured
/// windows, where the feed has caught up, the join changes nothing.
///
/// It does NOT make a fresh window comparable to a matured one: ~30% of placed margin is eventually
/// cancelled and only ~81% of that has happened by day 7, so the newest week still reads ~10% high on
/// timing alone. Treat the last two weeks as provisional.
/// </summary>
public interface ISalesReportProfitReader
{
    /// <summary>
    /// Weekly (ISO-week, Monday-anchored) realized profit for the WHOLE layer from <paramref name="sinceUtc"/>
    /// forward — the general profit trend, not tied to any approved set. One point per week that had sales,
    /// ascending by week.
    /// </summary>
    Task<IReadOnlyList<WeeklyProfitPoint>> GetWeeklyLayerProfitAsync(
        int srPlatformId, int srCompanyId, string operationalDatabase, DateTime sinceUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Realized profit (SUM Margin) for the given SKUs in the 7/30/90 days BEFORE vs AFTER
    /// <paramref name="approvedUtc"/> — the per-approval "did the bet pay off?" comparison. One round-trip
    /// (six conditional sums). Empty <paramref name="skus"/> returns all-zero without querying.
    /// </summary>
    Task<ApprovalProfitWindows> GetApprovalProfitWindowsAsync(
        int srPlatformId, int srCompanyId, string operationalDatabase, IReadOnlyCollection<string> skus,
        DateTime approvedUtc, CancellationToken ct = default);
}
