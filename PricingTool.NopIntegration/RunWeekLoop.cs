using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Checker;
using PricingTool.Agent.Operations;
using PricingTool.Agent.Storage;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;

namespace PricingTool.NopIntegration;

/// <summary>What one step of Run week did: a <see cref="StepStatus"/>, a line of counts, and the reasons behind it.</summary>
public sealed record RunWeekStepResult(string Status, string Detail, string? Why = null, long? Id = null);

/// <summary>The services Run week drives, one call per step (a seam for tests).</summary>
public interface IRunWeekSteps
{
    /// <summary>Failed when the database is over the cycle's size warning line (an engine run adds up to 0.5 GB).</summary>
    Task<RunWeekStepResult> SizeCheckAsync(CancellationToken ct);
    Task<RunWeekStepResult> AlphaAsync(int layerId, CancellationToken ct);
    Task<RunWeekStepResult> EngineAsync(int layerId, string triggeredBy, CancellationToken ct);
    /// <summary>The check's <see cref="RunWeekStepResult.Status"/> is <see cref="StepStatus.Failed"/> when it blocked the run.</summary>
    Task<RunWeekStepResult> CheckAsync(long runId, CancellationToken ct);
    Task<(IReadOnlyList<PublishItem> Items, string Detail)> CheckedPricesAsync(long runId, CancellationToken ct);
    Task<PublishOutcome> PublishAsync(int layerId, long runId, IReadOnlyList<PublishItem> items, CancellationToken ct);
    /// <summary>The published run's report: its numbers, and Alpha's account of it.</summary>
    Task<RunWeekStepResult> ReportAsync(long batchId, CancellationToken ct);
    /// <summary>The price list of what the publish wrote (<see cref="RunWeekPriceList"/>), as a file in the exports folder.</summary>
    Task<RunWeekStepResult> PriceListAsync(long runWeekId, int layerId, long batchId, IReadOnlyList<PublishItem> onShelf, CancellationToken ct);
}

/// <summary>
/// "Run week" for one layer (<c>ai.RunWeek</c>; plan section 5, Web, "Agent page controls"): the whole pricing loop with no clicks in
/// between. Alpha's pass in the layer's mode (applied within the limits; held items go to the inbox) → an engine run
/// with the per-category rules → the run checker → on pass or warn, the checked prices published to the shop
/// sandbox through <see cref="CheckedPricePublisher"/> (cuts as discounts, raises as the regular price) → the writer's
/// round-trip check. Each step is a
/// <see cref="RunWeekStep"/> row with its counts and reasons. A pulled kill switch stops it before Alpha (and before
/// any later step); a failed step or a check that didn't finish stops the steps after it (blocked rows are held, the rest goes on); the publish refuses unless
/// <c>Publisher:Target</c> is Sandbox. It writes no export file: the daily push file stays the cycle's.
/// </summary>
public sealed class RunWeekLoop
{
    /// <summary><c>PricingRun.TriggeredBy</c> prefix of its engine runs (the daily cycle never takes them for its own).</summary>
    public const string TriggeredBy = "run week";

    private readonly AgentDbContext _db;
    private readonly IRunWeekSteps _steps;
    private readonly IKillSwitch _killSwitch;
    private readonly PublisherOptions _publisher;
    private readonly ILogger<RunWeekLoop> _logger;
    private readonly TimeProvider _clock;

