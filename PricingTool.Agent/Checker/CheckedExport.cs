using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Storage;
using PricingTool.Agent.Tools;
using PricingTool.Data;
using PricingTool.Data.Entities;

namespace PricingTool.Agent.Checker;

public sealed record ExportResult(bool Exported, string Reason, string? FilePath, int Rows, int LeftOut, long? CheckId)
{
    public long? ExportBatchId { get; init; }
    public int LeftOutHoldout { get; init; }
}

/// <summary>Which proposals of a run belong to the holdout categories (the control group is never priced by v2).</summary>
public interface IHoldoutLookup
{
    Task<IReadOnlySet<long>> HoldoutProposalIdsAsync(long runId, int layerId, IReadOnlyCollection<string> categories, CancellationToken ct = default);
}

/// <summary>Through the product map (hk.Products): GjirafaMall by main group, Gjirafa50 by L1 category.</summary>
public sealed class HoldoutLookup : IHoldoutLookup
{
    private readonly AgentDataSource _source;
    public HoldoutLookup(AgentDataSource source) => _source = source;

    public async Task<IReadOnlySet<long>> HoldoutProposalIdsAsync(long runId, int layerId, IReadOnlyCollection<string> categories, CancellationToken ct = default)
    {
        var ids = new HashSet<long>();
        if (categories.Count == 0) return ids;
        await using var conn = new SqlConnection(_source.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            DECLARE @Mall bit = (SELECT CASE WHEN Brand = 'GjirafaMall' THEN 1 ELSE 0 END FROM PricingTool.Layers WHERE Id = @L);
            SELECT pp.Id FROM PricingTool.ProposedPrices pp
            JOIN hk.Products p ON p.LayerId = pp.LayerId AND p.ProductId = pp.ProductId
            WHERE pp.PricingRunId = @Run AND pp.HasChange = 1
              AND (CASE WHEN @Mall = 1 THEN ISNULL(p.MainGroup, N'Others') ELSE ISNULL(p.CategoryL1, N'Unknown') END)
                  IN (SELECT value FROM STRING_SPLIT(@Cats, '|'))
            """, conn) { CommandTimeout = 300 };
        cmd.Parameters.AddWithValue("@L", layerId);
        cmd.Parameters.AddWithValue("@Run", runId);
        cmd.Parameters.AddWithValue("@Cats", string.Join("|", categories));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) ids.Add(r.GetInt64(0));
        return ids;
    }
}

/// <summary>
/// The export of a run's prices for the team's push, gated by its latest run check: no check or a block →
/// nothing is written; warn → every changed price except the warned and blocked rows (a fixed row goes out at
/// the checker's fixed price); pass → every changed price. The holdout categories are always left out: they are
/// the control group the money ledger measures against. Once the team has pushed a file,
/// <see cref="MarkPushedAsync"/> records it as applied (the tool's Approval), which starts its measurement.
/// </summary>
public sealed class CheckedExport
{
    private readonly PricingToolDbContext _tool;
    private readonly AgentDbContext _db;
    private readonly IHoldoutLookup _holdouts;
    private readonly AgentOptions _options;
    private readonly TimeProvider _clock;

    public CheckedExport(PricingToolDbContext tool, AgentDbContext db, IHoldoutLookup holdouts, IOptions<AgentOptions> options,
        TimeProvider? clock = null)
    {
        _tool = tool;
        _db = db;
        _holdouts = holdouts;
        _options = options.Value;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<ExportResult> ExportAsync(long runId, string directory, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        // A test check (an injected-test drill) never gates or shapes a real export.
        var check = await _db.AgentRunChecks.AsNoTracking().Where(c => c.RunId == runId && c.Status != "Running" && !c.IsTest)
            .OrderByDescending(c => c.Id).FirstOrDefaultAsync(ct);
        if (check is null)
            return new ExportResult(false, $"Run {runId} has not been checked: nothing exported.", null, 0, 0, null);

        var rows = await _db.AgentRunCheckRows.AsNoTracking().Where(r => r.CheckId == check.Id).ToListAsync(ct);
        if (check.Verdict == Severity.Block)
        {
            var examples = rows.Where(r => r.FinalSeverity == Severity.Block).Take(3).Select(r => $"{r.Sku} ({r.Flags})");
            var why = check.Status != "Succeeded" ? check.Error : $"{check.Blocks} blocking row(s), e.g. {string.Join(", ", examples)}";
            return new ExportResult(false, $"Run {runId} is blocked by check #{check.Id}: {why}. Nothing exported.", null, 0, 0, check.Id);
        }

        var run = await _tool.PricingRuns.AsNoTracking().SingleAsync(r => r.Id == runId, ct);
        var layer = await _tool.Layers.AsNoTracking().SingleAsync(l => l.Id == run.LayerId, ct);
        var held = rows.Where(r => r.FinalSeverity is Severity.Warn or Severity.Block).Select(r => r.ProposalId).ToHashSet();
        var fixes = rows.Where(r => r.FinalSeverity == Severity.Fixed && r.FixPrice is not null).ToDictionary(r => r.ProposalId, r => r.FixPrice!.Value);
        var holdoutCategories = _options.Limits.Holdouts.TryGetValue(run.LayerId.ToString(CultureInfo.InvariantCulture), out var h) ? h : new List<string>();
        var holdout = await _holdouts.HoldoutProposalIdsAsync(runId, run.LayerId, holdoutCategories, ct);
        var proposals = await _tool.ProposedPrices.AsNoTracking().Where(p => p.PricingRunId == runId && p.HasChange)
            .Select(p => new { p.Id, p.Sku, p.OldPrice, p.CurrentPrice, p.ProposedPriceValue }).ToListAsync(ct);
        var exported = proposals.Where(p => !held.Contains(p.Id) && !holdout.Contains(p.Id))
            .Select(p => (p.Id, p.Sku, p.OldPrice, p.CurrentPrice, Price: fixes.TryGetValue(p.Id, out var f) ? f : p.ProposedPriceValue, Fixed: fixes.ContainsKey(p.Id)))
            .ToList();

        // Expected gross profit, estimated: each price change on the SKU's last 30 days of units, VAT excluded, no
        // elasticity. Shown next to what is measured; never counted in a total.
        var date = run.StartedUtc.Date;
        var skus = exported.Select(e => e.Sku).ToList();
        var units = await _tool.DailySnapshots.AsNoTracking().Where(s => s.LayerId == run.LayerId && s.SnapshotDate == date && skus.Contains(s.Sku))
            .Select(s => new { s.Sku, s.Qty30 }).ToListAsync(ct);
        var units30 = units.GroupBy(u => u.Sku).ToDictionary(g => g.Key, g => g.First().Qty30);
        var vat = 1m + layer.VatRatePct / 100m;
        var expected = exported.Sum(e => (e.Price - e.CurrentPrice) / vat * units30.GetValueOrDefault(e.Sku));

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"run-{runId}-L{run.LayerId}-checked-{_clock.GetUtcNow():yyyyMMdd-HHmmss}.csv");
        var sb = new StringBuilder("Sku,OldPrice,CurrentPrice,NewPrice,RunId,ProposalId,CheckId,Note\n");
        foreach (var e in exported)
            sb.Append(string.Join(",", Csv(e.Sku), N(e.OldPrice), N(e.CurrentPrice), N(e.Price),
                runId.ToString(CultureInfo.InvariantCulture), e.Id.ToString(CultureInfo.InvariantCulture), check.Id.ToString(CultureInfo.InvariantCulture),
                e.Fixed ? "fixed by the run checker" : "")).Append('\n');
        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, ct);

        var batch = new ExportBatch
        {
            LayerId = run.LayerId, RunId = runId, CheckId = check.Id, FilePath = path, Rows = exported.Count,
            LeftOutByCheck = proposals.Count(p => held.Contains(p.Id)), LeftOutHoldout = proposals.Count(p => holdout.Contains(p.Id) && !held.Contains(p.Id)),
            CreatedUtc = _clock.GetUtcNow().UtcDateTime, ExpectedGp30Eur = Math.Round(expected, 2),
        };
        _db.ExportBatches.Add(batch);
        await _db.SaveChangesAsync(ct);

        var type = await _db.TimeSheetTaskTypes.SingleAsync(t => t.Id == AgentDbContext.TaskTypes.CreateAndPushDiscounts, ct);
        _db.TimeSheetEntries.Add(new TimeSheetEntry
        {
            Date = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime), UserEmail = _options.TimeSheetOwnerEmail, TaskTypeId = type.Id,
            What = $"Engine run #{runId} (layer {run.LayerId}): push file of {exported.Count:N0} checked prices, {batch.LeftOutByCheck:N0} held by check #{check.Id}, " +
                   $"{batch.LeftOutHoldout:N0} holdout left out",
            MinutesBefore = type.MinutesBefore, MinutesAfter = Math.Round((decimal)sw.Elapsed.TotalMinutes, 2),
            Proof = $"ai.ExportBatch #{batch.Id}",
            Comment = "Written by the export; minutes after = the export's wall clock. The team's push of the file is manual and not included.",
        });
        await _db.SaveChangesAsync(ct);

        return new ExportResult(true,
            $"Check #{check.Id} verdict {check.Verdict}: {exported.Count} prices exported, {batch.LeftOutByCheck} held by the check, {batch.LeftOutHoldout} holdout left out.",
            path, exported.Count, proposals.Count - exported.Count, check.Id) { ExportBatchId = batch.Id, LeftOutHoldout = batch.LeftOutHoldout };
    }

    /// <summary>
    /// The team has pushed this export: record it as applied the way the tool does (an Approval with its items,
    /// read from the exported file), so its before/after measurement starts. Idempotent per export.
    /// </summary>
    public async Task<long> MarkPushedAsync(long exportBatchId, string pushedBy, CancellationToken ct = default)
    {
        var batch = await _db.ExportBatches.SingleAsync(b => b.Id == exportBatchId, ct);
        if (batch.ApprovalId is { } existing) return existing;

        var items = (await File.ReadAllLinesAsync(batch.FilePath, ct)).Skip(1).Where(l => l.Length > 0).Select(ParseLine).ToList();
        var now = _clock.GetUtcNow().UtcDateTime;
        var approval = new Approval
        {
            LayerId = batch.LayerId, ApprovedUtc = now, SourceRunId = batch.RunId, ApprovedBy = pushedBy, SkuCount = items.Count,
            IncreaseCount = items.Count(i => i.New > i.Current), DecreaseCount = items.Count(i => i.New < i.Current), CreatedUtc = now,
            Items = items.Select(i => new ApprovalItem { Sku = i.Sku, OldPrice = i.Current, NewPrice = i.New }).ToList(),
        };
        _tool.Approvals.Add(approval);
        _tool.AuditLog.Add(new AuditLogEntry
        {
            LayerId = batch.LayerId, TimestampUtc = now, UserName = pushedBy, Category = AgentModeStore.AuditCategory,
            Action = $"Export #{batch.Id} marked as pushed ({items.Count} prices)", EntityType = nameof(Approval), EntityId = $"run {batch.RunId}",
        });
        await _tool.SaveChangesAsync(ct);

        batch.PushedUtc = now;
        batch.PushedBy = pushedBy;
        batch.ApprovalId = approval.Id;
        await _db.SaveChangesAsync(CancellationToken.None);
        return approval.Id;
    }

    private static (string Sku, decimal Current, decimal New) ParseLine(string line)
    {
        // Sku may be quoted (it never contains a newline); the numbers are the next three fields.
        string sku;
        string rest;
        if (line.StartsWith('"'))
        {
            var end = line.IndexOf("\",", 1, StringComparison.Ordinal);
            sku = line[1..end].Replace("\"\"", "\"");
            rest = line[(end + 2)..];
        }
        else
        {
            var comma = line.IndexOf(',');
            sku = line[..comma];
            rest = line[(comma + 1)..];
        }
        var f = rest.Split(',');
        return (sku, decimal.Parse(f[1], CultureInfo.InvariantCulture), decimal.Parse(f[2], CultureInfo.InvariantCulture));
    }

    private static string N(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Csv(string v) => v.Contains(',') || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
}
