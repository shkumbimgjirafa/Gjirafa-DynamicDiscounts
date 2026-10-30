using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PricingTool.Core.Abstractions;
using PricingTool.Core.Domain;
using PricingTool.Core.Services;

namespace PricingTool.Data.Services;

/// <summary>
/// Aggregates per-SKU elasticity regression inputs from GjirafaTranslations.dbo.SR_ProductsData over
/// the read-only source connection. Buckets transactions by ISO week (denoising intermittent demand),
/// computes a volume-weighted realized unit price (VAT-inclusive BrutoPrice/Qty) per bucket, and sums
/// the log terms set-based so only one tiny row per SKU comes back. Scoped to the given
/// (PlatformId, CompanyId) pairs — a layer passes every platform of its country, so a shared SKU's
/// weekly bucket pools BOTH storefronts' units at the (synced, identical) price; valid orders are
/// OrderStatusId IN (10,20,30). Pre-sync history can blend slightly different per-platform prices
/// into a bucket — the volume-weighted realized price stays a meaningful regressor, just noisier.
/// </summary>
public class SqlElasticitySourceReader : IElasticitySourceReader
{
    private readonly string _connectionString;
    private readonly ILogger<SqlElasticitySourceReader> _logger;

    public SqlElasticitySourceReader(IConfiguration config, ILogger<SqlElasticitySourceReader> logger)
    {
        _connectionString = config.GetConnectionString("SourceReadOnly")
            ?? throw new InvalidOperationException("Missing connection string 'SourceReadOnly'.");
        _logger = logger;
    }

    // {0} is the scope predicate: OR-joined (PlatformId, CompanyId) pairs, one per country platform.
    private const string QueryTemplate = @"
DECLARE @from datetime = DATEADD(DAY, -@WindowDays, GETUTCDATE());
;WITH lines AS (
    SELECT d.ProductCode,
           DATEDIFF(WEEK, '2017-01-02', d.DateAdded) AS WeekIx,
           d.Qty, d.BrutoPrice
    FROM GjirafaTranslations.dbo.SR_ProductsData d
    WHERE d.OrderStatusId IN (10, 20, 30)
      AND d.DateAdded >= @from
      AND d.Qty > 0 AND d.BrutoPrice > 0
      AND d.ProductCode IS NOT NULL
      AND ({0})
),
buckets AS (
    SELECT ProductCode, WeekIx,
           SUM(Qty)                      AS Units,
           SUM(BrutoPrice) / SUM(Qty)    AS UnitPrice
    FROM lines
    GROUP BY ProductCode, WeekIx
)
SELECT
    ProductCode,
    COUNT(*)                                                   AS N,
    COUNT(DISTINCT CAST(ROUND(UnitPrice, 2) AS decimal(18,2))) AS DistinctPrices,
    MIN(UnitPrice)                                             AS MinP,
    MAX(UnitPrice)                                             AS MaxP,
    AVG(UnitPrice)                                             AS AvgP,
    ISNULL(STDEV(UnitPrice), 0)                                AS StdP,
    SUM(LOG(UnitPrice))                                        AS Sx,
    SUM(LOG(Units))                                            AS Sy,
    SUM(LOG(UnitPrice) * LOG(UnitPrice))                       AS Sxx,
    SUM(LOG(Units) * LOG(Units))                              AS Syy,
    SUM(LOG(UnitPrice) * LOG(Units))                          AS Sxy
FROM buckets
GROUP BY ProductCode
HAVING COUNT(*) >= @MinObs;";

    public async Task<IReadOnlyList<ElasticityFitInput>> GetElasticityInputsAsync(
        IReadOnlyList<ElasticityScope> scopes, int windowDays, CancellationToken ct = default)
    {
        if (scopes.Count == 0) throw new ArgumentException("At least one (PlatformId, CompanyId) scope is required.", nameof(scopes));
        var rows = new List<ElasticityFitInput>();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 600;
        // One parameterized (PlatformId, CompanyId) equality pair per scope, OR-joined.
        var predicate = string.Join(" OR ", scopes.Select((_, i) => $"(d.PlatformId = @Plat{i} AND d.CompanyId = @Comp{i})"));
        cmd.CommandText = string.Format(QueryTemplate, predicate);
        cmd.Parameters.AddWithValue("@WindowDays", windowDays);
        for (var i = 0; i < scopes.Count; i++)
        {
            cmd.Parameters.AddWithValue($"@Plat{i}", scopes[i].PlatformId);
            cmd.Parameters.AddWithValue($"@Comp{i}", scopes[i].CompanyId);
        }
        cmd.Parameters.AddWithValue("@MinObs", ElasticityGate.MinObservations);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        double D(int i) => Convert.ToDouble(reader.GetValue(i));
        decimal M(int i) => Convert.ToDecimal(reader.GetValue(i));

        while (await reader.ReadAsync(ct))
        {
            rows.Add(new ElasticityFitInput(
                Sku: reader.GetString(0).Trim(),
                Observations: Convert.ToInt32(reader.GetValue(1)),
                DistinctPricePoints: Convert.ToInt32(reader.GetValue(2)),
                MinPrice: M(3), MaxPrice: M(4),
                AvgPrice: D(5), StdPrice: D(6),
                SumLnPrice: D(7), SumLnUnits: D(8),
                SumLnPriceSq: D(9), SumLnUnitsSq: D(10), SumLnPriceLnUnits: D(11)));
        }

        _logger.LogInformation("Pulled {Count} SKU elasticity inputs (scopes {Scopes}, {Days}d).",
            rows.Count, string.Join("+", scopes.Select(s => $"{s.PlatformId}/{s.CompanyId}")), windowDays);
        return rows;
    }
}

/// <summary>Demo-mode stub: no live transaction history, so no coefficients are fitted.</summary>
public class DemoElasticitySourceReader : IElasticitySourceReader
{
    public Task<IReadOnlyList<ElasticityFitInput>> GetElasticityInputsAsync(
        IReadOnlyList<ElasticityScope> scopes, int windowDays, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ElasticityFitInput>>(Array.Empty<ElasticityFitInput>());
}
