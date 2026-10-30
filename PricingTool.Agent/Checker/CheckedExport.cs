using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PricingTool.Agent.Storage;
using PricingTool.Data;

namespace PricingTool.Agent.Checker;

public sealed record ExportResult(bool Exported, string Reason, string? FilePath, int Rows, int LeftOut, long? CheckId);

/// <summary>
/// The export of a run's prices for the push, gated by its latest run check: no check or a block → nothing
/// is written; warn → every changed price except the warned and blocked rows (a fixed row goes out at the
/// checker's fixed price); pass → every changed price.
/// </summary>
public sealed class CheckedExport
{
    private readonly PricingToolDbContext _tool;
    private readonly AgentDbContext _db;
    private readonly TimeProvider _clock;

    public CheckedExport(PricingToolDbContext tool, AgentDbContext db, TimeProvider? clock = null)
    {
        _tool = tool;
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<ExportResult> ExportAsync(long runId, string directory, CancellationToken ct = default)
    {
        var check = await _db.AgentRunChecks.AsNoTracking().Where(c => c.RunId == runId && c.Status != "Running")
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

        var held = rows.Where(r => r.FinalSeverity is Severity.Warn or Severity.Block).Select(r => r.ProposalId).ToHashSet();
        var fixes = rows.Where(r => r.FinalSeverity == Severity.Fixed && r.FixPrice is not null).ToDictionary(r => r.ProposalId, r => r.FixPrice!.Value);
        var proposals = await _tool.ProposedPrices.AsNoTracking().Where(p => p.PricingRunId == runId && p.HasChange)
            .Select(p => new { p.Id, p.Sku, p.OldPrice, p.CurrentPrice, p.ProposedPriceValue }).ToListAsync(ct);

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"run-{runId}-checked-{_clock.GetUtcNow():yyyyMMdd-HHmmss}.csv");
        var sb = new StringBuilder("Sku,OldPrice,CurrentPrice,NewPrice,RunId,ProposalId,CheckId,Note\n");
        var written = 0;
        foreach (var p in proposals.Where(p => !held.Contains(p.Id)))
        {
            var fixedPrice = fixes.TryGetValue(p.Id, out var f);
            sb.Append(string.Join(",", Csv(p.Sku), N(p.OldPrice), N(p.CurrentPrice), N(fixedPrice ? f : p.ProposedPriceValue),
                runId.ToString(CultureInfo.InvariantCulture), p.Id.ToString(CultureInfo.InvariantCulture), check.Id.ToString(CultureInfo.InvariantCulture),
                fixedPrice ? "fixed by the run checker" : "")).Append('\n');
            written++;
        }
        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, ct);
        return new ExportResult(true, $"Check #{check.Id} verdict {check.Verdict}: {written} prices exported, {proposals.Count - written} held back.",
            path, written, proposals.Count - written, check.Id);
    }

    private static string N(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Csv(string v) => v.Contains(',') || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
}
