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
    /// <paramref name="ownOnly"/>: on GjirafaMall, only its own products (not Gjirafa50-listed ones, which take Gjirafa50's price,
    /// so a Mall floor never moves them); no effect on Gjirafa50.
    /// </summary>
    public Task<List<SalesAggregate>> GetSalesAsync(int layerId, DateWindow current, DateWindow comparison,
        decimal tolerancePp, decimal atFloorBandPp, int? bandId = null, string? category = null, bool ownOnly = false, CancellationToken ct = default) =>
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
                  AND (@Own = 0 OR @Mall = 0 OR ISNULL(s.IsGj50Listed, 0) = 0)
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
                p.AddWithValue("@Own", ownOnly);
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
    /// stock is what the engine itself staged as dead (<c>DeadStockStage</c> &gt; 0). <paramref name="ownOnly"/>: as in
    /// <see cref="GetSalesAsync"/>.</summary>
    public Task<List<StockAggregate>> GetStockAsync(int layerId, EngineRunRef run, int? bandId = null, string? category = null,
        bool ownOnly = false, CancellationToken ct = default) =>
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
                  AND (@Own = 0 OR @Mall = 0 OR ISNULL(p.IsGj50Listed, 0) = 0)
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
                p.AddWithValue("@Own", ownOnly);
            },
            r => new StockAggregate(r.IsDBNull(0) ? null : Id(r, 0), r.GetString(1), r.GetInt32(2),
                r.GetDecimal(3), r.GetDecimal(4), r.IsDBNull(5) ? null : r.GetInt32(5)), ct);

    /// <summary>Booked unit cost, last 30 days vs the 60 before (<c>agent.CostChanges</c>), per SKU, with the
    /// band its recent cost incl. VAT falls in and its category from its own sales. <paramref name="ownOnly"/>: as in
    /// <see cref="GetSalesAsync"/>.</summary>
    public Task<List<CostChangeRow>> GetCostChangesAsync(int layerId, int? bandId = null, string? category = null,
        bool ownOnly = false, CancellationToken ct = default) =>
        QueryAsync("""
            DECLARE @Mall bit = (SELECT CASE WHEN Brand = 'GjirafaMall' THEN 1 ELSE 0 END FROM agent.Layers WHERE LayerId = @L);
            DECLARE @Vat decimal(9,4) = (SELECT 1 + VatRatePct / 100 FROM PricingTool.Layers WHERE Id = @L);
            WITH cat AS (
                SELECT Sku, MAX(CASE WHEN @Mall = 1 THEN MainGroup ELSE CategoryL1 END) AS Category
                FROM agent.SalesLines
                WHERE LayerId = @L AND SaleDate >= DATEADD(day, -90, CAST(GETDATE() AS date))
                  AND (@Own = 0 OR @Mall = 0 OR ISNULL(IsGj50Listed, 0) = 0)
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
                p.AddWithValue("@Own", ownOnly);
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

    /// <summary>Gross profit on normal days (no campaign of the layer running) in [from, to): the business-size yardstick,
    /// counted the way the campaign segments count the 28 days before a campaign.</summary>
    public async Task<decimal> GetNormalDayGrossProfitAsync(int layerId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        (await QueryAsync("""
            SELECT ISNULL(SUM(s.GrossProfit), 0) FROM agent.SalesLines s
            WHERE s.LayerId = @L AND s.SaleDate >= @From AND s.SaleDate < @To
              AND NOT EXISTS (SELECT 1 FROM agent.CampaignHistory c WHERE c.LayerId = @L AND s.SaleDate BETWEEN c.StartDate AND c.EndDate)
            """,
            p =>
            {
                p.AddWithValue("@L", layerId);
                p.Add("@From", SqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
                p.Add("@To", SqlDbType.Date).Value = to.ToDateTime(TimeOnly.MinValue);
            },
            r => r.GetDecimal(0), ct)).Single();

    /// <summary>
    /// For every campaign of the layer that ended before <paramref name="today"/>: per SKU, its sales on the normal days of the
    /// planner's rate window (<see cref="Campaigns.CampaignReplay.RateWindow"/>: the 28 days ending 21 days before the start)
    /// and its sales in the campaign, booked COGS. A SKU appears when it sold in either.
    /// </summary>
    public Task<List<Campaigns.ReplaySkuRow>> GetCampaignReplayRowsAsync(int layerId, DateOnly today, CancellationToken ct = default) =>
        QueryAsync("""
            WITH camp AS (SELECT CampaignId, StartDate, EndDate, DATEADD(day, -@Back, StartDate) AS F, DATEADD(day, -@Back + @Days, StartDate) AS T
                          FROM agent.CampaignHistory WHERE LayerId = @L AND EndDate < @Today),
            n AS (SELECT camp.CampaignId, s.Sku, SUM(s.Qty) AS U, SUM(s.NetRevenue) AS Net, SUM(s.GrossProfit) AS Gp FROM camp
                  JOIN agent.SalesLines s ON s.LayerId = @L AND s.SaleDate >= camp.F AND s.SaleDate < camp.T
                  WHERE NOT EXISTS (SELECT 1 FROM agent.CampaignHistory c WHERE c.LayerId = @L AND s.SaleDate BETWEEN c.StartDate AND c.EndDate)
                  GROUP BY camp.CampaignId, s.Sku),
            c AS (SELECT camp.CampaignId, s.Sku, SUM(s.Qty) AS U, SUM(s.NetRevenue) AS Net, SUM(s.GrossProfit) AS Gp FROM camp
                  JOIN agent.SalesLines s ON s.LayerId = @L AND s.SaleDate >= camp.StartDate AND s.SaleDate <= camp.EndDate
                  GROUP BY camp.CampaignId, s.Sku)
            SELECT COALESCE(n.CampaignId, c.CampaignId), COALESCE(n.Sku, c.Sku), ISNULL(n.U, 0), ISNULL(n.Net, 0), ISNULL(n.Gp, 0),
                   ISNULL(c.U, 0), ISNULL(c.Net, 0), ISNULL(c.Gp, 0)
            FROM n FULL JOIN c ON c.CampaignId = n.CampaignId AND c.Sku = n.Sku
            """,
            p =>
            {
                p.AddWithValue("@L", layerId);
                p.AddWithValue("@Back", Campaigns.CampaignReplay.LeadDays + Campaigns.CampaignReplay.LagDays + Campaigns.CampaignReplay.RateDays);
                p.AddWithValue("@Days", Campaigns.CampaignReplay.RateDays);
                p.Add("@Today", SqlDbType.Date).Value = today.ToDateTime(TimeOnly.MinValue);
            },
            r => new Campaigns.ReplaySkuRow(Id(r, 0), r.GetString(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6),
                r.GetDecimal(7)), ct);

    /// <summary>The layer's booked sales lines in [from, to) for the lever backtest, with the category decisions are made at,
    /// whether a campaign of the layer ran that day, the line's id and whether it sold under a team deal (a product discount
    /// on the line, hk.SalesLineDiscountsKs, at a unit price no KS approval gave the SKU, within 1.1 %). The columns are
    /// agent.SalesLines' (the same joins), read from hk.SalesLines for the line id, which the agents' view leaves out. In sale-time
    /// order, so the walk-forward reads a day's lines the same way every run (lever 1's "last known cost", lever 3's episode).</summary>
    public Task<List<Backtest.BacktestLine>> GetBacktestLinesAsync(int layerId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        QueryAsync("""
            DECLARE @Mall bit = (SELECT CASE WHEN Brand = 'GjirafaMall' THEN 1 ELSE 0 END FROM agent.Layers WHERE LayerId = @L);
            SELECT DISTINCT i.Sku, i.NewPrice INTO #approved
            FROM PricingTool.ApprovalItems i JOIN PricingTool.Approvals a ON a.Id = i.ApprovalId JOIN PricingTool.Layers y ON y.Id = a.LayerId
            WHERE y.CountryCode = 'KS';
            CREATE INDEX ix ON #approved (Sku);
            SELECT CAST(s.DateAdded AS date), s.Sku, s.Qty, s.NetoPrice, s.BrutoPrice, s.Cogs, s.Margin, ISNULL(s.ProductDiscount, 0), ISNULL(s.StockClass, N''),
                   CAST(s.Cogs / NULLIF(s.Qty, 0) * ISNULL(s.Vat, 1.18) AS decimal(12,2)), b.Id,
                   CASE WHEN @Mall = 1 THEN ISNULL(p.MainGroup, N'Others') ELSE ISNULL(p.CategoryL1, N'Unknown') END,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM agent.CampaignHistory c WHERE c.LayerId = @L AND CAST(s.DateAdded AS date) BETWEEN c.StartDate AND c.EndDate) THEN 1 ELSE 0 END AS bit),
                   s.SrLineId,
                   CAST(CASE WHEN d.ProductDiscount > 0 AND NOT EXISTS (SELECT 1 FROM #approved x WHERE x.Sku = s.Sku AND ABS(x.NewPrice - s.BrutoPrice / s.Qty) <= 0.011 * x.NewPrice)
                             THEN 1 ELSE 0 END AS bit)
            FROM hk.SalesLines s
            LEFT JOIN hk.Products p ON p.LayerId = s.LayerId AND p.ProductId = s.ProductId
            LEFT JOIN hk.SalesLineDiscountsKs d ON d.SrLineId = s.SrLineId
            OUTER APPLY (SELECT TOP 1 pb.Id FROM PricingTool.PriceBands pb
                         WHERE pb.LayerId = s.LayerId
                           AND s.Cogs / NULLIF(s.Qty, 0) * ISNULL(s.Vat, 1.18) >= pb.MinPrice
                           AND s.Cogs / NULLIF(s.Qty, 0) * ISNULL(s.Vat, 1.18) < pb.MaxPrice
                         ORDER BY pb.SortOrder) b
            WHERE s.LayerId = @L AND s.DateAdded >= @From AND s.DateAdded < @To AND s.Qty > 0
            ORDER BY s.DateAdded, s.SrLineId
            """,
            p =>
            {
                p.AddWithValue("@L", layerId);
                p.Add("@From", SqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
                p.Add("@To", SqlDbType.Date).Value = to.ToDateTime(TimeOnly.MinValue);
            },
            r => new Backtest.BacktestLine(DateOnly.FromDateTime(r.GetDateTime(0)), r.GetString(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4),
                r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetDecimal(9), r.IsDBNull(10) ? null : Id(r, 10),
                r.GetString(11), r.GetBoolean(12)) { SrLineId = Convert.ToInt64(r.GetValue(13)), TeamDeal = r.GetBoolean(14) }, ct);

    /// <summary>
    /// Lever 8's lines (the rule of evidence/raises-not-on-shelf.sql, E23): sales of an approval's raised SKUs (new price over
    /// old × 1.02) on its layer, from the day after it went live to the end of its span, sold at the old price (within 1.1 %),
    /// where what held the old price wasn't a team deal: the cheapest public fixed-amount discount live at the sale at that
    /// price (hk.DiscountHistoryRaisedKs) carries a price an earlier approval gave the SKU (the calculator's earlier push), or
    /// there is none (the regular price was never raised). By line id: the old and the new price.
    /// </summary>
    public async Task<Dictionary<long, (decimal Old, decimal New)>> GetHeldRaisesAsync(int layerId, IReadOnlyList<Backtest.LeverBacktester.RaiseSpan> spans,
        CancellationToken ct = default)
    {
        var values = string.Join(", ", spans.Select(s => FormattableString.Invariant($"({s.ApprovalId}, '{s.LiveFrom:yyyy-MM-dd}', '{s.LiveTo:yyyy-MM-dd}')")));
        var rows = await QueryAsync($"""
            SELECT * INTO #spans FROM (VALUES {values}) v (ApprovalId, LiveFrom, LiveTo);
            DECLARE @Db nvarchar(64) = (SELECT OperationalDatabase FROM PricingTool.Layers WHERE Id = @L);
            SELECT sp.ApprovalId, sl.SrLineId, sl.Sku, sl.ProductId, sl.DateAdded, sl.BrutoPrice / sl.Qty AS UnitP, ai.OldPrice, ai.NewPrice
            INTO #l
            FROM #spans sp
            JOIN PricingTool.ApprovalItems ai ON ai.ApprovalId = sp.ApprovalId AND ai.NewPrice > ai.OldPrice * 1.02
            JOIN hk.SalesLines sl ON sl.Sku = ai.Sku AND sl.LayerId = @L
             AND sl.DateAdded >= DATEADD(day, 1, CAST(sp.LiveFrom AS date)) AND sl.DateAdded < DATEADD(day, 1, CAST(sp.LiveTo AS date))
            WHERE sl.Qty > 0 AND ABS(sl.BrutoPrice / sl.Qty - ai.OldPrice) <= 0.011 * ai.OldPrice;
            SELECT l.SrLineId, l.OldPrice, l.NewPrice
            FROM #l l
            OUTER APPLY (SELECT TOP 1 d.DiscountId, d.DiscountAmount
                         FROM hk.DiscountHistoryRaisedKs d
                         WHERE d.Db = @Db AND d.ProductId = l.ProductId AND d.DiscountApplyTypeId = 2 AND d.UsePercentage = 0
                           AND d.StartDateUtc <= l.DateAdded AND (d.EndDateUtc IS NULL OR d.EndDateUtc >= l.DateAdded)
                           AND ABS(d.DiscountAmount - l.UnitP) <= 0.011 * l.UnitP
                         ORDER BY d.DiscountAmount, d.StartDateUtc DESC) h
            WHERE h.DiscountId IS NULL
               OR EXISTS (SELECT 1 FROM PricingTool.ApprovalItems a2
                          WHERE a2.Sku = l.Sku AND a2.ApprovalId < l.ApprovalId AND ABS(a2.NewPrice - h.DiscountAmount) < 0.011)
            """,
            p => p.AddWithValue("@L", layerId),
            r => (Id: Convert.ToInt64(r.GetValue(0)), Old: r.GetDecimal(1), New: r.GetDecimal(2)), ct);
        var held = new Dictionary<long, (decimal Old, decimal New)>();
        foreach (var r in rows) held.TryAdd(r.Id, (r.Old, r.New));
        return held;
    }

    /// <summary>
    /// Backtest lever 5: for each SKU and day strictly between a run finishing and its prices being approved (local days, so
    /// the partial first and last days aren't counted), the price live (old) and the price the engine had already computed
    /// (new), where they differ by at least 0.5 %. A SKU-day inside two such gaps takes the later approval.
    /// </summary>
    public Task<List<(string Sku, DateOnly Day, decimal Old, decimal New)>> GetPendingPriceChangesAsync(int layerId, CancellationToken ct = default) =>
        QueryAsync("""
            WITH a AS (
                SELECT a.Id, a.ApprovedUtc,
                       DATEADD(day, 1, CAST(CAST(f.Done AS datetime2) AT TIME ZONE 'UTC' AT TIME ZONE 'Central European Standard Time' AS date)) AS FromDay,
                       CAST(CAST(a.ApprovedUtc AS datetime2) AT TIME ZONE 'UTC' AT TIME ZONE 'Central European Standard Time' AS date) AS ToDay
                FROM PricingTool.Approvals a
                CROSS APPLY (SELECT MAX(TimestampUtc) AS Done FROM PricingTool.AuditLog l
                             WHERE l.EntityType = 'PricingRun' AND l.EntityId = CAST(a.SourceRunId AS nvarchar(20)) AND l.Action LIKE 'Run finished%') f
                WHERE a.LayerId = @L AND f.Done IS NOT NULL),
            x AS (
                SELECT DISTINCT c.Sku, s.SaleDate, c.OldPrice, c.NewPrice, a.ApprovedUtc
                FROM a
                JOIN agent.TestPhasePriceChanges c ON c.ApprovalId = a.Id AND c.OldPrice > 0 AND ABS(c.NewPrice - c.OldPrice) >= 0.005 * c.OldPrice
                JOIN agent.SalesLines s ON s.LayerId = @L AND s.Sku = c.Sku AND s.SaleDate >= a.FromDay AND s.SaleDate < a.ToDay)
            SELECT Sku, SaleDate, OldPrice, NewPrice FROM (
                SELECT *, ROW_NUMBER() OVER (PARTITION BY Sku, SaleDate ORDER BY ApprovedUtc DESC) AS Rn FROM x) y WHERE Rn = 1
            """,
            p => p.AddWithValue("@L", layerId),
            r => (r.GetString(0), DateOnly.FromDateTime(r.GetDateTime(1)), r.GetDecimal(2), r.GetDecimal(3)), ct);

    /// <summary>The raises (at least 0.5 %) of one approval that went live by hand: SKU, old and new price.</summary>
    public Task<List<(string Sku, decimal Old, decimal New)>> GetApprovalRaisesAsync(int approvalId, CancellationToken ct = default) =>
        QueryAsync("""
            SELECT Sku, OldPrice, NewPrice FROM agent.TestPhasePriceChanges
            WHERE ApprovalId = @A AND OldPrice > 0 AND NewPrice >= 1.005 * OldPrice
            """,
            p => p.AddWithValue("@A", approvalId),
            r => (r.GetString(0), r.GetDecimal(1), r.GetDecimal(2)), ct);

    /// <summary>Gross profit per category on normal days (no campaign of the layer) in [from, to): lever 6's before/after.</summary>
    public Task<List<(string Category, decimal Gp)>> GetCategoryNormalDayGpAsync(int layerId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        QueryAsync("""
            DECLARE @Mall bit = (SELECT CASE WHEN Brand = 'GjirafaMall' THEN 1 ELSE 0 END FROM agent.Layers WHERE LayerId = @L);
            SELECT ISNULL(CASE WHEN @Mall = 1 THEN s.MainGroup ELSE s.CategoryL1 END, N'Unknown'), SUM(s.GrossProfit)
            FROM agent.SalesLines s
            WHERE s.LayerId = @L AND s.SaleDate >= @From AND s.SaleDate < @To
              AND NOT EXISTS (SELECT 1 FROM agent.CampaignHistory c WHERE c.LayerId = @L AND s.SaleDate BETWEEN c.StartDate AND c.EndDate)
            GROUP BY ISNULL(CASE WHEN @Mall = 1 THEN s.MainGroup ELSE s.CategoryL1 END, N'Unknown')
            """,
            p =>
            {
                p.AddWithValue("@L", layerId);
                p.Add("@From", SqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
                p.Add("@To", SqlDbType.Date).Value = to.ToDateTime(TimeOnly.MinValue);
            },
            r => (r.GetString(0), r.GetDecimal(1)), ct);

    /// <summary>
    /// Campaign candidates as of a past window, from the booked sales lines of its normal days: per SKU, the average VAT-inclusive
    /// unit price, the highest pre-discount unit price a line booked (lines over 80 % off left out), the booked unit cost incl.
    /// VAT, units per normal day and whether it is Gjirafa50-listed. Stock unknown (counted as 1), no dead-stock lane.
    /// </summary>
    public async Task<IReadOnlyList<Campaigns.CampaignCandidate>> GetAsOfCandidatesAsync(int layerId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        await QueryAsync("""
            DECLARE @Mall bit = (SELECT CASE WHEN Brand = 'GjirafaMall' THEN 1 ELSE 0 END FROM agent.Layers WHERE LayerId = @L);
            DECLARE @NormalDays int = (
                SELECT COUNT(*) FROM (SELECT TOP (DATEDIFF(day, @From, @To)) DATEADD(day, ROW_NUMBER() OVER (ORDER BY (SELECT 1)) - 1, @From) AS D
                                      FROM sys.all_objects) d
                WHERE NOT EXISTS (SELECT 1 FROM agent.CampaignHistory c WHERE c.LayerId = @L AND d.D BETWEEN c.StartDate AND c.EndDate));
            SELECT s.Sku, MAX(s.BandId), ISNULL(MAX(CASE WHEN @Mall = 1 THEN s.MainGroup ELSE s.CategoryL1 END), N'Others'), MAX(s.Brand),
                   MAX(CASE WHEN s.DiscountGiven > 0 AND s.DiscountGiven <= 0.8 * (s.GrossRevenue + s.DiscountGiven) THEN (s.GrossRevenue + s.DiscountGiven) / s.Qty END),
                   SUM(s.GrossRevenue) / SUM(s.Qty), SUM(s.UnitCostInclVat * s.Qty) / SUM(s.Qty), SUM(s.Qty) / NULLIF(@NormalDays, 0),
                   CAST(MAX(CAST(s.IsGj50Listed AS int)) AS bit)
            FROM agent.SalesLines s
            WHERE s.LayerId = @L AND s.SaleDate >= @From AND s.SaleDate < @To AND s.Qty > 0 AND s.UnitCostInclVat > 0
              AND NOT EXISTS (SELECT 1 FROM agent.CampaignHistory c WHERE c.LayerId = @L AND s.SaleDate BETWEEN c.StartDate AND c.EndDate)
            GROUP BY s.Sku
            HAVING SUM(s.GrossRevenue) > 0
            """,
            p =>
            {
                p.AddWithValue("@L", layerId);
                p.Add("@From", SqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
                p.Add("@To", SqlDbType.Date).Value = to.ToDateTime(TimeOnly.MinValue);
            },
            r =>
            {
                var current = r.GetDecimal(5);
                var regular = r.IsDBNull(4) ? current : Math.Max(current, r.GetDecimal(4));
                return new Campaigns.CampaignCandidate(r.GetString(0), r.IsDBNull(1) ? null : Id(r, 1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                    Math.Round(regular, 2), Math.Round(current, 2), Math.Round(r.GetDecimal(6), 4), r.IsDBNull(7) ? 0 : r.GetDecimal(7), 1, 0, false, r.GetBoolean(8));
            }, ct);

    /// <summary>A window's booked result, split by Gjirafa50-listed or not: net revenue (VAT excl.) and gross profit.</summary>
    public Task<List<(bool Gj50Listed, decimal Net, decimal Gp)>> GetWindowResultAsync(int layerId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        QueryAsync("""
            SELECT CAST(ISNULL(s.IsGj50Listed, 0) AS bit), SUM(s.NetRevenue), SUM(s.GrossProfit) FROM agent.SalesLines s
            WHERE s.LayerId = @L AND s.SaleDate >= @From AND s.SaleDate < @To GROUP BY CAST(ISNULL(s.IsGj50Listed, 0) AS bit)
            """,
            p =>
            {
                p.AddWithValue("@L", layerId);
                p.Add("@From", SqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
                p.Add("@To", SqlDbType.Date).Value = to.ToDateTime(TimeOnly.MinValue);
            },
            r => (r.GetBoolean(0), r.GetDecimal(1), r.GetDecimal(2)), ct);

    /// <summary>A window's normal days (campaign days left out), split by Gjirafa50-listed or not: units, net revenue (VAT excl.) and
    /// booked gross profit.</summary>
    public Task<List<(bool Gj50Listed, decimal Units, decimal Net, decimal Gp)>> GetNormalDaySplitAsync(int layerId, DateWindow window, CancellationToken ct = default) =>
        QueryAsync("""
            SELECT CAST(ISNULL(s.IsGj50Listed, 0) AS bit), ISNULL(SUM(s.Qty), 0), ISNULL(SUM(s.NetRevenue), 0), ISNULL(SUM(s.GrossProfit), 0)
            FROM agent.SalesLines s
            WHERE s.LayerId = @L AND s.SaleDate >= @From0 AND s.SaleDate < @To0
              AND NOT EXISTS (SELECT 1 FROM agent.CampaignHistory c WHERE c.LayerId = @L AND s.SaleDate BETWEEN c.StartDate AND c.EndDate)
            GROUP BY CAST(ISNULL(s.IsGj50Listed, 0) AS bit)
            """,
            p =>
            {
                p.AddWithValue("@L", layerId);
                AddWindow(p, 0, window);
            },
            r => (r.GetBoolean(0), Convert.ToDecimal(r.GetValue(1)), r.GetDecimal(2), r.GetDecimal(3)), ct);

    /// <summary>
    /// The shop's effective margin on the products a run priced that sold on normal days in [<paramref name="from"/>,
    /// <paramref name="to"/>), each on the units it sold (the margin-target rule's basis): at today's shop price (the run's current
    /// price) and at the run's proposed price, against PPTCV (VAT included, as the price, so the ratio is the VAT-excluded one).
    /// Split by whether the run took Gjirafa50's price (GjirafaMall's shared products, reason SYNCED_TO_GJ50).
    /// </summary>
    public Task<List<(bool TakesGj50Price, decimal NetNow, decimal GpNow, decimal NetRun, decimal GpRun)>> GetShopMarginAsync(int layerId, long runId,
        DateOnly from, DateOnly to, CancellationToken ct = default) =>
        QueryAsync("""
            WITH s AS (
                SELECT sl.Sku, SUM(sl.Qty) AS Units
                FROM agent.SalesLines sl
                WHERE sl.LayerId = @L AND sl.SaleDate >= @From AND sl.SaleDate < @To
                  AND NOT EXISTS (SELECT 1 FROM agent.CampaignHistory c WHERE c.LayerId = @L AND sl.SaleDate BETWEEN c.StartDate AND c.EndDate)
                GROUP BY sl.Sku
                HAVING SUM(sl.Qty) > 0)
            SELECT CAST(CASE WHEN pp.ReasonCodes LIKE '%SYNCED_TO_GJ50%' THEN 1 ELSE 0 END AS bit),
                   SUM(s.Units * pp.CurrentPrice), SUM(s.Units * (pp.CurrentPrice - pp.Pptcv)),
                   SUM(s.Units * pp.ProposedPriceValue), SUM(s.Units * (pp.ProposedPriceValue - pp.Pptcv))
            FROM s
            JOIN PricingTool.ProposedPrices pp ON pp.PricingRunId = @Run AND pp.Sku = s.Sku
            WHERE pp.Pptcv > 0 AND pp.CurrentPrice > 0 AND pp.ProposedPriceValue > 0
            GROUP BY CAST(CASE WHEN pp.ReasonCodes LIKE '%SYNCED_TO_GJ50%' THEN 1 ELSE 0 END AS bit)
            """,
            p =>
            {
                p.AddWithValue("@L", layerId);
                p.AddWithValue("@Run", runId);
                p.Add("@From", SqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
                p.Add("@To", SqlDbType.Date).Value = to.ToDateTime(TimeOnly.MinValue);
            },
            r => (r.GetBoolean(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4)), ct);

    /// <summary>
    /// Backtest lever 4: the MK/AL layers' tool-scope gross profit per month in EUR (hk.SalesLinesMkAl, loaded by the data session,
    /// converted at the shop's Currency.Rate of the load day) and whether a regular-price push started that month (hk.PushDatesMkAl:
    /// public, all-platform, at least the given number of products).
    /// </summary>
    public Task<List<Backtest.CoverageMonth>> GetCoverageMonthsAsync(int pushMinDiscounts, CancellationToken ct = default) =>
        QueryAsync("""
            SELECT s.LayerId, FORMAT(s.DateAdded, 'yyyy-MM') AS M, SUM(s.GrossProfitEur),
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM hk.PushDatesMkAl p WHERE p.LayerId = s.LayerId AND p.ApplyType = 2 AND p.PlatformType = 0
                                          AND p.Discounts >= @Min AND FORMAT(p.StartDay, 'yyyy-MM') = FORMAT(s.DateAdded, 'yyyy-MM')) THEN 1 ELSE 0 END AS bit)
            FROM hk.SalesLinesMkAl s
            GROUP BY s.LayerId, FORMAT(s.DateAdded, 'yyyy-MM')
            ORDER BY M, s.LayerId
            """,
            p => p.AddWithValue("@Min", pushMinDiscounts),
            r => new Backtest.CoverageMonth(Id(r, 0), r.GetString(1), r.GetDecimal(2), r.GetBoolean(3)), ct);

    /// <summary>Kosovo's gross profit (both layers, every day) in [from, to): the base the calculator's measured gain is a share of.</summary>
    public async Task<decimal> GetKosovoGrossProfitAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        (await QueryAsync("""
            SELECT ISNULL(SUM(GrossProfit), 0) FROM agent.SalesLines WHERE LayerId IN (1, 4) AND SaleDate >= @From AND SaleDate < @To
            """,
            p =>
            {
                p.Add("@From", SqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
                p.Add("@To", SqlDbType.Date).Value = to.ToDateTime(TimeOnly.MinValue);
            },
            r => r.GetDecimal(0), ct)).Single();

    /// <summary>The people's band edits in the audit log (old and new band JSON), the history of the hand-set floors.</summary>
    public Task<List<(DateTime Utc, int BandId, string? OldJson, string? NewJson)>> GetBandFloorEditsAsync(int layerId, CancellationToken ct = default) =>
        QueryAsync("""
            SELECT TimestampUtc, EntityId, OldValue, NewValue FROM PricingTool.AuditLog
            WHERE LayerId = @L AND EntityType = 'PriceBand' AND UserName <> 'Alpha' AND ISNUMERIC(EntityId) = 1
            ORDER BY TimestampUtc
            """,
            p => p.AddWithValue("@L", layerId),
            r => (r.GetDateTime(0), int.Parse(r.GetString(1), System.Globalization.CultureInfo.InvariantCulture), r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3)), ct);

    /// <summary>The layer's first day of sales lines (comparison windows before it have no data).</summary>
    public async Task<DateOnly?> GetFirstSaleDayAsync(int layerId, CancellationToken ct = default) =>
        (await QueryAsync("SELECT MIN(SaleDate) FROM agent.SalesLines WHERE LayerId = @L", p => p.AddWithValue("@L", layerId),
            r => r.IsDBNull(0) ? (DateOnly?)null : DateOnly.FromDateTime(r.GetDateTime(0)), ct)).FirstOrDefault();

    public Task<List<CampaignWindow>> GetCampaignWindowsAsync(int layerId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        QueryAsync("""
            SELECT CampaignId, Name, StartDate, EndDate, CampaignType, Notes FROM agent.CampaignHistory
            WHERE LayerId = @L AND StartDate < @To AND EndDate >= @From ORDER BY StartDate
            """,
            p =>
            {
                p.AddWithValue("@L", layerId);
                p.Add("@From", SqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
                p.Add("@To", SqlDbType.Date).Value = to.ToDateTime(TimeOnly.MinValue);
            },
            r => new CampaignWindow(Id(r, 0), r.GetString(1), DateOnly.FromDateTime(r.GetDateTime(2)),
                DateOnly.FromDateTime(r.GetDateTime(3)), r.GetString(4)) { Notes = r.IsDBNull(5) ? null : r.GetString(5) }, ct);

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
