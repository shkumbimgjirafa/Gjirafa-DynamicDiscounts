using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Storage;
using PricingTool.Data;
using PricingTool.Data.Entities;

namespace PricingTool.Web.Services;

/// <summary>One category × band cell of a run: priced SKUs, and how many the run changed (up, down).</summary>
public sealed record RunGridCell(string Category, int BandId, int Skus, int Changed, int Up, int Down);

/// <summary>Why the publisher left a run's SKUs out, once per SKU.</summary>
public sealed record LeftOutReason(string Kind, int Skus);

/// <summary>The Run tab: the layer's current run treated as live, what it changed, whether it is working, and Alpha's account.</summary>
public class RunReport
{
    public Layer Layer { get; init; } = null!;
    /// <summary>The newest regular publish of a run that went through (published, not rolled back), and its run.</summary>
    public PublishBatch? Batch { get; init; }
    public PricingRun? Run { get; init; }
    public RunWeek? Week { get; init; }
    public IReadOnlyList<RunWeekStep> WeekSteps { get; init; } = Array.Empty<RunWeekStep>();
    /// <summary>The publish before it, of another run: the comparison for "what changed".</summary>
    public PublishBatch? PreviousBatch { get; init; }
    public IReadOnlyList<PricingRunBandTarget> Bands { get; init; } = Array.Empty<PricingRunBandTarget>();
    /// <summary>Alpha's per-category floors and targets the run used (only the cells it set; the rest use the band's).</summary>
    public IReadOnlyList<PricingRunCategoryRule> Rules { get; init; } = Array.Empty<PricingRunCategoryRule>();
    /// <summary>Null when the run's proposals were deleted by retention.</summary>
    public IReadOnlyList<RunGridCell>? Cells { get; init; }
    public IReadOnlyList<RunGridCell>? PreviousCells { get; init; }
    public AgentPass? AlphaPass { get; init; }
    /// <summary>Alpha's floor and target changes on this layer in the last 7 days (applied, keeps left out).</summary>
    public IReadOnlyList<AgentDecision> AlphaChanges { get; init; } = Array.Empty<AgentDecision>();
    public AgentRunCheck? Check { get; init; }
    public IReadOnlyList<LeftOutReason> LeftOut { get; init; } = Array.Empty<LeftOutReason>();
    /// <summary>Estimated: each written price change × the SKU's last 30 days' units, VAT excluded, no volume response.</summary>
    public decimal? ExpectedGp30 { get; init; }
    public int ExpectedSkus { get; init; }
    /// <summary>Measured: booked gross profit (VAT excluded) this month so far, and the same days of last month.</summary>
    public decimal MonthToDateGp { get; init; }
    public decimal LastMonthSameDaysGp { get; init; }
    public DateTime? SalesThrough { get; init; }
    public IReadOnlyList<string> Holdouts { get; init; } = Array.Empty<string>();

