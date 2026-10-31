using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Checker;
using PricingTool.Agent.Storage;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;

namespace PricingTool.Agent.Operations;

public static class StepStatus
{
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Warning = "Warning";
    public const string Skipped = "Skipped";
    public const string Failed = "Failed";
    public const string NotBuilt = "NotBuilt";

    /// <summary>A step in one of these states is done for the day and is not run again.</summary>
    public static bool IsDone(string s) => s is Succeeded or Warning or Skipped or NotBuilt;
}

/// <summary>Publishes a run's checked prices to the shop sandbox, discounts and raises (the publisher implements it;
/// a host without the publisher has none and the cycle's publish step says so).</summary>
public interface ICheckedPricePublisher
{
    Task<(string Status, string Detail)> PublishCheckedAsync(int layerId, long runId, CancellationToken ct = default);
}

/// <summary>Brings the live shop's other discounts and regular prices into the shop sandbox for a run's products, so the publish
/// after it measures what would reach the live shelf (the publisher implements it; Prompt 10 part B).</summary>
public interface ISandboxMirror
{
    Task<(string Status, string Detail)> MirrorAsync(int layerId, long runId, CancellationToken ct = default);

    /// <summary>Deletes the layer's mirrored copies ended more than <paramref name="days"/> days ago that no audit names.</summary>
    Task<int> RetireAsync(int layerId, int days, CancellationToken ct = default);
}

/// <summary>
/// The laptop's daily cycle for the 30 days (plan section 5 "Live operation on the laptop", prompt 05), one job
/// per day: size check → incremental hk refresh → engine runs (Gjirafa50 first) → Alpha's pass (weekly, on its
/// day) → run checks → exports for the team's push → the live shop's other discounts and regular prices into the shop
/// sandbox (Prompt 10 part B) → the same checked prices to the sandbox (discounts and raises, prompt 07) → money ledger →
/// time-sheet export → retention → backup.
/// Each step writes its own <c>ai.DailyCycleStep</c> row, and a failing step doesn't stop the rest. Idempotent
/// per day: a second start resumes the steps not done yet; a failed step is retried at most
/// <see cref="MaxAttemptsPerDay"/> times a day (so a failing AI step can't turn into a stream of paid calls).
/// </summary>
public sealed class DailyCycle
{
    public const int MaxAttemptsPerDay = 3;

    /// <summary><c>PricingRun.TriggeredBy</c> of the cycle's own engine runs. Only these count as today's run, so a
    /// manual run earlier in the day neither stands in for the cycle's run nor gets checked and exported by it.</summary>
    public const string TriggeredBy = "daily cycle";

    private readonly IServiceProvider _sp;
    private readonly AgentDbContext _db;
    private readonly AgentOptions _options;
    private readonly ILogger<DailyCycle> _logger;
    private readonly TimeProvider _clock;

