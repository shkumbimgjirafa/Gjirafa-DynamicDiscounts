using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.ClaudeCode;
using PricingTool.Agent.Storage;
using PricingTool.Core.Domain;
using PricingTool.Data;
using PricingTool.Data.Entities;

namespace PricingTool.Agent.Checker;

public sealed record RunCheckResult(long? CheckId, string Verdict, string Status, string? Error, int Flagged, int Sent, int Blocks, int Warns, int Fixed);

/// <summary>
/// The pre-publish check of an engine run (plan section 5 "RunChecker", prompt 06). Code flags every changed
/// price against the rules (<see cref="RunCheckRules"/>); one Claude Code call (the run checker's model) judges
/// the flagged rows, at most <see cref="AgentOptions.RunCheckerModelCap"/>, highest impact first; code turns
/// that into each row's outcome and the run's verdict:
/// <list type="bullet">
/// <item>the model may confirm a block, escalate a row to a block, or propose a fixed price, which is used only if it
///   passes every rule; it can't lift a rule's block (they are the firm pricing rules and the data-scale checks), so a
///   run with a block stays blocked whatever the model says;</item>
/// <item>rows over the cap keep their rule severity, and the check says how many;</item>
/// <item>any final block → verdict block (export and publish stop); any warn → warn (those rows stay out);
///   else pass. No valid answer, or the kill switch off → block.</item>
/// </list>
/// </summary>
public sealed class RunChecker
{
    private readonly PricingToolDbContext _tool;
    private readonly AgentDbContext _db;
    private readonly ClaudeCodeClient _client;
    private readonly AgentOptions _options;
    private readonly ILogger<RunChecker> _logger;
    private readonly TimeProvider _clock;
    private readonly IKillSwitch _killSwitch;
    private readonly IProposalCategoryLookup? _categories;

    public RunChecker(PricingToolDbContext tool, AgentDbContext db, ClaudeCodeClient client, IOptions<AgentOptions> options,
        ILogger<RunChecker> logger, TimeProvider? clock = null, IKillSwitch? killSwitch = null, IProposalCategoryLookup? categories = null)
    {
        _tool = tool;
        _db = db;
        _client = client;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _killSwitch = killSwitch ?? new ConfigKillSwitch(_options);
        _categories = categories;
    }

    public static string PromptsDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Prompts");

    internal sealed record Flagged(CheckInput Input, IReadOnlyList<RuleHit> Hits, string RuleSeverity)
    {
        public decimal Impact => Math.Abs(Input.Price - Input.CurrentPrice);
    }

