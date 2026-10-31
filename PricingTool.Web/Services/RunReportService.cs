using Microsoft.Data.SqlClient;
using PricingTool.Agent.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Storage;
using PricingTool.Data;
using PricingTool.Data.Entities;

namespace PricingTool.Web.Services;

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
    /// <summary>The run's stored report (<c>ai.RunReport</c>), with Alpha's account; null until it is written.</summary>
    public RunReportRecord? Stored { get; init; }
    public IReadOnlyList<string> Watch { get; init; } = Array.Empty<string>();
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
    private readonly IRunFacts _facts;
    private readonly IMemoryCache _cache;

    public RunReportService(PricingToolDbContext db, AgentDbContext ai, IRunFacts facts, IMemoryCache cache)
    {
        _db = db;
        _ai = ai;
        _facts = facts;
        _cache = cache;
    }

    private IQueryable<PublishBatch> Published(int layerId) => RunReportWriter.Published(_ai.PublishBatches.AsNoTracking(), layerId);

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
        var stored = await _ai.RunReports.AsNoTracking().SingleOrDefaultAsync(r => r.BatchId == batch.Id, ct);
        var previousStored = previous is null ? null : await _ai.RunReports.AsNoTracking().SingleOrDefaultAsync(r => r.BatchId == previous.Id, ct);
        var (expected, expectedSkus) = stored is not null ? (stored.ExpectedGp30Eur, stored.ExpectedSkus) : await ExpectedGpAsync(batch, layer, ct);

        return new RunReport
        {
            Layer = layer,
            Batch = batch,
            Run = await _db.PricingRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, ct),
            Week = week,
            Stored = stored,
            Watch = stored?.AlphaWatch is { } w ? System.Text.Json.JsonSerializer.Deserialize<List<string>>(w) ?? new List<string>() : new List<string>(),
            WeekSteps = week is null ? Array.Empty<RunWeekStep>() : await _ai.RunWeekSteps.AsNoTracking().Where(s => s.RunWeekId == week.Id).OrderBy(s => s.Id).ToListAsync(ct),
            PreviousBatch = previous,
            Bands = await _db.PricingRunBandTargets.AsNoTracking().Where(b => b.PricingRunId == runId).OrderBy(b => b.MinPrice).ToListAsync(ct),
            Rules = await _db.PricingRunCategoryRules.AsNoTracking().Where(r => r.PricingRunId == runId).ToListAsync(ct),
            Cells = stored is not null ? await StoredCellsAsync(stored.Id, ct) : await GridAsync(runId, ct),
            PreviousCells = previousStored is not null ? await StoredCellsAsync(previousStored.Id, ct)
                : previous is null ? null : await GridAsync(previous.EngineRunId!.Value, ct),
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

    /// <summary>A stored report's cells.</summary>
    private async Task<IReadOnlyList<RunGridCell>> StoredCellsAsync(long reportId, CancellationToken ct) =>
        (await _ai.RunReportCells.AsNoTracking().Where(c => c.ReportId == reportId).ToListAsync(ct))
        .Select(c => new RunGridCell(c.Category, c.BandId, c.Skus, c.Changed, c.Up, c.Down)).ToList();

    /// <summary>A run without a stored report: its cells counted now from its proposals (cached; a run never changes). Null
    /// when the run's proposals no longer exist.</summary>
    public async Task<IReadOnlyList<RunGridCell>?> GridAsync(long runId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue($"run-grid:{runId}", out IReadOnlyList<RunGridCell>? cached)) return cached;
        var cells = await _facts.GridAsync(runId, ct);
        if (cells is not null) _cache.Set($"run-grid:{runId}", cells, TimeSpan.FromHours(12));
        return cells;
    }

    private async Task<(decimal? Gp, int Skus)> ExpectedGpAsync(PublishBatch batch, Layer layer, CancellationToken ct)
    {
        if (_cache.TryGetValue($"run-expected:{batch.Id}", out (decimal?, int) cached)) return cached;
        var result = await _facts.ExpectedGpAsync(batch.Id, batch.EngineRunId!.Value, layer.Id, layer.VatRatePct, ct);
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
