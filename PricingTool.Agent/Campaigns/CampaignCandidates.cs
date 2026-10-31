using Microsoft.Data.SqlClient;
using PricingTool.Agent.Tools;

namespace PricingTool.Agent.Campaigns;

/// <summary>Where the planner's SKUs come from (SQL in the app, a fixed list in tests).</summary>
public interface ICampaignCandidateSource
{
    /// <summary>Every SKU of the layer's latest engine run that sold on normal days in [<paramref name="from"/>, <paramref name="to"/>),
    /// with its rate per normal day; with <paramref name="everyInStock"/>, the run's other SKUs too (rate 0), for a person's
    /// whole-shop campaign.</summary>
    Task<IReadOnlyList<CampaignCandidate>> LoadAsync(int layerId, DateOnly from, DateOnly to, CancellationToken ct = default, bool everyInStock = false);
}

/// <summary>
/// The latest engine run's proposals (regular price = the anchor, today's price, cost, band, dead-stock stage, whether a
/// GjirafaMall price follows Gjirafa50) joined to its snapshot's stock, the product map by SKU for the category, and
/// the booked sales lines for the normal-day rate: units sold in the window outside every campaign, over the window's
/// non-campaign days (the snapshot's Qty30 includes campaign days).
/// </summary>
public sealed class SqlCampaignCandidateSource : ICampaignCandidateSource
{
    private readonly AgentDataSource _source;
    public SqlCampaignCandidateSource(AgentDataSource source) => _source = source;

    public async Task<IReadOnlyList<CampaignCandidate>> LoadAsync(int layerId, DateOnly from, DateOnly to, CancellationToken ct = default, bool everyInStock = false)
    {
        await using var conn = new SqlConnection(_source.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            DECLARE @Mall bit = (SELECT CASE WHEN Brand = 'GjirafaMall' THEN 1 ELSE 0 END FROM PricingTool.Layers WHERE Id = @L);
            DECLARE @Run bigint = (SELECT TOP 1 r.Id FROM PricingTool.PricingRuns r
                                   WHERE r.LayerId = @L AND r.Status IN (1, 2) AND EXISTS (SELECT 1 FROM PricingTool.ProposedPrices p WHERE p.PricingRunId = r.Id)
                                   ORDER BY r.Id DESC);
            DECLARE @Snap date = (SELECT CAST(StartedUtc AS date) FROM PricingTool.PricingRuns WHERE Id = @Run);
            DECLARE @NormalDays int = (
                SELECT COUNT(*) FROM (SELECT TOP (DATEDIFF(day, @From, @To)) DATEADD(day, ROW_NUMBER() OVER (ORDER BY (SELECT 1)) - 1, @From) AS D
                                      FROM sys.all_objects) d
                WHERE NOT EXISTS (SELECT 1 FROM agent.CampaignHistory c WHERE c.LayerId = @L AND d.D BETWEEN c.StartDate AND c.EndDate));
            WITH s AS (
                SELECT sl.Sku, SUM(sl.Qty) AS Units
                FROM agent.SalesLines sl
                WHERE sl.LayerId = @L AND sl.SaleDate >= @From AND sl.SaleDate < @To
                  AND NOT EXISTS (SELECT 1 FROM agent.CampaignHistory c WHERE c.LayerId = @L AND sl.SaleDate BETWEEN c.StartDate AND c.EndDate)
                GROUP BY sl.Sku)
            SELECT pp.Sku, pp.PriceBandId,
                   CASE WHEN @Mall = 1 THEN ISNULL(m.MainGroup, N'Others') ELSE ISNULL(m.CategoryL1, N'Unknown') END,
                   pp.Brand, pp.AnchorPrice, pp.CurrentPrice, pp.Pptcv, s.Units / NULLIF(@NormalDays, 0),
                   ISNULL(ds.LocalWarehouseStock, 0), ISNULL(ds.SupplierWarehouseStock, 0),
                   CAST(CASE WHEN pp.DeadStockStage <> 0 OR pp.ReasonCodes LIKE '%DEAD_STOCK%' OR pp.GuardrailFlags LIKE '%DEAD_STOCK%'
                                  OR pp.GuardrailFlags LIKE '%CLEARANCE_LOCK%' THEN 1 ELSE 0 END AS bit),
                   CAST(CASE WHEN pp.ReasonCodes LIKE '%SYNCED_TO_GJ50%' THEN 1 ELSE 0 END AS bit)
            FROM PricingTool.ProposedPrices pp
            LEFT JOIN s ON s.Sku = pp.Sku
            LEFT JOIN PricingTool.DailySnapshots ds ON ds.LayerId = @L AND ds.SnapshotDate = @Snap AND ds.Sku = pp.Sku
            OUTER APPLY hk.ProductForProposal(pp.LayerId, pp.ProductId, pp.Sku) m
            WHERE pp.PricingRunId = @Run AND (s.Sku IS NOT NULL OR @All = 1)
              AND pp.Pptcv > 0 AND pp.CurrentPrice > 0 AND pp.AnchorPrice > 0
            """, conn) { CommandTimeout = 300 };
        cmd.Parameters.AddWithValue("@All", everyInStock);
        cmd.Parameters.AddWithValue("@L", layerId);
        cmd.Parameters.Add("@From", System.Data.SqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
        cmd.Parameters.Add("@To", System.Data.SqlDbType.Date).Value = to.ToDateTime(TimeOnly.MinValue);
        var rows = new List<CampaignCandidate>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new CampaignCandidate(r.GetString(0), r.IsDBNull(1) ? null : Convert.ToInt32(r.GetValue(1)), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6), r.IsDBNull(7) ? 0 : r.GetDecimal(7), r.GetInt32(8), r.GetInt32(9), r.GetBoolean(10), r.GetBoolean(11)));
        return rows;
    }
}