    /// <param name="isTest">A check of injected test rows: stored and labelled, never in the time sheet.</param>
    public async Task<RunCheckResult> CheckAsync(long runId, bool isTest = false, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var run = await _tool.PricingRuns.AsNoTracking().SingleAsync(r => r.Id == runId, ct);
        var check = new AgentRunCheck
        {
            RunId = runId, LayerId = run.LayerId, StartedUtc = _clock.GetUtcNow().UtcDateTime, Status = "Running", Verdict = Severity.Block, IsTest = isTest,
        };
        _db.AgentRunChecks.Add(check);
        await _db.SaveChangesAsync(ct);

        var stop = await _killSwitch.GetAsync(ct);
        if (stop.Stopped)
            return await FinishAsync(check, sw, "Skipped", $"Stopped by the kill switch ({stop.Reason}): no check, so the run stays blocked.", Array.Empty<AgentRunCheckRow>());

        var (inputs, notes) = await LoadAsync(run, ct);
        var flagged = inputs.Select(i => { var h = RunCheckRules.Evaluate(i); return new Flagged(i, h, RunCheckRules.Worst(h)); })
            .Where(f => f.Hits.Count > 0)
            .OrderByDescending(f => Severity.Rank(f.RuleSeverity)).ThenByDescending(f => f.Impact).ThenBy(f => f.Input.Sku, StringComparer.Ordinal)
            .ToList();
        var sent = flagged.Take(_options.RunCheckerModelCap).ToList();
        check.RowsChecked = inputs.Count;
        check.RowsFlagged = flagged.Count;
        check.RowsSentToModel = sent.Count;
        if (flagged.Count > sent.Count)
            notes.Add($"{flagged.Count - sent.Count} flagged rows over the cap of {_options.RunCheckerModelCap} were not sent to the model and keep their rule severity.");
        var stock = await StockAsync(run, sent.Select(f => f.Input.Sku).ToList(), ct);

        var rows = flagged.Select(f => NewRow(check, f, stock)).ToList();
        if (flagged.Count == 0)
            return await FinishAsync(check, sw, "Succeeded", null, rows, notes);

        var call = await _client.CallAsync(new AgentCallRequest(AgentRole.RunChecker,
            isTest ? $"[INJECTED-TEST] run-check {runId}" : $"run-check {runId}",
            BuildPrompt(run, inputs.Count, flagged.Count, sent, stock), Path.Combine(PromptsDirectory, "run-checker-rules.md"))
        {
            JsonSchema = await File.ReadAllTextAsync(Path.Combine(PromptsDirectory, "run-check.schema.json"), ct),
            MaxBudgetUsd = _options.MaxBudgetUsdPerCall,
        }, ct);
        check.CallTag = call.CallTag;
        check.AiCallLogId = call.CallLogIds.Count > 0 ? call.CallLogIds[^1] : null;

        var answer = call.Succeeded ? call.Deserialize<CheckAnswer>() : null;
        if (answer is null || answer.RunId != runId)
            return await FinishAsync(check, sw, "Failed",
                (call.Succeeded ? $"The answer was for run {answer?.RunId}, not {runId}." : $"{call.Status}: {call.Error}") + " The run is blocked.", rows, notes);

        check.ModelVerdict = answer.Verdict;
        var bySku = answer.Violations.GroupBy(v => v.Sku.Trim(), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var sentSkus = sent.Select(f => f.Input.Sku).ToHashSet(StringComparer.Ordinal);
        foreach (var (row, f) in rows.Zip(flagged))
        {
            if (!sentSkus.Contains(f.Input.Sku) || !bySku.TryGetValue(f.Input.Sku, out var v)) continue;
            row.ModelSeverity = v.Severity;
            row.ModelReason = Truncate(v.Reason, 1000);
            row.FixPrice = v.FixPrice;
            row.FinalSeverity = Decide(f, v, row);
        }
        foreach (var unknown in bySku.Keys.Where(k => !sentSkus.Contains(k)))
            notes.Add($"The answer named SKU {unknown}, which was not sent; ignored.");
        return await FinishAsync(check, sw, "Succeeded", null, rows, notes);
    }

    /// <summary>Code's decision for a reviewed row. A fixed price counts only if no rule above info fires on it; a rule's
    /// block stays a block; anything the model escalates becomes a block.</summary>
    internal static string Decide(Flagged f, CheckAnswerViolation v, AgentRunCheckRow row)
    {
        if (v.FixPrice is { } fix and > 0)
        {
            var again = RunCheckRules.Evaluate(f.Input, fix);
            if (again.All(h => Severity.Rank(h.Severity) <= Severity.Rank(Severity.Info)))
            {
                row.FixAccepted = true;
                return Severity.Fixed;
            }
            row.Detail = Truncate(row.Detail + $" | Fix {fix.ToString("0.00", CultureInfo.InvariantCulture)} refused: " +
                string.Join("; ", again.Where(h => h.Severity != Severity.Info).Select(h => h.Code)), 2000)!;
        }
        if (v.Severity == Severity.Block) return Severity.Block;
        if (f.RuleSeverity == Severity.Info && v.Severity == Severity.Warn) return Severity.Warn;
        return f.RuleSeverity;
    }

    private static AgentRunCheckRow NewRow(AgentRunCheck check, Flagged f, IReadOnlyDictionary<string, (int Stock, int Qty30)> stock) => new()
    {
        CheckId = check.Id, ProposalId = f.Input.ProposalId, Sku = f.Input.Sku, BandId = f.Input.BandId, BandName = f.Input.BandName,
        NewPrice = f.Input.Price, CurrentPrice = f.Input.CurrentPrice, Cost = f.Input.Cost,
        MarginPct = f.Input.MarginPct is { } m ? Math.Round(m, 4) : null, FloorPct = f.Input.FloorPct, ChangePct = Math.Round(f.Input.ChangePct, 4),
        Stock = stock.TryGetValue(f.Input.Sku, out var s) ? s.Stock : null,
        Flags = string.Join(",", f.Hits.Select(h => h.Code)), RuleSeverity = f.RuleSeverity, FinalSeverity = f.RuleSeverity,
        Detail = Truncate(string.Join(" ", f.Hits.Select(h => h.Detail)), 2000)!,
    };

    private async Task<RunCheckResult> FinishAsync(AgentRunCheck check, Stopwatch sw, string status, string? error,
        IReadOnlyList<AgentRunCheckRow> rows, List<string>? notes = null)
    {
        check.Status = status;
        check.Error = Truncate(error, 2000);
        check.Blocks = rows.Count(r => r.FinalSeverity == Severity.Block);
        check.Warns = rows.Count(r => r.FinalSeverity == Severity.Warn);
        check.Fixed = rows.Count(r => r.FinalSeverity == Severity.Fixed);
        check.Verdict = status != "Succeeded" || check.Blocks > 0 ? Severity.Block : check.Warns > 0 ? Severity.Warn : Severity.Pass;
        check.Notes = notes is { Count: > 0 } ? Truncate(string.Join(" ", notes), 2000) : null;
        check.FinishedUtc = _clock.GetUtcNow().UtcDateTime;
        check.DurationMs = (int)sw.ElapsedMilliseconds;
        _db.AgentRunCheckRows.AddRange(rows);
        await _db.SaveChangesAsync(CancellationToken.None);

        if (!check.IsTest && status == "Succeeded")
        {
            var type = await _db.TimeSheetTaskTypes.SingleAsync(t => t.Id == AgentDbContext.TaskTypes.PrePublishRunCheck);
            _db.TimeSheetEntries.Add(new TimeSheetEntry
            {
                Date = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime), UserEmail = _options.TimeSheetOwnerEmail, TaskTypeId = type.Id,
                What = $"Engine run #{check.RunId} (layer {check.LayerId}): {check.RowsChecked:N0} changed prices checked, {check.RowsFlagged:N0} flagged, " +
                       $"{check.RowsSentToModel} judged by the model; verdict {check.Verdict} ({check.Blocks} block, {check.Warns} warn, {check.Fixed} fixed)",
                MinutesBefore = type.MinutesBefore, MinutesAfter = Math.Round((decimal)sw.Elapsed.TotalMinutes, 2),
                Proof = $"ai.AgentRunCheck #{check.Id}",
                Comment = "Written by the run checker; minutes after = the check's wall clock.",
            });
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        if (check.Verdict == Severity.Block)
            _logger.LogWarning("Run {Run} blocked by check {Check}: {Status} {Error}", check.RunId, check.Id, status, error);
        return new RunCheckResult(check.Id, check.Verdict, status, check.Error, check.RowsFlagged, check.RowsSentToModel, check.Blocks, check.Warns, check.Fixed);
    }

    /// <summary>The run's changed prices with their band, the floor the run used (its band × category's when a
    /// per-category rule applied), the SKU's cost in the layer's previous run and, on GjirafaMall, Gjirafa50's price
    /// for the same SKU.</summary>
    private async Task<(List<CheckInput> Inputs, List<string> Notes)> LoadAsync(PricingRun run, CancellationToken ct)
    {
        var notes = new List<string>();
        var bands = await _tool.PriceBands.AsNoTracking().Where(b => b.LayerId == run.LayerId).ToDictionaryAsync(b => b.Id, ct);
        var runFloors = await _tool.PricingRunBandTargets.AsNoTracking().Where(t => t.PricingRunId == run.Id)
            .ToDictionaryAsync(t => t.BandId, t => t.MarginFloorPct, ct);
        // Per-category floors the run priced against (PricingRunCategoryRules), and the category of each changed
        // price in those bands, found by SKU like the engine does.
        var runRules = await _tool.PricingRunCategoryRules.AsNoTracking().Where(r => r.PricingRunId == run.Id)
            .ToDictionaryAsync(r => (r.BandId, r.Category), r => r.MarginFloorPct, ct);
        IReadOnlyDictionary<long, string> categoryOf = new Dictionary<long, string>();
        if (runRules.Count > 0)
        {
            if (_categories is null)
                notes.Add($"{runRules.Count} per-category floor(s) in this run not resolved (no category lookup): band floors used.");
            else
                categoryOf = await _categories.CategoriesAsync(run.Id, run.LayerId, runRules.Keys.Select(k => k.BandId).Distinct().ToList(), ct);
        }
        var proposals = await _tool.ProposedPrices.AsNoTracking().Where(p => p.PricingRunId == run.Id && p.HasChange)
            .Select(p => new { p.Id, p.Sku, p.PriceBandId, p.ProposedPriceValue, p.CurrentPrice, p.OldPrice, p.AnchorPrice, p.Pptcv, p.ChangePct,
                p.DeadStockStage, p.ReasonCodes, p.GuardrailFlags })
            .ToListAsync(ct);

        var previousRun = await _tool.PricingRuns.AsNoTracking()
            .Where(r => r.LayerId == run.LayerId && r.Id < run.Id && r.Status == RunStatus.Succeeded && _tool.ProposedPrices.Any(p => p.PricingRunId == r.Id))
            .OrderByDescending(r => r.Id).Select(r => (long?)r.Id).FirstOrDefaultAsync(ct);
        var previousCost = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (previousRun is { } prev)
            foreach (var p in await _tool.ProposedPrices.AsNoTracking().Where(p => p.PricingRunId == prev && p.Pptcv != null)
                         .Select(p => new { p.Sku, p.Pptcv }).ToListAsync(ct))
                previousCost.TryAdd(p.Sku, p.Pptcv!.Value);
        else
            notes.Add("cost-moved not checked: no earlier run with proposals on this layer.");

        var gj50 = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var layer = await _tool.Layers.AsNoTracking().SingleAsync(l => l.Id == run.LayerId, ct);
        if (layer.Brand == "GjirafaMall")
        {
            var sibling = await _tool.Layers.AsNoTracking().Where(l => l.Brand == "Gjirafa50" && l.CountryCode == layer.CountryCode)
                .Select(l => (int?)l.Id).FirstOrDefaultAsync(ct);
            var gj50Run = sibling is null ? null : await _tool.PricingRuns.AsNoTracking()
                .Where(r => r.LayerId == sibling && r.Id < run.Id && r.Status == RunStatus.Succeeded && _tool.ProposedPrices.Any(p => p.PricingRunId == r.Id))
                .OrderByDescending(r => r.Id).Select(r => (long?)r.Id).FirstOrDefaultAsync(ct);
            if (gj50Run is { } g)
                foreach (var p in await _tool.ProposedPrices.AsNoTracking().Where(p => p.PricingRunId == g).Select(p => new { p.Sku, p.ProposedPriceValue }).ToListAsync(ct))
                    gj50.TryAdd(p.Sku, p.ProposedPriceValue);
            else
                notes.Add("mall-gj50-disagree not checked: no Gjirafa50 run with proposals before this one.");
        }

        var inputs = proposals.Select(p =>
        {
            var band = p.PriceBandId is { } id && bands.TryGetValue(id, out var b) ? b : null;
            decimal? floor = p.PriceBandId is { } bid && runFloors.TryGetValue(bid, out var rf) ? rf : band?.MarginFloorPct;
            if (p.PriceBandId is { } cb && categoryOf.TryGetValue(p.Id, out var category) && runRules.TryGetValue((cb, category), out var cf))
                floor = cf;
            return new CheckInput(p.Id, p.Sku, p.PriceBandId, band?.Name, (RoundingConvention)(band?.RoundingConvention ?? 0), band?.RoundingEnabled ?? false,
                floor, p.ProposedPriceValue, p.CurrentPrice, p.OldPrice, p.AnchorPrice, p.Pptcv, p.ChangePct, p.DeadStockStage, p.ReasonCodes, p.GuardrailFlags)
            {
                PreviousCost = previousCost.TryGetValue(p.Sku, out var pc) ? pc : null,
                Gj50Price = gj50.TryGetValue(p.Sku, out var gp) ? gp : null,
            };
        }).ToList();
        return (inputs, notes);
    }

    private async Task<Dictionary<string, (int Stock, int Qty30)>> StockAsync(PricingRun run, IReadOnlyList<string> skus, CancellationToken ct)
    {
        var date = run.StartedUtc.Date;
        return (await _tool.DailySnapshots.AsNoTracking()
                .Where(s => s.LayerId == run.LayerId && s.SnapshotDate == date && skus.Contains(s.Sku))
                .Select(s => new { s.Sku, s.LocalWarehouseStock, s.Qty30 }).ToListAsync(ct))
            .GroupBy(s => s.Sku, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (g.First().LocalWarehouseStock, g.First().Qty30), StringComparer.Ordinal);
    }

    private static string BuildPrompt(PricingRun run, int checkedRows, int flaggedRows, IReadOnlyList<Flagged> sent,
        IReadOnlyDictionary<string, (int Stock, int Qty30)> stock)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Pre-publish check of engine run {run.Id} (layer {run.LayerId}), run at {run.StartedUtc:yyyy-MM-dd HH:mm} UTC.");
        sb.AppendLine($"{checkedRows:N0} changed prices; {flaggedRows:N0} flagged by the rules; the {sent.Count} below are the highest-impact flagged rows" +
                      (flaggedRows > sent.Count ? " (the rest keep their rule severity)." : "."));
        sb.AppendLine("Answer with run_id = " + run.Id + " and one violation per row below, identified by its sku exactly as written.");
        sb.AppendLine("Money is in the layer's currency, VAT included; margin = (price - cost) / price. Every number was computed by code.");
        sb.AppendLine();
        sb.AppendLine("sku|band|new_price|current|shelf|anchor|cost|margin_pct|floor_pct|change_pct|stock|units_30d|dead_stage|rule_severity|flags|engine_reasons");
        foreach (var f in sent)
        {
            var i = f.Input;
            stock.TryGetValue(i.Sku, out var s);
            sb.AppendLine(string.Join('|', i.Sku, i.BandName ?? "-", N(i.Price), N(i.CurrentPrice), N(i.OldPrice), N(i.AnchorPrice),
                i.Cost is { } c ? N(c) : "-", i.MarginPct is { } m ? P(m) : "-", i.FloorPct is { } fl ? P(fl) : "-", P(i.ChangePct),
                s.Stock.ToString(CultureInfo.InvariantCulture), s.Qty30.ToString(CultureInfo.InvariantCulture), i.DeadStock.ToString(),
                f.RuleSeverity, string.Join(",", f.Hits.Select(h => h.Code)), Truncate(string.Join(",", new[] { i.ReasonCodes, i.GuardrailFlags }.Where(x => x.Length > 0)), 160)));
        }
        return sb.ToString();
    }

    private static string N(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    private static string P(decimal v) => v.ToString("0.0", CultureInfo.InvariantCulture);
    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    internal sealed record CheckAnswer(
        [property: System.Text.Json.Serialization.JsonPropertyName("run_id")] long RunId,
        string Verdict, List<CheckAnswerViolation> Violations);

    internal sealed record CheckAnswerViolation(
        string Sku, string Band,
        [property: System.Text.Json.Serialization.JsonPropertyName("new_price")] decimal NewPrice,
        [property: System.Text.Json.Serialization.JsonPropertyName("margin_pct")] decimal? MarginPct,
        [property: System.Text.Json.Serialization.JsonPropertyName("floor_pct")] decimal? FloorPct,
        string Severity, string Reason,
        [property: System.Text.Json.Serialization.JsonPropertyName("fix_price")] decimal? FixPrice);
}
