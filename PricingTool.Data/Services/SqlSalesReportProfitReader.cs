using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PricingTool.Core.Abstractions;
using PricingTool.Core.Domain;

namespace PricingTool.Data.Services;

/// <summary>
/// Realized-profit reader over GjirafaTranslations.dbo.SR_ProductsData on the read-only source connection.
/// Profit is <c>SUM(Margin) = SUM(NetoPrice − Cogs)</c> — the ACTUAL VAT-net gross profit using the real
/// landed cost recorded on each order line (not today's PPTCV), the same column the SKU-details
/// price→profit chart reads. Layer scope is (PlatformId, CompanyId); valid sales are OrderStatusId IN
/// (10,20,30). Weeks are bucketed the same way as the elasticity fit for consistency across the tool.
///
/// The sales report's status column lags the shop by days, so both queries also LEFT JOIN the layer's
/// operational [Order] ({opDb}, substituted per layer) and require ISNULL(o.OrderStatusId, …) to be a
/// valid sale too — see <see cref="ISalesReportProfitReader"/> for the measurements behind this. LEFT,
/// not INNER: an order row that cannot be matched falls back to the report's own status rather than
/// silently dropping the sale.
/// </summary>
public class SqlSalesReportProfitReader : ISalesReportProfitReader
{
    private readonly string _connectionString;
    private readonly ILogger<SqlSalesReportProfitReader> _logger;

    public SqlSalesReportProfitReader(IConfiguration config, ILogger<SqlSalesReportProfitReader> logger)
    {
        _connectionString = config.GetConnectionString("SourceReadOnly")
            ?? throw new InvalidOperationException("Missing connection string 'SourceReadOnly'.");
        _logger = logger;
    }

    // ISO-week buckets (Monday-anchored on 2017-01-02, matching the elasticity / SKU-history bucketing) of
    // realized profit (SUM Margin), net revenue (SUM NetoPrice) and units, from @from forward, layer-wide.
    internal const string WeeklyQuery = @"
;WITH lines AS (
    SELECT DATEDIFF(WEEK, '2017-01-02', d.DateAdded) AS WeekIx,
           d.Qty, d.Margin, d.NetoPrice
    FROM GjirafaTranslations.dbo.SR_ProductsData d
    LEFT JOIN {opDb}.dbo.[Order] o ON o.Id = d.OrderId
    WHERE d.OrderStatusId IN (10, 20, 30)
      AND ISNULL(o.OrderStatusId, d.OrderStatusId) IN (10, 20, 30)
      AND d.DateAdded >= @from
      AND d.PlatformId = @Plat AND d.CompanyId = @Comp
      AND d.ProductCode IS NOT NULL
)
SELECT WeekIx,
       SUM(ISNULL(Margin, 0))    AS Profit,
       SUM(ISNULL(NetoPrice, 0)) AS Net,
       SUM(ISNULL(Qty, 0))       AS Units
FROM lines
GROUP BY WeekIx
ORDER BY WeekIx;";

    // Six conditional sums of Margin for a SKU set, bucketed 7/30/90 days before vs after @t. The SKU list
    // arrives pipe-joined (SKUs are alphanumeric, so '|' never collides) and is split into a temp table so
    // the join is index-friendly at 40k+ SKUs. The outer bound [@t-90, @t+90) keeps the scan tight.
    internal const string ProfitWindowsQuery = @"
SET NOCOUNT ON;
CREATE TABLE #skus (Sku varchar(64) COLLATE DATABASE_DEFAULT PRIMARY KEY);
INSERT INTO #skus (Sku)
SELECT DISTINCT LTRIM(RTRIM(value)) FROM STRING_SPLIT(@skus, '|') WHERE LTRIM(RTRIM(value)) <> '';

SELECT
  SUM(CASE WHEN d.DateAdded >= DATEADD(DAY,-7,@t)  AND d.DateAdded < @t THEN d.Margin ELSE 0 END) AS P7B,
  SUM(CASE WHEN d.DateAdded >= @t AND d.DateAdded < DATEADD(DAY, 7,@t)  THEN d.Margin ELSE 0 END) AS P7A,
  SUM(CASE WHEN d.DateAdded >= DATEADD(DAY,-30,@t) AND d.DateAdded < @t THEN d.Margin ELSE 0 END) AS P30B,
  SUM(CASE WHEN d.DateAdded >= @t AND d.DateAdded < DATEADD(DAY,30,@t)  THEN d.Margin ELSE 0 END) AS P30A,
  SUM(CASE WHEN d.DateAdded >= DATEADD(DAY,-90,@t) AND d.DateAdded < @t THEN d.Margin ELSE 0 END) AS P90B,
  SUM(CASE WHEN d.DateAdded >= @t AND d.DateAdded < DATEADD(DAY,90,@t)  THEN d.Margin ELSE 0 END) AS P90A
FROM GjirafaTranslations.dbo.SR_ProductsData d
JOIN #skus s ON s.Sku = d.ProductCode
LEFT JOIN {opDb}.dbo.[Order] o ON o.Id = d.OrderId
WHERE d.OrderStatusId IN (10, 20, 30)
  AND ISNULL(o.OrderStatusId, d.OrderStatusId) IN (10, 20, 30)
  AND d.PlatformId = @Plat AND d.CompanyId = @Comp
  AND d.DateAdded >= DATEADD(DAY,-90,@t) AND d.DateAdded < DATEADD(DAY,90,@t);

DROP TABLE #skus;";