    public DailyCycle(IServiceProvider sp, AgentDbContext db, IOptions<AgentOptions> options, ILogger<DailyCycle> logger, TimeProvider? clock = null)
    {
        _sp = sp;
        _db = db;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public sealed record Step(string Name, Func<CancellationToken, Task<(string Status, string Detail)>> Run);

    /// <summary>Tests only: the steps to run instead of the real ones.</summary>
    internal Func<DateOnly, IEnumerable<Step>>? StepSource { get; set; }

    public DateOnly Today() => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    public async Task<IReadOnlyList<DailyCycleStep>> RunAsync(DateOnly? date = null, CancellationToken ct = default)
    {
        var day = date ?? Today();
        foreach (var step in (StepSource ?? Steps)(day))
        {
            var history = await _db.DailyCycleSteps.AsNoTracking().Where(s => s.CycleDate == day && s.Step == step.Name).ToListAsync(ct);
            if (history.Any(s => StepStatus.IsDone(s.Status)) || history.Count(s => s.Status == StepStatus.Failed) >= MaxAttemptsPerDay)
                continue;

            var row = new DailyCycleStep { CycleDate = day, Step = step.Name, StartedUtc = _clock.GetUtcNow().UtcDateTime, Status = StepStatus.Running };
            _db.DailyCycleSteps.Add(row);
            await _db.SaveChangesAsync(ct);
            try
            {
                var (status, detail) = await step.Run(ct);
                row.Status = status;
                row.Detail = Truncate(detail);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                row.Status = StepStatus.Failed;
                row.Error = Truncate(ex.GetBaseException().Message);
                _logger.LogError(ex, "Daily cycle {Day}: step {Step} failed; the cycle goes on.", day, step.Name);
            }
            row.FinishedUtc = _clock.GetUtcNow().UtcDateTime;
            _db.ChangeTracker.Clear();
            _db.DailyCycleSteps.Update(row);
            await _db.SaveChangesAsync(CancellationToken.None);
            _logger.LogInformation("Daily cycle {Day}: {Step} {Status} {Detail}", day, step.Name, row.Status, row.Detail ?? row.Error);
        }
        return await _db.DailyCycleSteps.AsNoTracking().Where(s => s.CycleDate == day).OrderBy(s => s.Id).ToListAsync(ct);
    }

    /// <summary>The steps in order. Each resolves its services in its own scope, so one step's failure or
    /// tracked state never leaks into the next.</summary>
    public IEnumerable<Step> Steps(DateOnly day)
    {
        var c = _options.Cycle;
        yield return new Step("size-check", ct => SizeValve.CheckAsync(c, new SizeValve.Probe(UsedGbAsync, TrimAsync), ct));
        yield return new Step("refresh-hk", async ct =>
        {
            var results = await With((HkRefresher h) => h.RefreshAsync(ct));
            return (StepStatus.Succeeded, string.Join("; ", results.Select(r =>
                r.Mode == "kept" ? $"{r.Table} kept ({r.Note})" : $"{r.Table} {r.Mode} {r.Rows:N0} rows, replica {r.ReplicaMs / 1000.0:0.0} s")));
        });
        foreach (var layer in c.Layers)
            yield return new Step($"engine-L{layer}", async ct =>
            {
                if (await TodaysRunAsync(layer, day, ct) is { } done)
                    return (StepStatus.Skipped, $"already ran today: run #{done.Id}");
                using var scope = _sp.CreateScope();
                var run = await scope.ServiceProvider.GetRequiredService<PricingRunOrchestrator>().ExecuteRunAsync(TriggeredBy, isOnDemand: false, layer, ct);
                var ok = run.Status is RunStatus.Succeeded or RunStatus.SucceededWithErrors;
                return (ok ? StepStatus.Succeeded : StepStatus.Failed,
                    $"run #{run.Id} {run.Status}: {run.SkuCount:N0} SKUs, {run.ProposalCount:N0} proposals, {run.ErrorCount} errors {run.ErrorMessage}");
            });
        foreach (var layer in c.Layers)
            yield return new Step($"alpha-L{layer}", async ct =>
            {
                if (day.DayOfWeek != c.AlphaDay) return (StepStatus.Skipped, $"Alpha's pass runs on {c.AlphaDay}s");
                var r = await With((FloorTargetAgent a) => a.RunPassAsync(layer, ct: ct));
                var detail = $"pass #{r.PassId} {r.Status}: {r.Decisions.Count(d => d.Status == DecisionStatus.Applied && d.Action != "keep")} changed, " +
                             $"{r.Count(DecisionStatus.Held)} held, {r.Count(DecisionStatus.Rejected)} rejected, {r.Count(DecisionStatus.Pending)} pending {r.Error}";
                return (r.Status == "Succeeded" || r.Status == "Skipped" ? StepStatus.Succeeded : StepStatus.Failed, detail);
            });
        foreach (var layer in c.Layers)
            yield return new Step($"check-L{layer}", async ct =>
            {
                var run = await TodaysRunAsync(layer, day, ct) ?? throw new InvalidOperationException($"no engine run today for layer {layer}");
                var r = await With((RunChecker k) => k.CheckAsync(run.Id, ct: ct));
                return (r.Status == "Succeeded" ? StepStatus.Succeeded : StepStatus.Failed,
                    $"check #{r.CheckId} of run #{run.Id}: verdict {r.Verdict}, {r.Flagged:N0} flagged, {r.Sent} judged; {r.Blocks} block, {r.Warns} warn, {r.Fixed} fixed {r.Error}");
            });
        foreach (var layer in c.Layers)
            yield return new Step($"export-L{layer}", async ct =>
            {
                var run = await TodaysRunAsync(layer, day, ct) ?? throw new InvalidOperationException($"no engine run today for layer {layer}");
                var r = await With((CheckedExport x) => x.ExportAsync(run.Id, c.ExportDir(), ct));
                return (r.Exported ? StepStatus.Succeeded : StepStatus.Warning, r.Reason + (r.FilePath is null ? "" : $" File: {r.FilePath}"));
            });
        foreach (var layer in c.Layers)
            yield return new Step($"mirror-L{layer}", async ct =>
            {
                var run = await TodaysRunAsync(layer, day, ct) ?? throw new InvalidOperationException($"no engine run today for layer {layer}");
                if (SizeValve.SandboxPause(c, await UsedGbAsync(ct)) is { } full) return (StepStatus.Skipped, full);
                using var scope = _sp.CreateScope();
                if (scope.ServiceProvider.GetService<ISandboxMirror>() is not { } mirror)
                    return (StepStatus.NotBuilt, "no sandbox mirror in this host: the sandbox keeps the shop as it was seeded");
                return await mirror.MirrorAsync(layer, run.Id, ct);
            });
        foreach (var layer in c.Layers)
            yield return new Step($"publish-L{layer}", async ct =>
            {
                var run = await TodaysRunAsync(layer, day, ct) ?? throw new InvalidOperationException($"no engine run today for layer {layer}");
                if (SizeValve.SandboxPause(c, await UsedGbAsync(ct)) is { } full) return (StepStatus.Skipped, full);
                using var scope = _sp.CreateScope();
                if (scope.ServiceProvider.GetService<ICheckedPricePublisher>() is not { } publisher)
                    return (StepStatus.NotBuilt, "no publisher in this host: the push file is the only path");
                return await publisher.PublishCheckedAsync(layer, run.Id, ct);
            });
        // Every publish that went through gets its report (Run week writes its own; this catches the cycle's and any by hand).
        yield return new Step("run-report", async ct =>
        {
            var written = await With((RunReportWriter w) => w.WriteMissingAsync(ct: ct));
            return (StepStatus.Succeeded, written.Count == 0 ? "no publish without a report" : string.Join("; ", written.Select(r =>
                $"batch #{r.BatchId} (run #{r.RunId}): {r.OnShelf:N0} of {r.Requested:N0} on the shelf, Alpha's account {r.AlphaStatus}")));
        });
        yield return new Step("ledger", async ct =>
        {
            var (added, measured, s) = await With(async (MoneyLedger ledger) =>
            {
                var (a, m) = await ledger.UpdateAsync(ct);
                return (a, m, await ledger.SummaryAsync(ct));
            });
            return (StepStatus.Succeeded, $"{added} new applied sets, {measured} windows measured; measured 30-day total {s.Measured30Eur:0} EUR " +
                                          $"over {s.Entries} entries ({s.Measured30} measured at 30 days; unmeasured count as 0)");
        });
        yield return new Step("campaign-calendar", async ct =>
        {
            var (added, moved) = await With((Campaigns.CampaignCalendar c) => c.SyncAsync(ct));
            return (StepStatus.Succeeded, $"{added} campaigns added from the campaign history, {moved} statuses moved (planned / live / done)");
        });
        yield return new Step("outcomes", async ct =>
        {
            var r = await With((OutcomeTracker t) => t.UpdateAsync(ct));
            return (StepStatus.Succeeded, $"{r.Decisions} applied changes: {r.NotLive} not live yet (no pushed export priced with them), {r.Waiting} waiting " +
                                          $"for their window, {r.Measured} measured; {r.RolledBack} rolled back and paused" +
                                          (r.RollbacksWaiting > 0 ? $", {r.RollbacksWaiting} rollbacks waiting for the kill switch" : ""));
        });
        yield return new Step("time-sheet", async ct =>
        {
            var r = await With((TimeSheetExporter t) => t.ExportAsync(ct));
            return (StepStatus.Succeeded, $"{r.Exported} rows exported to {r.XlsxPath} (+ .csv, .json); {r.WithoutMinutesBefore} rows wait for their task type's minutes before");
        });
        yield return new Step("retention", async ct =>
        {
            var (proposals, snapshots) = await With((DatabaseMaintenance m) => m.RetentionAsync(ct));
            var exceptions = await With((DatabaseMaintenance m) => m.ExceptionRetentionAsync(ct));
            return (StepStatus.Succeeded, $"deleted {proposals:N0} proposals and {snapshots:N0} snapshot rows of older local runs (keeping {c.KeepRunsPerLayer} per layer and the imported prod runs), " +
                                          $"and {exceptions:N0} publish exceptions older than {DatabaseMaintenance.ExceptionDays} days");
        });
        yield return new Step("backup", async ct =>
        {
            var (path, bytes, removed) = await With((DatabaseMaintenance m) => m.BackupAsync(ct));
            return (StepStatus.Succeeded, $"{path} ({bytes / 1024.0 / 1024 / 1024:0.00} GB); {removed} old backups removed (keeping {c.KeepBackups})");
        });
        yield return new Step("cost-watcher", async ct =>
        {
            // The watcher runs on its own 15-minute loop in the engine host; this step reports its last day.
            var since = _clock.GetUtcNow().UtcDateTime.AddDays(-1);
            var done = await _db.CostObservations.AsNoTracking().Where(o => o.ProcessedUtc >= since).GroupBy(o => o.Outcome)
                .Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
            var waiting = await _db.CostObservations.CountAsync(o => o.ProcessedUtc == null, ct);
            return (StepStatus.Succeeded, $"cost watcher, last 24 h: {(done.Count == 0 ? "no cost moves" : string.Join(", ", done.Select(d => $"{d.Count} {d.Key}")))}; {waiting} waiting");
        });
        // What the agents couldn't settle reaches a person once a day, with who can fix it (Alpha's problem pass, posted in the chat).
        yield return new Step("problems", async ct =>
        {
            var r = await With((ProblemDesk d) => d.RunAsync(ct));
            return (r.Status == "Failed" ? StepStatus.Failed : r.Status == "Skipped" ? StepStatus.Skipped : StepStatus.Succeeded,
                $"{(r.Review is { } v ? $"re-check: {v.Fixed} fixed, {v.Resolved} closed, {v.StillHeld} still held; " : "")}" +
                $"Alpha reported {r.Reported}, confirmed {r.Confirmed} real rises{(r.PostId is { } id ? $", posted message #{id}" : "")}{(r.Error is null ? "" : $" ({r.Error})")}");
        });
        yield return new Step("weekly-report", async ct =>
        {
            if (day.DayOfWeek != c.NovaDay) return (StepStatus.Skipped, $"The weekly report is written on {c.NovaDay}s");
            var r = await With((Chat.AgentChat chat) => chat.WeeklyReportAsync(ct));
            var email = _sp.GetRequiredService<IOptions<AgentOptions>>().Value.ReportEmail;
            return (StepStatus.Succeeded, $"weekly report posted as the first message of conversation {r.ConversationId}; " +
                                          (string.IsNullOrWhiteSpace(email) ? "no email (Agent:ReportEmail isn't set)" : "email not sent: sending isn't built yet"));
        });
        foreach (var layer in c.Layers)
            yield return new Step($"nova-L{layer}", async ct =>
            {
                if (day.DayOfWeek != c.NovaDay) return (StepStatus.Skipped, $"Nova's campaign pass runs on {c.NovaDay}s");
                var r = await With((Campaigns.CampaignAgent n) => n.RunPassAsync(layer, ct: ct));
                return (r.Status is "Succeeded" or "Skipped" ? StepStatus.Succeeded : StepStatus.Failed,
                    $"pass #{r.PassId} {r.Status}: {r.Briefs.Count} briefs ({r.Count(BriefStatus.Proposed)} proposed, {r.Count(BriefStatus.Held)} held, " +
                    $"{r.Count(BriefStatus.Rejected)} rejected), {r.ResultsWritten} campaign results {r.Error}");
            });
    }

    /// <summary>Runs one step's work in its own DI scope (disposed afterwards).</summary>
    private async Task<TResult> With<T, TResult>(Func<T, Task<TResult>> work) where T : notnull
    {
        using var scope = _sp.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<T>());
    }

    /// <summary>The cycle's successful engine run for the layer on the day (local), if there is one.</summary>
    private async Task<decimal> UsedGbAsync(CancellationToken ct) => (await With((DatabaseMaintenance m) => m.SizeAsync(ct))).UsedGb;

    /// <summary>The size valve's trim: one local run per layer (keeping the run behind each layer's latest export), publish
    /// exceptions and the sandbox's mirrored copies older than <paramref name="days"/> days.</summary>
    private async Task<string> TrimAsync(int days, CancellationToken ct)
    {
        var c = _options.Cycle;
        var exported = await _db.ExportBatches.AsNoTracking().GroupBy(e => e.LayerId)
            .Select(g => g.OrderByDescending(e => e.Id).First().RunId).ToListAsync(ct);
        var (proposals, snapshots) = await With((DatabaseMaintenance m) => m.RetentionAsync(1, exported, ct));
        var exceptions = await With((DatabaseMaintenance m) => m.ExceptionRetentionAsync(ct, days));
        var mirrored = 0;
        using (var scope = _sp.CreateScope())
            if (scope.ServiceProvider.GetService<ISandboxMirror>() is { } mirror)
                foreach (var layer in c.Layers) mirrored += await mirror.RetireAsync(layer, days, ct);
        return $"{proposals:N0} proposals and {snapshots:N0} snapshot rows of older local runs, {exceptions:N0} publish exceptions, {mirrored:N0} mirrored discounts";
    }

    private async Task<PricingRun?> TodaysRunAsync(int layer, DateOnly day, CancellationToken ct)
    {
        var start = day.ToDateTime(TimeOnly.MinValue);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(start, DateTimeKind.Unspecified), _clock.LocalTimeZone);
        using var scope = _sp.CreateScope();
        var tool = scope.ServiceProvider.GetRequiredService<PricingToolDbContext>();
        return await tool.PricingRuns.AsNoTracking()
            .Where(r => r.LayerId == layer && r.TriggeredBy == TriggeredBy && r.StartedUtc >= startUtc && r.StartedUtc < startUtc.AddDays(1)
                        && (r.Status == RunStatus.Succeeded || r.Status == RunStatus.SucceededWithErrors)
                        && tool.ProposedPrices.Any(p => p.PricingRunId == r.Id))
            .OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);
    }

    /// <summary>True when the day's latest attempt of some step failed and that step has attempts left.</summary>
    public static bool HasRetryableFailure(IEnumerable<DailyCycleStep> steps) =>
        steps.GroupBy(s => s.Step).Any(g => g.OrderBy(s => s.Id).Last().Status == StepStatus.Failed
                                            && g.Count(s => s.Status == StepStatus.Failed) < MaxAttemptsPerDay);

    private static string? Truncate(string? s) => s is null || s.Length <= 2000 ? s : s[..2000];
}