    public RunWeekLoop(AgentDbContext db, IRunWeekSteps steps, IKillSwitch killSwitch, IOptions<PublisherOptions> publisher, ILogger<RunWeekLoop> logger,
        TimeProvider? clock = null)
    {
        _db = db;
        _steps = steps;
        _killSwitch = killSwitch;
        _publisher = publisher.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<long> StartAsync(int layerId, string by, CancellationToken ct = default)
    {
        var week = new RunWeek { LayerId = layerId, StartedBy = by, StartedUtc = _clock.GetUtcNow().UtcDateTime, Status = "Running" };
        _db.RunWeeks.Add(week);
        await _db.SaveChangesAsync(ct);
        return week.Id;
    }

    public async Task<RunWeek> RunAsync(long runWeekId, CancellationToken ct = default)
    {
        var week = await _db.RunWeeks.SingleAsync(w => w.Id == runWeekId, ct);
        try
        {
            week.Outcome = await StepsAsync(week, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Run week #{Id} failed.", week.Id);
            (week.Status, week.Outcome) = ("Failed", Truncate(ex.GetBaseException().Message, 2000));
        }
        week.FinishedUtc = _clock.GetUtcNow().UtcDateTime;
        // Its own calls only: Alpha's pass for the layer and the check of its engine run, inside its window.
        var alphaPurpose = $"floor-target L{week.LayerId}";
        var checkPurpose = $"run-check {week.EngineRunId}";
        var reportPurpose = week.PublishBatchId is { } pb ? RunReportWriter.Purpose(week.LayerId, pb) : null;
        var calls = await _db.AiCallLog.AsNoTracking()
            .Where(c => c.CalledAtUtc >= week.StartedUtc && c.CalledAtUtc <= week.FinishedUtc
                        && (c.Purpose == alphaPurpose || (week.EngineRunId != null && c.Purpose == checkPurpose) || c.Purpose == reportPurpose))
            .ToListAsync(CancellationToken.None);
        (week.AiCalls, week.AiCostUsd) = (calls.Count, calls.Sum(c => c.CostUsd ?? 0m));
        await _db.SaveChangesAsync(CancellationToken.None);
        return week;
    }

    private async Task<string> StepsAsync(RunWeek week, CancellationToken ct)
    {
        string Stop(string why) { week.Status = "Stopped"; return why; }

        if (await KillSwitchAsync(week, "alpha", ct) is { } stopped) return Stop(stopped);
        var since = _clock.GetUtcNow().UtcDateTime.AddHours(-6);
        if (await _db.DailyCycleSteps.AsNoTracking().AnyAsync(s => s.Status == StepStatus.Running && s.StartedUtc >= since, ct))
        {
            await RecordAsync(week, "daily-cycle", new RunWeekStepResult(StepStatus.Failed, "The daily cycle is running: Run week waits for it to finish (they run the same agents and engine)."), ct);
            return Stop("The daily cycle is running: try again when it has finished.");
        }
        var size = await StepAsync(week, "size-check", () => _steps.SizeCheckAsync(ct), ct);
        if (size.Status == StepStatus.Failed) return Stop($"The database is too full for another engine run: {size.Detail}.");
        var alpha = await StepAsync(week, "alpha", () => _steps.AlphaAsync(week.LayerId, ct), ct);
        week.PassId = alpha.Id;
        if (alpha.Status == StepStatus.Failed) return Stop($"Alpha's pass failed: {alpha.Detail}. Nothing after it ran.");

        if (await KillSwitchAsync(week, "engine", ct) is { } s2) return Stop(s2);
        var engine = await StepAsync(week, "engine", () => _steps.EngineAsync(week.LayerId, $"{TriggeredBy} ({week.StartedBy})", ct), ct);
        week.EngineRunId = engine.Id;
        if (engine.Status == StepStatus.Failed || engine.Id is null) return Stop($"The engine run failed: {engine.Detail}. Nothing after it ran.");

        if (await KillSwitchAsync(week, "check", ct) is { } s3) return Stop(s3);
        var check = await StepAsync(week, "check", () => _steps.CheckAsync(engine.Id.Value, ct), ct);
        week.CheckId = check.Id;
        if (check.Status == StepStatus.Failed) return Stop($"The run checker stopped the run: {check.Detail}. Nothing was published.");

        if (await KillSwitchAsync(week, "publish", ct) is { } s4) return Stop(s4);
        if (!_publisher.IsSandbox)
        {
            await RecordAsync(week, "publish", new RunWeekStepResult(StepStatus.Failed,
                $"Refused: Publisher:Target is '{_publisher.Target}'. Run week publishes only to the shop sandbox; going live is the canary in GO-LIVE.md."), ct);
            return Stop("The publish refused: the target isn't the sandbox. Nothing was published.");
        }
        var (items, chosen) = await _steps.CheckedPricesAsync(engine.Id.Value, ct);
        if (items.Count == 0)
        {
            await RecordAsync(week, "publish", new RunWeekStepResult(StepStatus.Skipped, $"Nothing to publish: {chosen}"), ct);
            week.Status = "Succeeded";
            return "Done: the run had no checked price to publish.";
        }
        PublishOutcome? outcome = null;
        var publish = await StepAsync(week, "publish", async () =>
        {
            outcome = await _steps.PublishAsync(week.LayerId, engine.Id.Value, items, ct);
            var skipped = outcome.Exceptions.GroupBy(e => e.Kind).Select(g => $"{g.Key} {g.Count():N0} (e.g. {g.First().Sku}: {g.First().Detail})");
            var unwritten = outcome.Exceptions.Count(e => e.Kind is "at-or-above-shelf" or "changed-since-run" or "above-ceiling" or "no-ceiling");
            return new RunWeekStepResult(outcome.Status == "Published" ? (unwritten > 0 ? StepStatus.Warning : StepStatus.Succeeded) : StepStatus.Failed,
                $"batch #{outcome.BatchId} {outcome.Status}: {chosen}; {CheckedPricePublisher.Describe(outcome)}",
                outcome.Exceptions.Count == 0 ? null : "Listed in the exceptions inbox: " + string.Join("; ", skipped), outcome.BatchId);
        }, ct);
        week.PublishBatchId = publish.Id;
        if (outcome is null || publish.Status == StepStatus.Failed) return Stop($"The publish didn't go through: {publish.Detail}.");

        var o = outcome;
        var ok = o.RoundTripMismatches == 0 && !o.RolledBack;
        await RecordAsync(week, "verify", new RunWeekStepResult(ok ? StepStatus.Succeeded : StepStatus.Warning,
            $"round-trip: {o.RoundTripChecked - o.RoundTripMismatches:N0} of {o.RoundTripChecked:N0} read back as written" + (o.RolledBack ? "; the batch was rolled back" : ""),
            ok ? null : "The writer reads the winning deal back after each publish; too many mismatches roll the batch back (the exceptions inbox lists it).", o.BatchId), ct);
        // The run's report and Alpha's account of it; a failed report doesn't undo the publish.
        if (!o.RolledBack)
        {
            await StepAsync(week, "report", () => _steps.ReportAsync(o.BatchId, ct), ct);
            await StepAsync(week, RunWeekPriceList.Step, () => _steps.PriceListAsync(week.Id, week.LayerId, o.BatchId, RunWeekPriceList.OnShelf(items, o), ct), ct);
        }
        week.Status = "Succeeded";
        return $"Done: published to the sandbox in batch #{o.BatchId}" + (ok ? "." : ", with read-back mismatches.");
    }

    private async Task<string?> KillSwitchAsync(RunWeek week, string before, CancellationToken ct)
    {
        var state = await _killSwitch.GetAsync(ct);
        if (!state.Stopped) return null;
        await RecordAsync(week, "kill-switch", new RunWeekStepResult(StepStatus.Failed, $"The kill switch is pulled ({state.Reason}): stopped before {before}."), ct);
        return $"The kill switch is pulled: stopped before {before}.";
    }

    private async Task<RunWeekStepResult> StepAsync(RunWeek week, string name, Func<Task<RunWeekStepResult>> run, CancellationToken ct)
    {
        var row = new RunWeekStep { RunWeekId = week.Id, Step = name, StartedUtc = _clock.GetUtcNow().UtcDateTime, Status = StepStatus.Running };
        _db.RunWeekSteps.Add(row);
        await _db.SaveChangesAsync(ct);
        RunWeekStepResult r;
        try
        {
            r = await run();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Run week #{Id}: {Step} failed.", week.Id, name);
            r = new RunWeekStepResult(StepStatus.Failed, ex.GetBaseException().Message);
        }
        (row.Status, row.Detail, row.Why, row.FinishedUtc) = (r.Status, Truncate(r.Detail, 2000), Truncate(r.Why, 4000), _clock.GetUtcNow().UtcDateTime);
        await _db.SaveChangesAsync(CancellationToken.None);
        return r;
    }

    private Task RecordAsync(RunWeek week, string name, RunWeekStepResult r, CancellationToken ct) => StepAsync(week, name, () => Task.FromResult(r), ct);

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}

/// <summary>The real steps: each resolves its service in its own scope, as the daily cycle does.</summary>
public sealed class RunWeekSteps : IRunWeekSteps
{
    private readonly IServiceProvider _sp;
    /// <summary>The margin-trim cuts the checked set held back, from <see cref="CheckedPricesAsync"/> to <see cref="PublishAsync"/>.</summary>
    private IReadOnlyList<string>? _heldBackTrimCuts;

    public RunWeekSteps(IServiceProvider sp) => _sp = sp;

    /// <summary>The whole data file counts: the engine run's proposals and snapshots, and the publish's writes (the sandbox's
    /// discount and regular-price tables, and an audit row per discount or regular price written).</summary>
    public async Task<RunWeekStepResult> SizeCheckAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var (gb, warn) = await scope.ServiceProvider.GetRequiredService<DatabaseMaintenance>().SizeAsync(ct);
        return new RunWeekStepResult(warn ? StepStatus.Failed : StepStatus.Succeeded,
            $"{gb:0.00} GB used of LocalDB's 10 GB (sandbox discounts, regular prices and publish audits included)" + (warn ? ", over the warning line" : ""));
    }

    public async Task<RunWeekStepResult> AlphaAsync(int layerId, CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var r = await scope.ServiceProvider.GetRequiredService<FloorTargetAgent>().RunPassAsync(layerId, ct: ct);
        var changed = r.Decisions.Where(d => d.Status == DecisionStatus.Applied && d.Action != "keep").ToList();
        var why = r.Decisions.Where(d => d.Action != "keep" || d.Status != DecisionStatus.Applied).Take(6)
            .Select(d => $"band {d.BandId} {d.Category}: {d.Action} {d.CurrentFloorPct:0.#}→{d.ProposedFloorPct:0.#} % floor, {d.Status}{(d.Limit is null ? "" : $" ({d.Limit})")}: {d.Reason}");
        return new RunWeekStepResult(r.Status is "Succeeded" or "Skipped" ? StepStatus.Succeeded : StepStatus.Failed,
            $"pass #{r.PassId} {r.Status}: {changed.Count} changed, {r.Count(DecisionStatus.Held)} held for a person, {r.Count(DecisionStatus.Rejected)} rejected by code, " +
            $"{r.Count(DecisionStatus.Pending)} pending (approve-first) {r.Error}".Trim(), string.Join(" | ", why), r.PassId);
    }

    public async Task<RunWeekStepResult> EngineAsync(int layerId, string triggeredBy, CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var run = await scope.ServiceProvider.GetRequiredService<PricingRunOrchestrator>().ExecuteRunAsync(triggeredBy, isOnDemand: true, layerId, ct);
        var ok = run.Status is RunStatus.Succeeded or RunStatus.SucceededWithErrors;
        return new RunWeekStepResult(ok ? StepStatus.Succeeded : StepStatus.Failed,
            $"run #{run.Id} {run.Status}: {run.SkuCount:N0} SKUs, {run.ProposalCount:N0} proposals, {run.ErrorCount} errors {run.ErrorMessage}".Trim(), null, run.Id);
    }

    public async Task<RunWeekStepResult> CheckAsync(long runId, CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var r = await scope.ServiceProvider.GetRequiredService<RunChecker>().CheckAsync(runId, ct: ct);
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        var flags = r.CheckId is { } id
            ? (await db.AgentRunCheckRows.AsNoTracking().Where(x => x.CheckId == id).Select(x => x.Flags).ToListAsync(ct))
                .SelectMany(f => f.Split(',', StringSplitOptions.RemoveEmptyEntries)).GroupBy(f => f).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count():N0}")
            : Enumerable.Empty<string>();
        // A check that didn't finish stops the run; blocked rows are held with the warned ones and the rest goes on (the user's decision, 1 Nov).
        var failed = r.Status != "Succeeded";
        return new RunWeekStepResult(failed ? StepStatus.Failed : r.Verdict == Severity.Block ? StepStatus.Warning : StepStatus.Succeeded,
            $"check #{r.CheckId}: verdict {r.Verdict}, {r.Flagged:N0} flagged, {r.Sent} judged by the model; {r.Blocks} block, {r.Warns:N0} warn (both held), {r.Fixed} fixed {r.Error}".Trim(),
            "Flags: " + string.Join(", ", flags), r.CheckId);
    }

