using System.Data;
using Microsoft.Data.SqlClient;

namespace PricingTool.Agent.Tools;

/// <summary>Connection to the hackathon database (the tool DB, with the <c>agent</c> views and <c>hk</c> data).</summary>
public sealed record AgentDataSource(string ConnectionString);

/// <summary>
/// Read-only aggregates for Alpha's scorecard and the agents' data tools. Our code does the SQL and the
/// arithmetic; the agents only read the results. Sales come from the curated <c>agent.*</c> views (booked
/// COGS, exclusions applied); stock, dead stock and the engine's banding come from the latest engine run.
/// Category is the layer's decision level: GjirafaMall main group, Gjirafa50 L1.
/// </summary>
public sealed class PricingDataReader
{
    private const int CommandTimeoutSeconds = 60;
    private readonly AgentDataSource _source;

    public PricingDataReader(AgentDataSource source) => _source = source;

    public async Task<LayerInfo> GetLayerAsync(int layerId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT l.Id, l.DisplayName, l.Brand, l.Currency, l.VatRatePct
            FROM PricingTool.Layers l JOIN agent.Layers a ON a.LayerId = l.Id
            WHERE l.Id = @L
            """;
        var rows = await QueryAsync(sql, p => p.AddWithValue("@L", layerId),
            r => new LayerInfo(Id(r, 0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetDecimal(4)), ct);
        return rows.SingleOrDefault()
            ?? throw new ArgumentOutOfRangeException(nameof(layerId), layerId, "Not an agent layer (1 = GjirafaMall KS, 4 = Gjirafa50 KS).");
    }

    public Task<List<BandConfigRow>> GetBandConfigAsync(int layerId, CancellationToken ct = default) =>
        QueryAsync("""
            SELECT BandId, BandName, MinPrice, MaxPrice, MarginFloorPct, TargetMarginPct, SortOrder
            FROM agent.BandConfig WHERE LayerId = @L ORDER BY SortOrder
            """,
            p => p.AddWithValue("@L", layerId),
            r => new BandConfigRow(Id(r, 0), r.GetString(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4),
                r.IsDBNull(5) ? null : r.GetDecimal(5), r.GetInt32(6)), ct);

    /// <summary>
    /// Sales per band × category × period × campaign day. Period 0 = <paramref name="current"/>, 1 =
    /// <paramref name="comparison"/> (pass <see cref="DateWindow.Empty"/> for none). A line is below the floor
    /// when its booked margin is under today's band floor by more than <paramref name="tolerancePp"/>; at the
    /// floor when within that tolerance below and <paramref name="atFloorBandPp"/> above.
    /// </summary>
    public Task<List<SalesAggregate>> GetSalesAsync(int layerId, DateWindow current, DateWindow comparison,
        decimal tolerancePp, decimal atFloorBandPp, int? bandId = null, string? category = null, CancellationToken ct = default) =>
        QueryAsync("""
            DECLARE @Mall bit = (SELECT CASE WHEN Brand = 'GjirafaMall' THEN 1 ELSE 0 END FROM agent.Layers WHERE LayerId = @L);
            WITH s AS (
                SELECT s.BandId, CASE WHEN @Mall = 1 THEN s.MainGroup ELSE s.CategoryL1 END AS Category,
                       CASE WHEN s.SaleDate >= @From0 AND s.SaleDate < @To0 THEN 0 ELSE 1 END AS Period,
                       s.Sku, s.Qty, s.NetRevenue, s.GrossProfit, s.StockClass, s.SaleDate,
                       100.0 * s.GrossProfit / NULLIF(s.NetRevenue, 0) AS MarginPct
                FROM agent.SalesLines s
                WHERE s.LayerId = @L
                  AND ((s.SaleDate >= @From0 AND s.SaleDate < @To0) OR (s.SaleDate >= @From1 AND s.SaleDate < @To1))
                  AND (@Band IS NULL OR s.BandId = @Band)
                  AND (@Category IS NULL OR (CASE WHEN @Mall = 1 THEN s.MainGroup ELSE s.CategoryL1 END) = @Category)
            )
            SELECT s.BandId, s.Category, s.Period, CAST(CASE WHEN c.CampaignId IS NULL THEN 0 ELSE 1 END AS bit) AS CampaignDay,
                   SUM(s.Qty), ISNULL(SUM(s.NetRevenue), 0), ISNULL(SUM(s.GrossProfit), 0),
                   ISNULL(SUM(CASE WHEN s.MarginPct < b.MarginFloorPct - @Tol THEN s.NetRevenue ELSE 0 END), 0),
                   ISNULL(SUM(CASE WHEN s.MarginPct >= b.MarginFloorPct - @Tol AND s.MarginPct < b.MarginFloorPct + @AtBand THEN s.NetRevenue ELSE 0 END), 0),
                   ISNULL(SUM(CASE WHEN s.StockClass = 'CrossDock' THEN s.NetRevenue ELSE 0 END), 0),
                   COUNT(DISTINCT s.Sku),
                   COUNT(DISTINCT CASE WHEN e.Sku IS NOT NULL THEN s.Sku END),
                   ISNULL(SUM(CASE WHEN e.Sku IS NOT NULL THEN s.NetRevenue ELSE 0 END), 0)
            FROM s
            LEFT JOIN agent.BandConfig b ON b.LayerId = @L AND b.BandId = s.BandId
            OUTER APPLY (SELECT TOP 1 ch.CampaignId FROM agent.CampaignHistory ch
                         WHERE ch.LayerId = @L AND s.SaleDate BETWEEN ch.StartDate AND ch.EndDate) c
            LEFT JOIN (SELECT DISTINCT Sku FROM PricingTool.SkuElasticity WHERE LayerId = @L AND IsUsable = 1) e ON e.Sku = s.Sku
            GROUP BY s.BandId, s.Category, s.Period, CASE WHEN c.CampaignId IS NULL THEN 0 ELSE 1 END
            """,
            p =>
            {
                p.AddWithValue("@L", layerId);
                AddWindow(p, 0, current);
                AddWindow(p, 1, comparison);
                p.AddWithValue("@Tol", tolerancePp);
                p.AddWithValue("@AtBand", atFloorBandPp);
                p.AddWithValue("@Band", (object?)bandId ?? DBNull.Value);
                p.Add("@Category", SqlDbType.NVarChar, 200).Value = (object?)category ?? DBNull.Value;
            },
            r => new SalesAggregate(
                r.IsDBNull(0) ? null : Id(r, 0), r.IsDBNull(1) ? "Unknown" : r.GetString(1), r.GetInt32(2), r.GetBoolean(3),
                r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9),
                r.GetInt32(10), r.GetInt32(11), r.GetDecimal(12)), ct);

    /// <summary>The latest successful engine run for the layer, whose snapshot holds today's stock.</summary>
    public async Task<EngineRunRef?> GetLatestRunAsync(int layerId, CancellationToken ct = default)
    {
        var rows = await QueryAsync("""
            SELECT TOP 1 Id, CAST(StartedUtc AS date) FROM PricingTool.PricingRuns
            WHERE LayerId = @L AND Status = 1 AND ProposalCount > 0
              AND EXISTS (SELECT 1 FROM PricingTool.ProposedPrices pp WHERE pp.PricingRunId = PricingRuns.Id)
            ORDER BY Id DESC
            """,
            p => p.AddWithValue("@L", layerId),
            r => new EngineRunRef(r.GetInt64(0), DateOnly.FromDateTime(r.GetDateTime(1))), ct);
        return rows.SingleOrDefault();
    }

    /// <summary>Local-warehouse stock at PPTCV per engine band × category, from the run's snapshot; dead
    /// stock is what the engine itself staged as dead (<c>DeadStockStage</c> &gt; 0).</summary>
    public Task<List<StockAggregate>> GetStockAsync(int layerId, EngineRunRef run, int? bandId = null, string? category = null,
        CancellationToken ct = default) =>
        QueryAsync("""
            DECLARE @Mall bit = (SELECT CASE WHEN Brand = 'GjirafaMall' THEN 1 ELSE 0 END FROM agent.Layers WHERE LayerId = @L);
            WITH x AS (
                SELECT pp.PriceBandId AS BandId,
                       CASE WHEN @Mall = 1 THEN ISNULL(p.MainGroup, N'Others') ELSE ISNULL(p.CategoryL1, N'Unknown') END AS Category,
                       ds.LocalWarehouseStock * ds.Pptcv AS Value, pp.DeadStockStage, st.DeadObservedDays
                FROM PricingTool.DailySnapshots ds
                JOIN PricingTool.ProposedPrices pp ON pp.PricingRunId = @Run AND pp.Sku = ds.Sku
                OUTER APPLY hk.ProductForProposal(@L, pp.ProductId, pp.Sku) p
                LEFT JOIN PricingTool.SkuDeadStockStates st ON st.LayerId = @L AND st.Sku = ds.Sku
                WHERE ds.LayerId = @L AND ds.SnapshotDate = @Snap AND ds.LocalWarehouseStock > 0 AND ds.Pptcv > 0
            )
            SELECT BandId, Category, COUNT(*), SUM(Value), SUM(CASE WHEN DeadStockStage > 0 THEN Value ELSE 0 END),
                   MAX(CASE WHEN DeadStockStage > 0 THEN DeadObservedDays END)
            FROM x
            WHERE (@Band IS NULL OR BandId = @Band) AND (@Category IS NULL OR Category = @Category)
            GROUP BY BandId, Category
            """,
            p =>
            {
                p.AddWithValue("@L", layerId);
                p.AddWithValue("@Run", run.RunId);
                p.Add("@Snap", SqlDbType.Date).Value = run.SnapshotDate.ToDateTime(TimeOnly.MinValue);
                p.AddWithValue("@Band", (object?)bandId ?? DBNull.Value);
                p.Add("@Category", SqlDbType.NVarChar, 200).Value = (object?)category ?? DBNull.Value;
            },
            r => new StockAggregate(r.IsDBNull(0) ? null : Id(r, 0), r.GetString(1), r.GetInt32(2),
                r.GetDecimal(3), r.GetDecimal(4), r.IsDBNull(5) ? null : r.GetInt32(5)), ct);

    /// <summary>Booked unit cost, last 30 days vs the 60 before (<c>agent.CostChanges</c>), per SKU, with the
    /// band its recent cost incl. VAT falls in and its category from its own sales.</summary>
    public Task<List<CostChangeRow>> GetCostChangesAsync(int layerId, int? bandId = null, string? category = null,
        CancellationToken ct = default) =>
        QueryAsync("""
            DECLARE @Mall bit = (SELECT CASE WHEN Brand = 'GjirafaMall' THEN 1 ELSE 0 END FROM agent.Layers WHERE LayerId = @L);
            DECLARE @Vat decimal(9,4) = (SELECT 1 + VatRatePct / 100 FROM PricingTool.Layers WHERE Id = @L);
            WITH cat AS (
                SELECT Sku, MAX(CASE WHEN @Mall = 1 THEN MainGroup ELSE CategoryL1 END) AS Category
                FROM agent.SalesLines
                WHERE LayerId = @L AND SaleDate >= DATEADD(day, -90, CAST(GETDATE() AS date))
                GROUP BY Sku
            ), x AS (
                SELECT cc.Sku, b.BandId, cat.Category, cc.UnitCostPrior60, cc.UnitCostLast30, cc.ChangePct
                FROM agent.CostChanges cc
                JOIN cat ON cat.Sku = cc.Sku
                OUTER APPLY (SELECT TOP 1 bc.BandId FROM agent.BandConfig bc
                             WHERE bc.LayerId = @L AND cc.UnitCostLast30 * @Vat >= bc.MinPrice AND cc.UnitCostLast30 * @Vat < bc.MaxPrice
                             ORDER BY bc.SortOrder) b
                WHERE cc.LayerId = @L
            )
            SELECT Sku, BandId, Category, UnitCostPrior60, UnitCostLast30, ChangePct FROM x
            WHERE (@Band IS NULL OR BandId = @Band) AND (@Category IS NULL OR Category = @Category)
            """,
            p =>
            {
                p.AddWithValue("@L", layerId);
                p.AddWithValue("@Band", (object?)bandId ?? DBNull.Value);
                p.Add("@Category", SqlDbType.NVarChar, 200).Value = (object?)category ?? DBNull.Value;
            },
            r => new CostChangeRow(r.GetString(0), r.IsDBNull(1) ? null : Id(r, 1), r.IsDBNull(2) ? "Unknown" : r.GetString(2),
                r.GetDecimal(3), r.GetDecimal(4), r.IsDBNull(5) ? 0m : r.GetDecimal(5)), ct);

    /// <summary>Fitted elasticities for SKUs the latest run priced, with the engine's band for each.</summary>
    public Task<List<ElasticityFit>> GetElasticityFitsAsync(int layerId, EngineRunRef run, int? bandId = null, string? category = null,
        CancellationToken ct = default) =>
        QueryAsync("""
            DECLARE @Mall bit = (SELECT CASE WHEN Brand = 'GjirafaMall' THEN 1 ELSE 0 END FROM agent.Layers WHERE LayerId = @L);
            WITH x AS (
                SELECT e.Sku, pp.PriceBandId AS BandId,
                       CASE WHEN @Mall = 1 THEN ISNULL(p.MainGroup, N'Others') ELSE ISNULL(p.CategoryL1, N'Unknown') END AS Category,
                       e.IsUsable, e.Coefficient, e.ObservationCount
                FROM PricingTool.SkuElasticity e
                JOIN PricingTool.ProposedPrices pp ON pp.PricingRunId = @Run AND pp.Sku = e.Sku
                OUTER APPLY hk.ProductForProposal(@L, pp.ProductId, pp.Sku) p
                WHERE e.LayerId = @L
            )
            SELECT Sku, BandId, Category, IsUsable, Coefficient, ObservationCount FROM x
            WHERE (@Band IS NULL OR BandId = @Band) AND (@Category IS NULL OR Category = @Category)
            """,
            p =>
            {
                p.AddWithValue("@L", layerId);
                p.AddWithValue("@Run", run.RunId);
                p.AddWithValue("@Band", (object?)bandId ?? DBNull.Value);
                p.Add("@Category", SqlDbType.NVarChar, 200).Value = (object?)category ?? DBNull.Value;
            },
            r => new ElasticityFit(r.GetString(0), r.IsDBNull(1) ? null : Id(r, 1), r.GetString(2), r.GetBoolean(3),
                r.GetDecimal(4), r.GetInt32(5)), ct);

    /// <summary>Per day in [<paramref name="from"/>, <paramref name="to"/>): units and gross profit of band
    /// <paramref name="bandId"/> × <paramref name="category"/>, and of the <paramref name="holdouts"/> categories (all bands).
    /// One query; days without sales are absent.</summary>
    public Task<List<DaySales>> GetDailyRowAndHoldoutAsync(int layerId, int bandId, string category, IReadOnlyCollection<string> holdouts,
        DateOnly from, DateOnly to, CancellationToken ct = default) =>
        QueryAsync("""
            DECLARE @Mall bit = (SELECT CASE WHEN Brand = 'GjirafaMall' THEN 1 ELSE 0 END FROM agent.Layers WHERE LayerId = @L);
            WITH s AS (
                SELECT s.SaleDate, s.Qty, s.GrossProfit,
                       CASE WHEN s.BandId = @Band AND c.Category = @Category THEN 1 ELSE 0 END AS IsRow,
                       CASE WHEN c.Category IN (SELECT value FROM STRING_SPLIT(@Holdouts, N'|')) THEN 1 ELSE 0 END AS IsHoldout
                FROM agent.SalesLines s
                CROSS APPLY (SELECT CASE WHEN @Mall = 1 THEN s.MainGroup ELSE s.CategoryL1 END AS Category) c
                WHERE s.LayerId = @L AND s.SaleDate >= @From AND s.SaleDate < @To)
            SELECT s.SaleDate,
                   ISNULL(SUM(CASE WHEN s.IsRow = 1 THEN s.Qty END), 0), ISNULL(SUM(CASE WHEN s.IsRow = 1 THEN s.GrossProfit END), 0),
                   ISNULL(SUM(CASE WHEN s.IsHoldout = 1 THEN s.Qty END), 0), ISNULL(SUM(CASE WHEN s.IsHoldout = 1 THEN s.GrossProfit END), 0)
            FROM s
            WHERE s.IsRow = 1 OR s.IsHoldout = 1
            GROUP BY s.SaleDate
            """,
            p =>
            {
                p.AddWithValue("@L", layerId);
                p.AddWithValue("@Band", bandId);
                p.Add("@Category", SqlDbType.NVarChar, 200).Value = category;
                p.Add("@Holdouts", SqlDbType.NVarChar, 2000).Value = string.Join("|", holdouts);
                p.Add("@From", SqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
                p.Add("@To", SqlDbType.Date).Value = to.ToDateTime(TimeOnly.MinValue);
            },
            r => new DaySales(DateOnly.FromDateTime(r.GetDateTime(0)), Convert.ToDecimal(r.GetValue(1)), r.GetDecimal(2),
                Convert.ToDecimal(r.GetValue(3)), r.GetDecimal(4)), ct);

    /// <summary>
    /// For every campaign of the layer that ended before <paramref name="today"/>: sales per category in its four windows
    /// (see <see cref="CampaignSegmentRow"/>). Lines on other campaigns' days are left out of the three comparison windows.
    /// Discount depth uses the VAT-inclusive pre-discount value (GrossRevenue + DiscountGiven; ListValue is VAT-exclusive),
    /// and leaves out lines claiming more than 80 % off: 968 such lines carry 13 % of all discount and are data errors.
    /// </summary>
    public Task<List<CampaignSegmentRow>> GetCampaignSegmentsAsync(int layerId, string? category, DateOnly today, CancellationToken ct = default) =>
        QueryAsync("""
            DECLARE @Mall bit = (SELECT CASE WHEN Brand = 'GjirafaMall' THEN 1 ELSE 0 END FROM agent.Layers WHERE LayerId = @L);
            WITH c AS (SELECT CampaignId, StartDate, EndDate FROM agent.CampaignHistory WHERE LayerId = @L AND EndDate < @Today),
            w AS (
                SELECT CampaignId, 0 AS Seg, StartDate AS F, DATEADD(day, 1, EndDate) AS T FROM c
                UNION ALL SELECT CampaignId, 1, DATEADD(day, -28, StartDate), StartDate FROM c
                UNION ALL SELECT CampaignId, 2, DATEADD(day, -364, StartDate), DATEADD(day, -363, EndDate) FROM c
                UNION ALL SELECT CampaignId, 3, DATEADD(day, -392, StartDate), DATEADD(day, -364, StartDate) FROM c),
            allc AS (SELECT StartDate, EndDate FROM agent.CampaignHistory WHERE LayerId = @L)
            SELECT w.CampaignId, w.Seg, ISNULL(x.Category, N'Unknown'), SUM(s.Qty), ISNULL(SUM(s.NetRevenue), 0), ISNULL(SUM(s.GrossProfit), 0),
                   ISNULL(SUM(CASE WHEN s.DiscountGiven >= 0 AND s.DiscountGiven <= 0.8 * (s.GrossRevenue + s.DiscountGiven) THEN s.DiscountGiven END), 0),
                   ISNULL(SUM(CASE WHEN s.DiscountGiven >= 0 AND s.DiscountGiven <= 0.8 * (s.GrossRevenue + s.DiscountGiven) THEN s.GrossRevenue + s.DiscountGiven END), 0),
                   COUNT(*), SUM(CASE WHEN s.DiscountGiven > 0 THEN 1 ELSE 0 END)
            FROM w
            JOIN agent.SalesLines s ON s.LayerId = @L AND s.SaleDate >= w.F AND s.SaleDate < w.T
            CROSS APPLY (SELECT CASE WHEN @Mall = 1 THEN s.MainGroup ELSE s.CategoryL1 END AS Category) x
            WHERE (w.Seg = 0 OR NOT EXISTS (SELECT 1 FROM allc WHERE s.SaleDate BETWEEN allc.StartDate AND allc.EndDate))
              AND (@Category IS NULL OR x.Category = @Category)
            GROUP BY w.CampaignId, w.Seg, ISNULL(x.Category, N'Unknown')
            """,
            p =>
            {
                p.AddWithValue("@L", layerId);
                p.Add("@Today", SqlDbType.Date).Value = today.ToDateTime(TimeOnly.MinValue);
                p.Add("@Category", SqlDbType.NVarChar, 200).Value = (object?)category ?? DBNull.Value;
            },
            r => new CampaignSegmentRow(Id(r, 0), r.GetInt32(1), r.GetString(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5),
                r.GetDecimal(6), r.GetDecimal(7), r.GetInt32(8), r.GetInt32(9)), ct);

    /// <summary>Weekly sales for <paramref name="weeks"/> weeks from <paramref name="from"/>, and the same weeks a year earlier.</summary>
    public Task<List<WeekSales>> GetWeeklySalesAsync(int layerId, string? category, DateOnly from, int weeks, CancellationToken ct = default) =>
        QueryAsync("""
            DECLARE @Mall bit = (SELECT CASE WHEN Brand = 'GjirafaMall' THEN 1 ELSE 0 END FROM agent.Layers WHERE LayerId = @L);
            DECLARE @To date = DATEADD(day, 7 * @Weeks, @From);
            DECLARE @FromLy date = DATEADD(day, -364, @From), @ToLy date = DATEADD(day, -364, @To);
            SELECT k.Wk, k.Ly, SUM(s.Qty), ISNULL(SUM(s.NetRevenue), 0), ISNULL(SUM(s.GrossProfit), 0),
                   ISNULL(SUM(CASE WHEN s.DiscountGiven >= 0 AND s.DiscountGiven <= 0.8 * (s.GrossRevenue + s.DiscountGiven) THEN s.DiscountGiven END), 0),
                   ISNULL(SUM(CASE WHEN s.DiscountGiven >= 0 AND s.DiscountGiven <= 0.8 * (s.GrossRevenue + s.DiscountGiven) THEN s.GrossRevenue + s.DiscountGiven END), 0),
                   COUNT(*), SUM(CASE WHEN s.DiscountGiven > 0 THEN 1 ELSE 0 END)
            FROM agent.SalesLines s
            CROSS APPLY (SELECT CASE WHEN @Mall = 1 THEN s.MainGroup ELSE s.CategoryL1 END AS Category) x
            CROSS APPLY (SELECT CAST(CASE WHEN s.SaleDate >= @From THEN 0 ELSE 1 END AS bit) AS Ly,
                                CASE WHEN s.SaleDate >= @From THEN DATEDIFF(day, @From, s.SaleDate) ELSE DATEDIFF(day, @FromLy, s.SaleDate) END / 7 AS Wk) k
            WHERE s.LayerId = @L AND ((s.SaleDate >= @From AND s.SaleDate < @To) OR (s.SaleDate >= @FromLy AND s.SaleDate < @ToLy))
              AND (@Category IS NULL OR x.Category = @Category)
            GROUP BY k.Wk, k.Ly
            """,
            p =>
            {
                p.AddWithValue("@L", layerId);
                p.AddWithValue("@Weeks", weeks);
                p.Add("@From", SqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
                p.Add("@Category", SqlDbType.NVarChar, 200).Value = (object?)category ?? DBNull.Value;
            },
            r => new WeekSales(r.GetInt32(0), r.GetBoolean(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6),
                r.GetInt32(7), r.GetInt32(8)), ct);

    /// <summary>The layer's per-category floor/target overrides (written by the pricing agent within its limits).</summary>
    public Task<List<CategoryRuleRow>> GetCategoryRulesAsync(int layerId, CancellationToken ct = default) =>
        QueryAsync("""
            SELECT BandId, Category, MarginFloorPct, TargetMarginPct, UpdatedBy, UpdatedUtc
            FROM PricingTool.BandCategoryRules WHERE LayerId = @L ORDER BY BandId, Category
            """,
            p => p.AddWithValue("@L", layerId),
            r => new CategoryRuleRow(Id(r, 0), r.GetString(1), r.IsDBNull(2) ? null : r.GetDecimal(2), r.IsDBNull(3) ? null : r.GetDecimal(3),
                r.GetString(4), r.GetDateTime(5)), ct);

    /// <summary>The layer's first day of sales lines (comparison windows before it have no data).</summary>
    public async Task<DateOnly?> GetFirstSaleDayAsync(int layerId, CancellationToken ct = default) =>
        (await QueryAsync("SELECT MIN(SaleDate) FROM agent.SalesLines WHERE LayerId = @L", p => p.AddWithValue("@L", layerId),
            r => r.IsDBNull(0) ? (DateOnly?)null : DateOnly.FromDateTime(r.GetDateTime(0)), ct)).FirstOrDefault();

    public Task<List<CampaignWindow>> GetCampaignWindowsAsync(int layerId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        QueryAsync("""
            SELECT CampaignId, Name, StartDate, EndDate, CampaignType FROM agent.CampaignHistory
            WHERE LayerId = @L AND StartDate < @To AND EndDate >= @From ORDER BY StartDate
            """,
            p =>
            {
                p.AddWithValue("@L", layerId);
                p.Add("@From", SqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
                p.Add("@To", SqlDbType.Date).Value = to.ToDateTime(TimeOnly.MinValue);
            },
            r => new CampaignWindow(Id(r, 0), r.GetString(1), DateOnly.FromDateTime(r.GetDateTime(2)),
                DateOnly.FromDateTime(r.GetDateTime(3)), r.GetString(4)), ct);

    /// <summary>An id column, whatever integer width the table uses.</summary>
    private static int Id(SqlDataReader r, int i) => Convert.ToInt32(r.GetValue(i));

    private static void AddWindow(SqlParameterCollection p, int index, DateWindow w)
    {
        p.Add($"@From{index}", SqlDbType.Date).Value = w.From.ToDateTime(TimeOnly.MinValue);
        p.Add($"@To{index}", SqlDbType.Date).Value = w.To.ToDateTime(TimeOnly.MinValue);
    }

    private async Task<List<T>> QueryAsync<T>(string sql, Action<SqlParameterCollection> bind, Func<SqlDataReader, T> map,
        CancellationToken ct)
    {
        await using var conn = new SqlConnection(_source.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = CommandTimeoutSeconds };
        bind(cmd.Parameters);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct)) rows.Add(map(reader));
        return rows;
    }
}
