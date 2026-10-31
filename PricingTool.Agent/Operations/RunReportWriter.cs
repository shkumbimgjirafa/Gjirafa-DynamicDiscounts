using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.ClaudeCode;
using PricingTool.Agent.Storage;
using PricingTool.Agent.Tools;
using PricingTool.Data;
using PricingTool.Data.Entities;

namespace PricingTool.Agent.Operations;

/// <summary>One category × band cell of a run: priced SKUs, and how many the run changed (up, down).</summary>
public sealed record RunGridCell(string Category, int BandId, int Skus, int Changed, int Up, int Down);

/// <summary>The counts that need the run's proposals and the category map (a seam for tests).</summary>
public interface IRunFacts
{
    /// <summary>The run's priced SKUs by main category and band; null when its proposals no longer exist.</summary>
    Task<IReadOnlyList<RunGridCell>?> GridAsync(long runId, CancellationToken ct = default);

    /// <summary>Estimated: what the run's prices on the shelf earn a month against the shop's own prices (see <see cref="ShelfEffect"/>);
    /// null when the run's proposals are gone. <paramref name="holdouts"/> are the layer's holdout categories (never published).</summary>
    Task<ShelfEffect?> ShelfEffectAsync(long batchId, long runId, int layerId, decimal vatRatePct, IReadOnlyCollection<string> holdouts, CancellationToken ct = default);
}

/// <summary>The counts from the tool database: categories through the product map (<c>hk.ProductForProposal</c>, GjirafaMall's
/// main group, Gjirafa50's L1 category), the same way the engine and the run checker find a SKU's category.</summary>
public sealed class SqlRunFacts : IRunFacts
{
    private readonly AgentDataSource _source;
    public SqlRunFacts(AgentDataSource source) => _source = source;