    public async Task<(IReadOnlyList<PublishItem> Items, string Detail)> CheckedPricesAsync(long runId, CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var set = await scope.ServiceProvider.GetRequiredService<CheckedExport>().CheckedSetAsync(runId, ct);
        if (set.Check is null) return (Array.Empty<PublishItem>(), $"run #{runId} has no check");
        if (set.Blocked is { } why) return (Array.Empty<PublishItem>(), $"check #{set.Check.Id} blocked it: {why}");
        _heldBackTrimCuts = set.HeldBackTrimCutSkus;
        return (CheckedPricePublisher.Items(set), CheckedPricePublisher.Chosen(set));
    }

    public async Task<PublishOutcome> PublishAsync(int layerId, long runId, IReadOnlyList<PublishItem> items, CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<NopDiscountWriter>();
        var o = await writer.PublishAsync(CheckedPricePublisher.Request(layerId, runId, items, _heldBackTrimCuts?.Count), ct);
        if (o.Status == "Published") await CheckedPricePublisher.EndHeldBackTrimCutsAsync(writer, layerId, _heldBackTrimCuts, ct);
        return o;
    }

    public async Task<RunWeekStepResult> ReportAsync(long batchId, CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var r = await scope.ServiceProvider.GetRequiredService<RunReportWriter>().WriteAsync(batchId, ct: ct);
        if (r is null) return new RunWeekStepResult(StepStatus.Skipped, $"batch #{batchId} isn't a publish that went through: no report");
        return new RunWeekStepResult(r.AlphaStatus == "written" ? StepStatus.Succeeded : StepStatus.Warning,
            $"report #{r.Id}: {r.OnShelf:N0} of {r.Requested:N0} prices on the shelf, {r.Changed:N0} changed ({r.Raised:N0} up, {r.Lowered:N0} down), " +
            $"expected {(r.ExpectedGp30Eur is { } e ? $"€{e:N0}" : "–")} a month (estimated); Alpha's account {r.AlphaStatus}", r.Headline, r.Id);
    }

    public async Task<RunWeekStepResult> PriceListAsync(long runWeekId, int layerId, long batchId, IReadOnlyList<PublishItem> onShelf, CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var (path, lines) = await scope.ServiceProvider.GetRequiredService<RunWeekPriceList>().WriteAsync(runWeekId, layerId, batchId, onShelf, ct);
        var withRule = lines.Count(l => l.AlgorithmUsed.Contains("Alpha's", StringComparison.Ordinal));
        return new RunWeekStepResult(StepStatus.Succeeded,
            $"{lines.Count:N0} of the run's prices on the sandbox shelf at our price, {withRule:N0} of them under one of Alpha's band × category rules: {PriceListPath(runWeekId)}",
            $"File: {path}. Columns: {RunWeekPriceList.Header}. Dates are local time; a raise is the store's regular price, so it has no end date.");
    }

    /// <summary>Where the web app serves a Run week's price list.</summary>
    public static string PriceListPath(long runWeekId) => $"/Run/PriceList/{runWeekId}";
}
