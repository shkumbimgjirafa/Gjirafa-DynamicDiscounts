using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PricingTool.Core.Abstractions;
using PricingTool.Core.Domain;

namespace PricingTool.Data.Services;

/// <summary>
/// On-demand per-SKU sales history from GjirafaTranslations.dbo.SR_ProductsData — same source,
/// (PlatformId, CompanyId) scope and OrderStatusId IN (10,20,30) filter as the elasticity fit, for ONE
/// ProductCode. Powers the SKU details page charts (weekly price→profit scatter + monthly net-sales);
/// runs only when that page is opened and stores nothing.
/// </summary>
public class SqlSkuSalesHistoryReader : ISkuSalesHistoryReader
{
    private readonly string _connectionString;
    private readonly ILogger<SqlSkuSalesHistoryReader> _logger;

    public SqlSkuSalesHistoryReader(IConfiguration config, ILogger<SqlSkuSalesHistoryReader> logger)
    {
        _connectionString = config.GetConnectionString("SourceReadOnly")
            ?? throw new InvalidOperationException("Missing connection string 'SourceReadOnly'.");
        _logger = logger;
    }

    // Weekly buckets (ISO week) of volume-weighted VAT-inclusive unit price + units + realized profit — the
    // price→profit scatter. Profit is SUM(Margin) = SUM(NetoPrice − Cogs): the ACTUAL VAT-net gross profit
    // earned that week using the real landed cost recorded on each order line, not today's PPTCV.
    private const string WeeklyQuery = @"
DECLARE @from datetime = DATEADD(DAY, -@WindowDays, GETUTCDATE());
;WITH lines AS (
    SELECT DATEDIFF(WEEK, '2017-01-02', d.DateAdded) AS WeekIx, d.Qty, d.BrutoPrice, d.Margin
    FROM GjirafaTranslations.dbo.SR_ProductsData d
    WHERE d.ProductCode = @Sku
      AND d.OrderStatusId IN (10, 20, 30)
      AND d.DateAdded >= @from
      AND d.Qty > 0 AND d.BrutoPrice > 0
      AND d.PlatformId = @Plat AND d.CompanyId = @Comp
)
SELECT WeekIx, SUM(Qty) AS Units, SUM(BrutoPrice) / SUM(Qty) AS UnitPrice, SUM(ISNULL(Margin, 0)) AS RealizedProfit
FROM lines
GROUP BY WeekIx
ORDER BY WeekIx;";

    // Monthly totals of net sales (VAT-exclusive NetoPrice) + units — the historic net-sales chart.
    private const string MonthlyQuery = @"
DECLARE @from datetime = DATEADD(MONTH, -@MonthsBack, GETUTCDATE());
SELECT YEAR(d.DateAdded) AS Yr, MONTH(d.DateAdded) AS Mo,
       SUM(d.NetoPrice) AS NetSales, SUM(d.Qty) AS Units
FROM GjirafaTranslations.dbo.SR_ProductsData d
WHERE d.ProductCode = @Sku
  AND d.OrderStatusId IN (10, 20, 30)
  AND d.DateAdded >= @from
  AND d.PlatformId = @Plat AND d.CompanyId = @Comp
GROUP BY YEAR(d.DateAdded), MONTH(d.DateAdded)
ORDER BY Yr, Mo;";

    public async Task<IReadOnlyList<SkuPriceBucket>> GetWeeklyBucketsAsync(
        int srPlatformId, int srCompanyId, string productCode, int windowDays, CancellationToken ct = default)
    {
        var buckets = new List<SkuPriceBucket>();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 60;
        cmd.CommandText = WeeklyQuery;
        cmd.Parameters.AddWithValue("@WindowDays", windowDays);
        cmd.Parameters.AddWithValue("@Sku", productCode.Trim());
        cmd.Parameters.AddWithValue("@Plat", srPlatformId);
        cmd.Parameters.AddWithValue("@Comp", srCompanyId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            buckets.Add(new SkuPriceBucket(
                WeekIndex: Convert.ToInt32(reader.GetValue(0)),
                UnitPrice: Convert.ToDecimal(reader.GetValue(2)),
                Units: Convert.ToInt32(reader.GetValue(1)),
                RealizedProfit: Convert.ToDecimal(reader.GetValue(3))));
        }

        _logger.LogInformation(
            "Weekly price buckets for SKU {Sku}: {Count} (platform {Plat}, company {Comp}, {Days}d).",
            productCode, buckets.Count, srPlatformId, srCompanyId, windowDays);
        return buckets;
    }

    public async Task<IReadOnlyList<SkuMonthlyNetSales>> GetMonthlyNetSalesAsync(
        int srPlatformId, int srCompanyId, string productCode, int monthsBack, CancellationToken ct = default)
    {
        var rows = new List<SkuMonthlyNetSales>();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 60;
        cmd.CommandText = MonthlyQuery;
        cmd.Parameters.AddWithValue("@MonthsBack", monthsBack);
        cmd.Parameters.AddWithValue("@Sku", productCode.Trim());
        cmd.Parameters.AddWithValue("@Plat", srPlatformId);
        cmd.Parameters.AddWithValue("@Comp", srCompanyId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new SkuMonthlyNetSales(
                Year: Convert.ToInt32(reader.GetValue(0)),
                Month: Convert.ToInt32(reader.GetValue(1)),
                NetSales: Convert.ToDecimal(reader.GetValue(2)),
                Units: Convert.ToInt32(reader.GetValue(3))));
        }

        _logger.LogInformation(
            "Monthly net sales for SKU {Sku}: {Count} months (platform {Plat}, company {Comp}, {Months}m).",
            productCode, rows.Count, srPlatformId, srCompanyId, monthsBack);
        return rows;
    }
}

/// <summary>Demo-mode stub: no live transaction history, so no points/sales to plot.</summary>
public class DemoSkuSalesHistoryReader : ISkuSalesHistoryReader
{
    public Task<IReadOnlyList<SkuPriceBucket>> GetWeeklyBucketsAsync(
        int srPlatformId, int srCompanyId, string productCode, int windowDays, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SkuPriceBucket>>(Array.Empty<SkuPriceBucket>());

    public Task<IReadOnlyList<SkuMonthlyNetSales>> GetMonthlyNetSalesAsync(
        int srPlatformId, int srCompanyId, string productCode, int monthsBack, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SkuMonthlyNetSales>>(Array.Empty<SkuMonthlyNetSales>());
}