    // The Monday DATEDIFF(WEEK, '2017-01-02', …) counts from; WeekStart = epoch + 7·WeekIx (same anchor).
    private static readonly DateTime WeekEpoch = new(2017, 1, 2);

    public async Task<IReadOnlyList<WeeklyProfitPoint>> GetWeeklyLayerProfitAsync(
        int srPlatformId, int srCompanyId, string operationalDatabase, DateTime sinceUtc,
        CancellationToken ct = default)
    {
        var points = new List<WeeklyProfitPoint>();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 120;
        cmd.CommandText = WeeklyQuery.Replace(
            SourceQueries.OperationalDbToken, SourceQueries.SafeDbName(operationalDatabase));
        cmd.Parameters.AddWithValue("@from", sinceUtc);
        cmd.Parameters.AddWithValue("@Plat", srPlatformId);
        cmd.Parameters.AddWithValue("@Comp", srCompanyId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var weekIx = Convert.ToInt32(reader.GetValue(0));
            points.Add(new WeeklyProfitPoint(
                WeekStart: WeekEpoch.AddDays(7 * weekIx),
                Profit: Convert.ToDecimal(reader.GetValue(1)),
                Units: Convert.ToInt64(reader.GetValue(3)),
                NetRevenue: Convert.ToDecimal(reader.GetValue(2))));
        }

        _logger.LogInformation(
            "Weekly layer profit: {Count} week(s) since {Since:u} (platform {Plat}, company {Comp}).",
            points.Count, sinceUtc, srPlatformId, srCompanyId);
        return points;
    }

    public async Task<ApprovalProfitWindows> GetApprovalProfitWindowsAsync(
        int srPlatformId, int srCompanyId, string operationalDatabase, IReadOnlyCollection<string> skus,
        DateTime approvedUtc, CancellationToken ct = default)
    {
        var zero = new ApprovalProfitWindows(0, 0, 0, 0, 0, 0);
        if (skus.Count == 0) return zero;

        var skuList = string.Join('|', skus
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim()));
        if (skuList.Length == 0) return zero;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 600;
        cmd.CommandText = ProfitWindowsQuery.Replace(
            SourceQueries.OperationalDbToken, SourceQueries.SafeDbName(operationalDatabase));
        cmd.Parameters.AddWithValue("@skus", skuList);
        cmd.Parameters.AddWithValue("@t", approvedUtc);
        cmd.Parameters.AddWithValue("@Plat", srPlatformId);
        cmd.Parameters.AddWithValue("@Comp", srCompanyId);

        decimal D(SqlDataReader r, int i) => r.IsDBNull(i) ? 0m : Convert.ToDecimal(r.GetValue(i));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = zero;
        if (await reader.ReadAsync(ct))
            result = new ApprovalProfitWindows(
                D(reader, 0), D(reader, 1), D(reader, 2), D(reader, 3), D(reader, 4), D(reader, 5));

        _logger.LogInformation(
            "Approval profit windows for {Count} SKU(s) around {T:u} (platform {Plat}, company {Comp}).",
            skus.Count, approvedUtc, srPlatformId, srCompanyId);
        return result;
    }
}

/// <summary>Demo-mode stub: no live sales report, so no profit trend or windows.</summary>
public class DemoSalesReportProfitReader : ISalesReportProfitReader
{
    public Task<IReadOnlyList<WeeklyProfitPoint>> GetWeeklyLayerProfitAsync(
        int srPlatformId, int srCompanyId, string operationalDatabase, DateTime sinceUtc,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WeeklyProfitPoint>>(Array.Empty<WeeklyProfitPoint>());

    public Task<ApprovalProfitWindows> GetApprovalProfitWindowsAsync(
        int srPlatformId, int srCompanyId, string operationalDatabase, IReadOnlyCollection<string> skus,
        DateTime approvedUtc,
        CancellationToken ct = default) =>
        Task.FromResult(new ApprovalProfitWindows(0, 0, 0, 0, 0, 0));
}