    public bool IsHoldout(string category) => Holdouts.Any(h => string.Equals(h.Trim(), category.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Prices a shopper now pays at our price: written, or already at it.</summary>
    public int OnShelf => Batch is null ? 0 : Batch.Created + Batch.Updated + Batch.Unchanged + Batch.TierRaised + Batch.TierInserted;

    public (decimal? Floor, decimal? Target, bool ByAlpha) FloorTarget(string category, int bandId)
    {
        var rule = Rules.FirstOrDefault(r => r.BandId == bandId && string.Equals(r.Category, category, StringComparison.OrdinalIgnoreCase));
        if (rule is not null) return (rule.MarginFloorPct, rule.TargetMarginPct, true);
        var band = Bands.FirstOrDefault(b => b.BandId == bandId);
        return (band?.MarginFloorPct, band?.TargetMarginPct, false);
    }
}

/// <summary>Builds the Run tab from the publish records, the run's captured bands and rules, its proposals and the sales lines.
/// The grid and the expected gross profit are cached per run and batch (both are fixed once written).</summary>
public class RunReportService
{
    private readonly PricingToolDbContext _db;
    private readonly AgentDbContext _ai;
    private readonly IMemoryCache _cache;

    public RunReportService(PricingToolDbContext db, AgentDbContext ai, IMemoryCache cache)
    {
        _db = db;
        _ai = ai;
        _cache = cache;
    }

    /// <summary>A layer's regular publishes of a run that went through (published, not rolled back), newest first: the first is
    /// the current run. The cost watcher's one-SKU publishes, expiries and campaigns have no run and are left out.</summary>
    public static IQueryable<PublishBatch> Published(IQueryable<PublishBatch> batches, int layerId) => batches
        .Where(b => b.LayerId == layerId && b.Kind == "Regular" && b.EngineRunId != null && b.Status == "Published" && b.RolledBackUtc == null)
        .OrderByDescending(b => b.Id);

    private IQueryable<PublishBatch> Published(int layerId) => Published(_ai.PublishBatches.AsNoTracking(), layerId);

    public async Task<RunReport> BuildAsync(int layerId, IReadOnlyList<string> holdouts, CancellationToken ct = default)
    {
        var layer = await _db.Layers.AsNoTracking().SingleAsync(l => l.Id == layerId, ct);
        var batch = await Published(layerId).FirstOrDefaultAsync(ct);
        if (batch is null) return new RunReport { Layer = layer, Holdouts = holdouts };
        var runId = batch.EngineRunId!.Value;
        var previous = await Published(layerId).Where(b => b.Id < batch.Id && b.EngineRunId != runId).FirstOrDefaultAsync(ct);
        var week = await _ai.RunWeeks.AsNoTracking().Where(w => w.PublishBatchId == batch.Id).OrderByDescending(w => w.Id).FirstOrDefaultAsync(ct);
        var since = DateTime.UtcNow.AddDays(-7);
        var (mtd, lastMonth, through) = await MonthToDateAsync(layerId, ct);
        var (expected, expectedSkus) = await ExpectedGpAsync(batch, layer, ct);

        return new RunReport
        {
            Layer = layer,
            Batch = batch,
            Run = await _db.PricingRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, ct),
            Week = week,
            WeekSteps = week is null ? Array.Empty<RunWeekStep>() : await _ai.RunWeekSteps.AsNoTracking().Where(s => s.RunWeekId == week.Id).OrderBy(s => s.Id).ToListAsync(ct),
            PreviousBatch = previous,
            Bands = await _db.PricingRunBandTargets.AsNoTracking().Where(b => b.PricingRunId == runId).OrderBy(b => b.MinPrice).ToListAsync(ct),
            Rules = await _db.PricingRunCategoryRules.AsNoTracking().Where(r => r.PricingRunId == runId).ToListAsync(ct),
            Cells = await GridAsync(runId, ct),
            PreviousCells = previous is null ? null : await GridAsync(previous.EngineRunId!.Value, ct),
            AlphaPass = await _ai.AgentPasses.AsNoTracking().Where(p => p.LayerId == layerId && p.Source == FloorTargetAgent.Source && !p.IsTest)
                .OrderByDescending(p => p.Id).FirstOrDefaultAsync(ct),
            AlphaChanges = await _ai.AgentDecisions.AsNoTracking()
                .Where(d => d.LayerId == layerId && d.Source == FloorTargetAgent.Source && d.Status == DecisionStatus.Applied && d.Action != "keep" && d.CreatedUtc >= since
                            && _ai.AgentPasses.Any(p => p.Id == d.PassId && !p.IsTest))
                .OrderByDescending(d => d.Id).ToListAsync(ct),
            Check = await _ai.AgentRunChecks.AsNoTracking().Where(c => c.RunId == runId && !c.IsTest).OrderByDescending(c => c.Id).FirstOrDefaultAsync(ct),
            LeftOut = (await _ai.PublishExceptions.AsNoTracking().Where(e => e.BatchId == batch.Id)
                    .GroupBy(e => e.Kind).Select(g => new { g.Key, Skus = g.Select(e => e.Sku).Distinct().Count() }).ToListAsync(ct))
                .Select(x => new LeftOutReason(x.Key, x.Skus)).OrderByDescending(x => x.Skus).ToList(),
            ExpectedGp30 = expected,
            ExpectedSkus = expectedSkus,
            MonthToDateGp = mtd,
            LastMonthSameDaysGp = lastMonth,
            SalesThrough = through,
            Holdouts = holdouts,
        };
    }

    /// <summary>The run's priced SKUs by main category (GjirafaMall main group, Gjirafa50 L1 category, the same map the engine
    /// and the checker use) and band, with the changed counts. Null when the run's proposals no longer exist.</summary>
    public async Task<IReadOnlyList<RunGridCell>?> GridAsync(long runId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue($"run-grid:{runId}", out IReadOnlyList<RunGridCell>? cached)) return cached;
        if (!await _db.ProposedPrices.AnyAsync(p => p.PricingRunId == runId, ct)) return null;
        var cells = new List<RunGridCell>();
        var conn = (SqlConnection)_db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) await conn.OpenAsync(ct);
        try
        {
            await using var cmd = new SqlCommand("""
                DECLARE @L int = (SELECT LayerId FROM PricingTool.PricingRuns WHERE Id = @Run);
                DECLARE @Mall bit = (SELECT CASE WHEN Brand = 'GjirafaMall' THEN 1 ELSE 0 END FROM PricingTool.Layers WHERE Id = @L);
                SELECT ISNULL(c.Category, N'Unknown'), pp.PriceBandId, COUNT(*),
                       SUM(CASE WHEN pp.HasChange = 1 THEN 1 ELSE 0 END),
                       SUM(CASE WHEN pp.HasChange = 1 AND pp.ProposedPriceValue > pp.CurrentPrice THEN 1 ELSE 0 END),
                       SUM(CASE WHEN pp.HasChange = 1 AND pp.ProposedPriceValue < pp.CurrentPrice THEN 1 ELSE 0 END)
                FROM PricingTool.ProposedPrices pp
                OUTER APPLY (SELECT CASE WHEN @Mall = 1 THEN ISNULL(p.MainGroup, N'Others') ELSE ISNULL(p.CategoryL1, N'Unknown') END AS Category
                             FROM hk.ProductForProposal(pp.LayerId, pp.ProductId, pp.Sku) p) c
                WHERE pp.PricingRunId = @Run AND pp.Status <> @Skipped AND pp.PriceBandId IS NOT NULL
                GROUP BY ISNULL(c.Category, N'Unknown'), pp.PriceBandId
                """, conn) { CommandTimeout = 300 };
            cmd.Parameters.AddWithValue("@Run", runId);
            cmd.Parameters.AddWithValue("@Skipped", (int)ProposalStatus.Skipped);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                cells.Add(new RunGridCell(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5)));
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
        _cache.Set($"run-grid:{runId}", (IReadOnlyList<RunGridCell>)cells, TimeSpan.FromHours(12));
        return cells;
    }

    /// <summary>The written price changes of a batch × each SKU's last 30 days' units (the snapshot of the run's day), VAT
    /// excluded, with no volume response: the export's formula. Estimated; null when the run's proposals are gone.</summary>
    private async Task<(decimal? Gp, int Skus)> ExpectedGpAsync(PublishBatch batch, Layer layer, CancellationToken ct)
    {
        if (_cache.TryGetValue($"run-expected:{batch.Id}", out (decimal?, int) cached)) return cached;
        var conn = (SqlConnection)_db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) await conn.OpenAsync(ct);
        (decimal?, int) result;
        try
        {
            await using var cmd = new SqlCommand("""
                DECLARE @Date date = (SELECT CAST(StartedUtc AS date) FROM PricingTool.PricingRuns WHERE Id = @Run);
                SELECT COUNT(*), SUM((pp.ProposedPriceValue - pp.CurrentPrice) / @Vat * ISNULL(s.Qty30, 0))
                FROM PricingTool.ProposedPrices pp
                JOIN (SELECT DISTINCT Sku FROM ai.PublishAudit WHERE BatchId = @Batch) a ON a.Sku = pp.Sku
                OUTER APPLY (SELECT TOP 1 d.Qty30 FROM PricingTool.DailySnapshots d WHERE d.LayerId = @L AND d.SnapshotDate = @Date AND d.Sku = pp.Sku) s
                WHERE pp.PricingRunId = @Run AND pp.HasChange = 1
                """, conn) { CommandTimeout = 120 };
            cmd.Parameters.AddWithValue("@Run", batch.EngineRunId!.Value);
            cmd.Parameters.AddWithValue("@Batch", batch.Id);
            cmd.Parameters.AddWithValue("@L", layer.Id);
            cmd.Parameters.AddWithValue("@Vat", 1m + layer.VatRatePct / 100m);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            var n = r.GetInt32(0);
            result = (n == 0 || r.IsDBNull(1) ? null : Math.Round(r.GetDecimal(1), 0), n);
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
        _cache.Set($"run-expected:{batch.Id}", result, TimeSpan.FromHours(12));
        return result;
    }

    /// <summary>Booked gross profit (VAT excluded) of the layer this month so far, against the same days of last month, from the
    /// refreshed sales lines. The real shop's sales: sandbox prices never reach a customer.</summary>
    private async Task<(decimal Mtd, decimal LastMonth, DateTime? Through)> MonthToDateAsync(int layerId, CancellationToken ct)
    {
        var conn = (SqlConnection)_db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) await conn.OpenAsync(ct);
        try
        {
            await using var cmd = new SqlCommand("""
                DECLARE @Through datetime2 = (SELECT MAX(DateAdded) FROM hk.SalesLines WHERE LayerId = @L);
                DECLARE @Month date = DATEFROMPARTS(YEAR(@Through), MONTH(@Through), 1);
                DECLARE @Prev date = DATEADD(month, -1, @Month);
                DECLARE @PrevEnd datetime2 = DATEADD(second, DATEDIFF(second, CAST(@Month AS datetime2), @Through), CAST(@Prev AS datetime2));
                SELECT @Through,
                       ISNULL(SUM(CASE WHEN DateAdded >= @Month THEN Margin END), 0),
                       ISNULL(SUM(CASE WHEN DateAdded >= @Prev AND DateAdded <= @PrevEnd THEN Margin END), 0)
                FROM hk.SalesLines WHERE LayerId = @L AND DateAdded >= @Prev
                """, conn) { CommandTimeout = 60 };
            cmd.Parameters.AddWithValue("@L", layerId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return (r.GetDecimal(1), r.GetDecimal(2), r.IsDBNull(0) ? null : r.GetDateTime(0));
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
    }
}