    public async Task<IReadOnlyList<RunGridCell>?> GridAsync(long runId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_source.ConnectionString);
        await conn.OpenAsync(ct);
        await using (var any = new SqlCommand("SELECT CASE WHEN EXISTS (SELECT 1 FROM PricingTool.ProposedPrices WHERE PricingRunId = @Run) THEN 1 ELSE 0 END", conn))
        {
            any.Parameters.AddWithValue("@Run", runId);
            if ((int)(await any.ExecuteScalarAsync(ct))! == 0) return null;
        }
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
        var cells = new List<RunGridCell>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            cells.Add(new RunGridCell(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5)));
        return cells;
    }

    public async Task<ShelfEffect?> ShelfEffectAsync(long batchId, long runId, int layerId, decimal vatRatePct, IReadOnlyCollection<string> holdouts, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_source.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            DECLARE @Date date = (SELECT CAST(StartedUtc AS date) FROM PricingTool.PricingRuns WHERE Id = @Run);
            DECLARE @Mall bit = (SELECT CASE WHEN Brand = 'GjirafaMall' THEN 1 ELSE 0 END FROM PricingTool.Layers WHERE Id = @L);
            DECLARE @Check bigint = (SELECT MAX(Id) FROM ai.AgentRunCheck WHERE RunId = @Run AND IsTest = 0 AND Status <> 'Running');
            DECLARE @HoldTrim bit = (SELECT CASE WHEN HeldBackTrimCuts IS NULL THEN 0 ELSE 1 END FROM ai.PublishBatch WHERE Id = @Batch);
            WITH ch AS (
                SELECT pp.CurrentPrice AS Cur, pp.Pptcv AS Cost, ISNULL(s.Qty30, 0) AS U, pp.ReasonCodes, pp.ManualOverrideBy,
                       COALESCE(CASE WHEN f.FinalSeverity = 'fixed' THEN f.FixPrice END, pp.ProposedPriceValue) AS NewP
                FROM PricingTool.ProposedPrices pp
                OUTER APPLY (SELECT TOP 1 p.MainGroup, p.CategoryL1 FROM hk.ProductForProposal(pp.LayerId, pp.ProductId, pp.Sku) p) c
                OUTER APPLY (SELECT TOP 1 d.Qty30 FROM PricingTool.DailySnapshots d WHERE d.LayerId = @L AND d.SnapshotDate = @Date AND d.Sku = pp.Sku) s
                OUTER APPLY (SELECT TOP 1 r.FinalSeverity, r.FixPrice FROM ai.AgentRunCheckRow r WHERE r.CheckId = @Check AND r.ProposalId = pp.Id) f
                WHERE pp.PricingRunId = @Run AND pp.HasChange = 1 AND pp.CurrentPrice > 0
                  AND ISNULL(f.FinalSeverity, '') NOT IN ('warn', 'block')
                  AND (CASE WHEN @Mall = 1 THEN ISNULL(c.MainGroup, N'Others') ELSE ISNULL(c.CategoryL1, N'Unknown') END)
                      NOT IN (SELECT value FROM STRING_SPLIT(@Holdouts, '|'))
                  AND NOT EXISTS (SELECT 1 FROM ai.PublishException e WHERE e.BatchId = @Batch AND e.Sku = pp.Sku AND e.Kind <> 'app-deal-cheaper')),
            g AS (
                SELECT NewP, Cur, U,
                       (NewP - Cur) / @Vat * U AS Gp0,
                       CASE WHEN Cost IS NULL THEN (NewP - Cur) / @Vat * U
                            ELSE U * POWER(CAST(NewP / Cur AS float), -1.0) * (NewP - Cost) / @Vat - U * (Cur - Cost) / @Vat END AS Gp1,
                       CASE WHEN Cost IS NULL THEN (NewP - Cur) / @Vat * U
                            ELSE U * POWER(CAST(NewP / Cur AS float), -2.0) * (NewP - Cost) / @Vat - U * (Cur - Cost) / @Vat END AS Gp2
                FROM ch
                -- A margin-trim cut the publish held back is not on the shelf (AgentOptions.HoldBackTrimCuts).
                WHERE NOT (@HoldTrim = 1 AND NewP < Cur AND ManualOverrideBy IS NULL
                           AND ',' + REPLACE(ISNULL(ReasonCodes, ''), ' ', '') + ',' LIKE '%,MARGIN_TARGET_TRIM,%'))
            SELECT COUNT(*), SUM(CASE WHEN U > 0 THEN 1 ELSE 0 END), ISNULL(SUM(U), 0),
                   SUM(CASE WHEN NewP > Cur THEN 1 ELSE 0 END), ISNULL(SUM(CASE WHEN NewP > Cur THEN Gp0 END), 0),
                   SUM(CASE WHEN NewP < Cur THEN 1 ELSE 0 END), ISNULL(SUM(CASE WHEN NewP < Cur THEN Gp0 END), 0),
                   ISNULL(SUM(Gp0), 0), ISNULL(SUM(Gp1), 0), ISNULL(SUM(Gp2), 0)
            FROM g
            """, conn) { CommandTimeout = 300 };
        cmd.Parameters.AddWithValue("@Run", runId);
        cmd.Parameters.AddWithValue("@Batch", batchId);
        cmd.Parameters.AddWithValue("@L", layerId);
        cmd.Parameters.AddWithValue("@Vat", 1m + vatRatePct / 100m);
        cmd.Parameters.AddWithValue("@Holdouts", string.Join('|', holdouts));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        var n = r.GetInt32(0);
        if (n == 0) return null;
        static decimal D(SqlDataReader r, int i) => Math.Round(Convert.ToDecimal(r.GetValue(i)), 0);
        return new ShelfEffect(n, r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), D(r, 4), r.GetInt32(5), D(r, 6), D(r, 7), D(r, 8), D(r, 9));
    }
}

/// <summary>
/// What a run's prices on the shelf are expected to earn a month (estimated), against the price the shop charged when the run priced
/// it: every price the publish was given (changed, not held back by the run checker, a fixed row at its fix price, not a holdout
/// category) that it didn't leave out, each change × the SKU's last 30 days' units, VAT excluded. <see cref="GrossProfit"/> keeps the
/// units as they were; <see cref="IfElasticityMinus1"/> and <see cref="IfElasticityMinus2"/> let them move with the price (a 1 % cut
/// sells 1 % or 2 % more, a raise the reverse), on the SKU's current cost.
/// </summary>
public sealed record ShelfEffect(int Skus, int SoldSkus, int Units30, int Raises, decimal FromRaises, int Cuts, decimal FromCuts,
    decimal GrossProfit, decimal IfElasticityMinus1, decimal IfElasticityMinus2);

/// <summary>Alpha's account of a run, as the schema asks for it.</summary>
public sealed record RunReportAnswer(string Headline, string Report, IReadOnlyList<string>? Watch);

/// <summary>
/// Writes a published run's report (<c>ai.RunReport</c> and its category × band cells) right after the publish, from the
/// publish records, the run's captured bands and Alpha's rules, its proposals and the checker's verdict. Then Alpha
/// writes its account of the run from those numbers: one model call per published run, no data tools; code computes
/// every number and the model only writes about them. A failed or skipped call leaves the numbers in place.
/// </summary>
public sealed class RunReportWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly AgentDbContext _db;
    private readonly PricingToolDbContext _tool;
    private readonly IRunFacts _facts;
    private readonly ClaudeCodeClient _client;
    private readonly AgentOptions _options;
    private readonly ILogger<RunReportWriter> _logger;
    private readonly TimeProvider _clock;

    public RunReportWriter(AgentDbContext db, PricingToolDbContext tool, IRunFacts facts, ClaudeCodeClient client, IOptions<AgentOptions> options,
        ILogger<RunReportWriter> logger, TimeProvider? clock = null)
    {
        _db = db;
        _tool = tool;
        _facts = facts;
        _client = client;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>A layer's regular publishes of a run that went through (published, not rolled back), newest first: the first is
    /// the current run. The cost watcher's one-SKU publishes, expiries and campaigns have no run and are left out.</summary>
    public static IQueryable<PublishBatch> Published(IQueryable<PublishBatch> batches, int layerId) => batches
        .Where(b => b.LayerId == layerId && b.Kind == "Regular" && b.EngineRunId != null && b.Status == "Published" && b.RolledBackUtc == null)
        .OrderByDescending(b => b.Id);

    /// <summary>The batch's report, written now if it has none (and Alpha's account asked for when <paramref name="withAlpha"/>
    /// and it has none yet). Null when the batch isn't a regular publish of a run that went through.</summary>
    public async Task<RunReportRecord?> WriteAsync(long batchId, bool withAlpha = true, CancellationToken ct = default)
    {
        var report = await _db.RunReports.SingleOrDefaultAsync(r => r.BatchId == batchId, ct);
        if (report is null)
        {
            var batch = await _db.PublishBatches.AsNoTracking().SingleOrDefaultAsync(b => b.Id == batchId, ct);
            if (batch is null || batch.Kind != "Regular" || batch.EngineRunId is null || batch.Status != "Published" || batch.RolledBackUtc is not null)
                return null;
            report = await NumbersAsync(batch, ct);
        }
        if (withAlpha && report.AlphaText is null) await AlphaAsync(report, ct);
        return report;
    }

    /// <summary>Reports for the cycle layers' publishes of the last <paramref name="days"/> days that have none, oldest first;
    /// Alpha writes only about each layer's newest (the current run), so a backlog costs one call per layer.</summary>
    public async Task<IReadOnlyList<RunReportRecord>> WriteMissingAsync(int days = 3, CancellationToken ct = default)
    {
        var since = _clock.GetUtcNow().UtcDateTime.AddDays(-days);
        var written = new List<RunReportRecord>();
        foreach (var layer in _options.Cycle.Layers)
        {
            var batches = await Published(_db.PublishBatches.AsNoTracking(), layer).Where(b => b.StartedUtc >= since).ToListAsync(ct);
            batches.Reverse();                                  // oldest first: each report's comparison exists when it is written
            for (var i = 0; i < batches.Count; i++)
            {
                var newest = i == batches.Count - 1;
                if (await _db.RunReports.AnyAsync(r => r.BatchId == batches[i].Id && (!newest || r.AlphaText != null), ct)) continue;
                if (await WriteAsync(batches[i].Id, withAlpha: newest, ct) is { } r) written.Add(r);
            }
        }
        return written;
    }

    private async Task<RunReportRecord> NumbersAsync(PublishBatch batch, CancellationToken ct)
    {
        var runId = batch.EngineRunId!.Value;
        var layer = await _tool.Layers.AsNoTracking().SingleAsync(l => l.Id == batch.LayerId, ct);
        var cells = await _facts.GridAsync(runId, ct) ?? Array.Empty<RunGridCell>();
        var bands = await _tool.PricingRunBandTargets.AsNoTracking().Where(b => b.PricingRunId == runId).ToListAsync(ct);
        var rules = await _tool.PricingRunCategoryRules.AsNoTracking().Where(r => r.PricingRunId == runId).ToListAsync(ct);
        var check = await _db.AgentRunChecks.AsNoTracking().Where(c => c.RunId == runId && !c.IsTest).OrderByDescending(c => c.Id).FirstOrDefaultAsync(ct);
        var leftOut = (await _db.PublishExceptions.AsNoTracking().Where(e => e.BatchId == batch.Id)
                .GroupBy(e => e.Kind).Select(g => new { g.Key, Skus = g.Select(e => e.Sku).Distinct().Count() }).ToListAsync(ct))
            .OrderByDescending(x => x.Skus).ToDictionary(x => x.Key, x => x.Skus);
        var holdouts = _options.Limits.Holdouts.TryGetValue(batch.LayerId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var hl) ? hl : new List<string>();
        var effect = await _facts.ShelfEffectAsync(batch.Id, runId, batch.LayerId, layer.VatRatePct, holdouts, ct);
        var previous = await Published(_db.PublishBatches.AsNoTracking(), batch.LayerId).Where(b => b.Id < batch.Id && b.EngineRunId != runId)
            .Select(b => (long?)b.Id).FirstOrDefaultAsync(ct);
        var pass = await _db.AgentPasses.AsNoTracking()
            .Where(p => p.LayerId == batch.LayerId && p.Source == FloorTargetAgent.Source && !p.IsTest && p.Status == "Succeeded" && p.StartedUtc <= batch.StartedUtc)
            .OrderByDescending(p => p.Id).Select(p => (long?)p.Id).FirstOrDefaultAsync(ct);
        bool Holdout(string category) => _options.Limits.IsHoldout(batch.LayerId, category);

        var report = new RunReportRecord
        {
            LayerId = batch.LayerId, RunId = runId, BatchId = batch.Id, PreviousBatchId = previous,
            RunWeekId = await _db.RunWeeks.AsNoTracking().Where(w => w.PublishBatchId == batch.Id).Select(w => (long?)w.Id).FirstOrDefaultAsync(ct),
            PublishedUtc = batch.FinishedUtc ?? batch.StartedUtc, CreatedUtc = _clock.GetUtcNow().UtcDateTime,
            PricedSkus = cells.Sum(c => c.Skus), Changed = cells.Sum(c => c.Changed), Raised = cells.Sum(c => c.Up), Lowered = cells.Sum(c => c.Down),
            HoldoutChanged = cells.Where(c => Holdout(c.Category)).Sum(c => c.Changed),
            HeldByCheck = check?.Warns ?? 0,
            Requested = batch.Requested,
            OnShelf = batch.OnShelf,
            LeftOut = batch.Skipped,
            LeftOutByKind = leftOut.Count == 0 ? null : Truncate(JsonSerializer.Serialize(leftOut, Json), 2000),
            ExpectedGp30Eur = effect?.GrossProfit, ExpectedSkus = effect?.Skus ?? 0, ExpectedSoldSkus = effect?.SoldSkus ?? 0,
            ExpectedRaises = effect?.Raises ?? 0, ExpectedFromRaisesEur = effect?.FromRaises, ExpectedCuts = effect?.Cuts ?? 0, ExpectedFromCutsEur = effect?.FromCuts,
            ExpectedE1Eur = effect?.IfElasticityMinus1, ExpectedE2Eur = effect?.IfElasticityMinus2,
            AlphaPassId = pass, AlphaStatus = "not asked",
        };
        _db.RunReports.Add(report);
        await _db.SaveChangesAsync(ct);

        foreach (var c in cells)
        {
            var rule = rules.FirstOrDefault(r => r.BandId == c.BandId && string.Equals(r.Category, c.Category, StringComparison.OrdinalIgnoreCase));
            var band = bands.FirstOrDefault(b => b.BandId == c.BandId);
            _db.RunReportCells.Add(new RunReportCellRecord
            {
                ReportId = report.Id, Category = Truncate(c.Category, 200)!, BandId = c.BandId,
                FloorPct = rule?.MarginFloorPct ?? band?.MarginFloorPct, TargetPct = rule is not null ? rule.TargetMarginPct : band?.TargetMarginPct,
                ByAlpha = rule is not null, Holdout = Holdout(c.Category),
                Skus = c.Skus, Changed = c.Changed, Up = c.Up, Down = c.Down,
            });
        }
        await _db.SaveChangesAsync(ct);
        return report;
    }

    /// <summary>Alpha writes its account of the run from the report's numbers, its own pass and this week's decisions.</summary>
    private async Task AlphaAsync(RunReportRecord report, CancellationToken ct)
    {
        var prompt = await PromptAsync(report, ct);
        var call = await _client.CallAsync(new AgentCallRequest(AgentRole.Alpha, Purpose(report.LayerId, report.BatchId), prompt,
            Path.Combine(FloorTargetAgent.PromptsDirectory, "run-report-rules.md"))
        {
            JsonSchema = await File.ReadAllTextAsync(Path.Combine(FloorTargetAgent.PromptsDirectory, "run-report.schema.json"), ct),
            TimeoutSeconds = 300,
            MaxBudgetUsd = 1m,
        }, ct);
        report.AiCallLogId = call.CallLogIds.Count > 0 ? call.CallLogIds[^1] : null;
        var answer = call.Succeeded ? call.Deserialize<RunReportAnswer>(Json) : null;
        if (answer is null || string.IsNullOrWhiteSpace(answer.Headline) || string.IsNullOrWhiteSpace(answer.Report))
        {
            report.AlphaStatus = Truncate(call.Status == AgentCallStatus.Disabled
                ? $"skipped: {call.Error ?? "the kill switch is pulled"}"
                : $"failed: {(call.Succeeded ? "the answer had no headline or report" : $"{call.Status}: {call.Error}")}", 500)!;
            _logger.LogWarning("Run report for batch {Batch}: Alpha's account not written ({Status}).", report.BatchId, report.AlphaStatus);
        }
        else
        {
            report.Headline = Truncate(answer.Headline.Trim(), 300);
            report.AlphaText = Truncate(answer.Report.Trim(), 4000);
            report.AlphaWatch = answer.Watch is { Count: > 0 } w ? Truncate(JsonSerializer.Serialize(w.Where(x => !string.IsNullOrWhiteSpace(x)).Take(5), Json), 2000) : null;
            report.AlphaStatus = "written";
        }
        await _db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>The model call's purpose in <c>ai.AiCallLog</c> (Run week counts its own calls by purpose).</summary>
    public static string Purpose(int layerId, long batchId) => $"run-report L{layerId} batch {batchId}";

    private async Task<string> PromptAsync(RunReportRecord report, CancellationToken ct)
    {
        var layer = await _tool.Layers.AsNoTracking().SingleAsync(l => l.Id == report.LayerId, ct);
        var bands = await _tool.PricingRunBandTargets.AsNoTracking().Where(b => b.PricingRunId == report.RunId).OrderBy(b => b.MinPrice).ToListAsync(ct);
        var cells = await _db.RunReportCells.AsNoTracking().Where(c => c.ReportId == report.Id).ToListAsync(ct);
        var previous = report.PreviousBatchId is { } pb ? await _db.RunReports.AsNoTracking().SingleOrDefaultAsync(r => r.BatchId == pb, ct) : null;
        var pass = report.AlphaPassId is { } pid ? await _db.AgentPasses.AsNoTracking().SingleOrDefaultAsync(p => p.Id == pid, ct) : null;
        var since = report.PublishedUtc.AddDays(-7);
        var decisions = await _db.AgentDecisions.AsNoTracking()
            .Where(d => d.LayerId == report.LayerId && d.Source == FloorTargetAgent.Source && d.Action != "keep" && d.CreatedUtc >= since && d.CreatedUtc <= report.PublishedUtc
                        && _db.AgentPasses.Any(p => p.Id == d.PassId && !p.IsTest))
            .OrderByDescending(d => d.Id).Take(20).ToListAsync(ct);
        var bandName = bands.ToDictionary(b => b.BandId, b => b.Name);
        string Band(int id) => bandName.TryGetValue(id, out var n) ? n : $"band {id}";

        var facts = new
        {
            layer = layer.DisplayName,
            run = report.RunId,
            publishBatch = report.BatchId,
            published = report.PublishedUtc.ToString("yyyy-MM-dd HH:mm") + " UTC",
            publishedTo = "the shop sandbox (during the hackathon; the shop's real prices did not change)",
            pricedSkus = report.PricedSkus,
            changed = report.Changed,
            raised = report.Raised,
            lowered = report.Lowered,
            changedInHoldoutCategories = report.HoldoutChanged,
            holdoutCategories = _options.Limits.Holdouts.TryGetValue(report.LayerId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var h) ? h : new List<string>(),
            heldBackByTheRunChecker = report.HeldByCheck,
            marginTrimCutsHeldBack = await _db.PublishBatches.AsNoTracking().Where(b => b.Id == report.BatchId).Select(b => b.HeldBackTrimCuts).SingleAsync(ct),
            marginTrimCutsPolicy = "the pricing manager's decision (31 Oct): a cut the margin target trim made is not published and our earlier discount on it is ended, " +
                                   "because cheaper team deals block most raises while every cut lands; raises, dead-stock markdowns and prices a person set still go",
            publishGiven = report.Requested,
            onTheShelfAtOurPrice = report.OnShelf,
            leftOutByReason = report.LeftOutByKind is { } j ? JsonSerializer.Deserialize<Dictionary<string, int>>(j) : new Dictionary<string, int>(),
            leftOutReasonsMean = new Dictionary<string, string>
            {
                ["cheaper-deal"] = "a cheaper deal the team created wins, so our price isn't the one shoppers pay",
                ["app-deal-cheaper"] = "a cheaper app-only deal wins in the app; ours is for the web",
                ["own-push"] = "an earlier push of the calculator's prices holds it",
                ["raised-under-team-deal"] = "the regular price was raised, but a cheaper team deal still wins",
                ["changed-since-run"] = "the price changed after the run priced it, so it was left for the next run",
            },
            onTheShelfAgainstTheShopsPrices = new
            {
                pricesThatDifferFromTheShop = report.ExpectedSkus,
                ofThemSoldInTheLast30Days = report.ExpectedSoldSkus,
                raises = new { skus = report.ExpectedRaises, eurPerMonth = report.ExpectedFromRaisesEur },
                cuts = new { skus = report.ExpectedCuts, eurPerMonth = report.ExpectedFromCutsEur },
                expectedGrossProfitPerMonthEur = report.ExpectedGp30Eur,
                ifEachOnePercentCutSellsOnePercentMore = report.ExpectedE1Eur,
                ifEachOnePercentCutSellsTwoPercentMore = report.ExpectedE2Eur,
                basis = "estimated: every price on the shelf at our price against what the shop charged when the run priced it, × the SKU's last 30 days' units, VAT excluded; the first figure keeps the units as they were",
            },
            previousPublish = previous is null ? null : new
            {
                run = previous.RunId, published = previous.PublishedUtc.ToString("yyyy-MM-dd HH:mm") + " UTC",
                previous.Changed, previous.Raised, previous.Lowered, previous.OnShelf, previous.Requested, expectedGrossProfitPerMonthEur = previous.ExpectedGp30Eur,
                raisesOnTheShelf = previous.ExpectedRaises, cutsOnTheShelf = previous.ExpectedCuts,
            },
            bands = bands.Select(b => new { band = b.Name, costFrom = b.MinPrice, costTo = b.MaxPrice, floorPct = b.MarginFloorPct, targetPct = b.TargetMarginPct }),
            categoriesAlphaSet = cells.Where(c => c.ByAlpha).Select(c => new { category = c.Category, band = Band(c.BandId), floorPct = c.FloorPct, targetPct = c.TargetPct, c.Skus, c.Changed }),
            mostChangedCells = cells.OrderByDescending(c => c.Changed).Take(12)
                .Select(c => new { category = c.Category, band = Band(c.BandId), c.Skus, c.Changed, raised = c.Up, lowered = c.Down, holdout = c.Holdout }),
            yourPass = pass is null ? null : new { pass.Id, started = pass.StartedUtc.ToString("yyyy-MM-dd HH:mm") + " UTC", pass.Mode, summary = Truncate(pass.Summary, 1500) },
            yourDecisionsThisWeek = decisions.Select(d => new
            {
                band = Band(d.BandId), d.Category, d.Action, d.Status, floor = $"{d.CurrentFloorPct:0.#} -> {d.ProposedFloorPct:0.#}",
                target = $"{d.CurrentTargetPct:0.#} -> {d.ProposedTargetPct:0.#}", reason = Truncate(d.Reason, 400),
            }),
        };
        return "Write your report on this published run for the pricing manager. Every number you may use is below.\n\n" + JsonSerializer.Serialize(facts, Json);
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
