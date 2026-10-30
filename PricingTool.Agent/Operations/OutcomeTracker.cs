using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Storage;
using PricingTool.Agent.Tools;
using PricingTool.Data;
using PricingTool.Data.Entities;

namespace PricingTool.Agent.Operations;

/// <summary>Units and gross profit of one window (normal days only), EUR VAT excl., booked cost.</summary>
public readonly record struct WindowSales(decimal Units, decimal GrossProfit);

/// <summary>The two windows around a day: the row's and the holdout's sales, and how many normal days each has.</summary>
public readonly record struct WindowPair(WindowSales Before, WindowSales After, WindowSales HoldoutBefore, WindowSales HoldoutAfter,
    int DaysBefore, int DaysAfter);

/// <summary>A band × category's and the holdout's daily sales around a change, and the campaign days to leave out.</summary>
public sealed record DailySeries(IReadOnlyList<DaySales> Days, IReadOnlySet<DateOnly> CampaignDays, string Holdouts);

/// <summary>What one measurement found (see <see cref="OutcomeTracker.Judge"/>), with the normal variation it was judged against.</summary>
public sealed record OutcomeFigures(
    decimal GpPerDayBefore, decimal GpPerDayAfter, decimal UnitsPerDayBefore, decimal UnitsPerDayAfter,
    decimal? HoldoutGpRatio, decimal? HoldoutUnitsRatio, decimal? ExpectedGpPerDayAfter, decimal? ExpectedUnitsPerDayAfter,
    decimal? GpVsExpectedPct, decimal? UnitsVsExpectedPct, string Verdict, string? Why)
{
    public int PlaceboWindows { get; init; }
    public decimal? PlaceboGpMinPct { get; init; }
    public decimal? PlaceboGpMaxPct { get; init; }
    public decimal? PlaceboUnitsMinPct { get; init; }
}

public sealed record OutcomeUpdate(int Decisions, int NotLive, int Waiting, int Measured, int RolledBack, int RollbacksWaiting);

/// <summary>When a run's prices reached customers: a pushed export, or a successful publish to the live shop.</summary>
internal sealed record LiveSource(int LayerId, long RunId, DateTime LiveUtc, long? ExportId, long? PublishId)
{
    public string What => ExportId is { } e ? $"export #{e} was pushed" : $"live publish batch #{PublishId} went out";
}

/// <summary>
/// The outcome tracker (plan section 5, "OutcomeTracker" and "Auto-pause and rollback"), shared by both agents. For
/// every applied floor/target change of a real pass it finds when the change reached customers (the first pushed
/// export of an engine run that priced with its rule), then compares the band × category's gross profit per day and
/// units per day over the <see cref="OutcomeOptions.WindowDays"/> days after that push with the same number of days
/// before, normal days only, booked cost from the sales lines. The holdout categories carry the season: expected
/// after = before × the holdout's after/before ratio.
///
/// <para>Band × category sales over two weeks are noisy: on 26 no-change fortnights of four of Alpha's rule groups
/// (Mar–Sep 2026) gross profit against this expectation ranged from −60 % to +217 %, and a plain "20 % under" rule
/// called a drop in about one window in five. So the same comparison is run on <see cref="OutcomeOptions.PlaceboWindows"/>
/// no-change windows of the same band × category before the change, and a drop must be at least
/// <see cref="OutcomeOptions.DropPct"/> % under the expectation <i>and</i> worse than every one of them (a false alarm
/// about once in 27 measurements). A drop rolls the change back, restoring the rule's previous floor and target, and
/// pauses the band × category, so the pricing agent's next changes there wait for a person. Each outcome is written to
/// <c>ai.AgentOutcome</c> and read back by <c>get_past_decisions</c>.</para>
/// </summary>
public sealed class OutcomeTracker
{
    public const string Source = "OutcomeTracker";

    private readonly AgentDbContext _db;
    private readonly PricingToolDbContext _tool;
    private readonly PricingDataReader _reader;
    private readonly IKillSwitch _killSwitch;
    private readonly AgentOptions _options;
    private readonly ILogger<OutcomeTracker> _logger;
    private readonly TimeProvider _clock;

    public OutcomeTracker(AgentDbContext db, PricingToolDbContext tool, PricingDataReader reader, IKillSwitch killSwitch,
        IOptions<AgentOptions> options, ILogger<OutcomeTracker> logger, TimeProvider? clock = null)
    {
        _db = db;
        _tool = tool;
        _reader = reader;
        _killSwitch = killSwitch;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Test seam: the row's and the holdout's daily sales in [from, to), and the campaign days.</summary>
    internal Func<AgentDecision, DateOnly, DateOnly, CancellationToken, Task<DailySeries>>? DailySource { get; set; }

    public async Task<OutcomeUpdate> UpdateAsync(CancellationToken ct = default)
    {
        var o = _options.Outcomes;
        var now = _clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

        var realPasses = _db.AgentPasses.Where(p => !p.IsTest).Select(p => p.Id);
        var decisions = await _db.AgentDecisions.AsNoTracking()
            .Where(d => d.Status == DecisionStatus.Applied && d.Action != "keep" && d.AppliedUtc != null && realPasses.Contains(d.PassId))
            .OrderBy(d => d.Id).ToListAsync(ct);
        var outcomes = await _db.AgentOutcomes.ToDictionaryAsync(x => x.DecisionId, ct);

        // When each rule went live: pushed exports, and successful publishes to the live shop (Target "Live", kind "Regular", not
        // rolled back; the publisher's PublisherOptions.LiveTarget and PublishKinds.Regular), the runs behind them and the rules those
        // runs priced with. A sandbox publish never reaches a customer, so it never counts.
        var pushed = (await _db.ExportBatches.AsNoTracking().Where(b => b.PushedUtc != null && b.ApprovalId != null).ToListAsync(ct))
            .Select(b => new LiveSource(b.LayerId, b.RunId, b.PushedUtc!.Value, b.Id, null))
            .Concat((await _db.PublishBatches.AsNoTracking()
                    .Where(b => b.Target == "Live" && b.Kind == "Regular" && b.Status == "Published" && b.RolledBackUtc == null && b.EngineRunId != null)
                    .ToListAsync(ct))
                .Select(b => new LiveSource(b.LayerId, b.EngineRunId!.Value, b.FinishedUtc ?? b.StartedUtc, null, b.Id)))
            .ToList();
        var runIds = pushed.Select(b => b.RunId).Distinct().ToList();
        var runStarts = await _tool.PricingRuns.AsNoTracking().Where(r => runIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.StartedUtc, ct);
        var runRules = (await _tool.PricingRunCategoryRules.AsNoTracking().Where(r => runIds.Contains(r.PricingRunId)).ToListAsync(ct))
            .ToLookup(r => r.PricingRunId);

        int notLive = 0, waiting = 0, measured = 0, rolledBack = 0, rollbackWaits = 0;
        foreach (var d in decisions)
        {
            if (!outcomes.TryGetValue(d.Id, out var x))
            {
                x = new AgentOutcome { DecisionId = d.Id, LayerId = d.LayerId, BandId = d.BandId, Category = d.Category };
                _db.AgentOutcomes.Add(x);
                outcomes[d.Id] = x;
            }
            x.WindowDays = o.WindowDays;
            x.UpdatedUtc = now;
            if (x.Status == OutcomeStatus.Final) continue;

            var live = pushed
                .Where(b => b.LayerId == d.LayerId && runStarts.TryGetValue(b.RunId, out var started) && started >= d.AppliedUtc
                            && runRules[b.RunId].Any(r => r.BandId == d.BandId && r.Category == d.Category
                                                          && Math.Abs(r.MarginFloorPct - d.ProposedFloorPct) < 0.005m
                                                          && Math.Abs((r.TargetMarginPct ?? -1m) - (d.ProposedTargetPct ?? -1m)) < 0.005m))
                .OrderBy(b => b.LiveUtc).FirstOrDefault();
            if (live is null)
            {
                x.Status = OutcomeStatus.NotLive;
                x.Evidence = "Applied to the rule; no export priced with it has been marked pushed and no live publish carried it, so its prices haven't reached customers.";
                notLive++;
                continue;
            }

            x.LiveFromUtc = live.LiveUtc;
            x.LiveExportBatchId = live.ExportId;
            x.LivePublishBatchId = live.PublishId;
            var pushDay = DateOnly.FromDateTime(live.LiveUtc);
            var afterEnds = pushDay.AddDays(1 + o.WindowDays);                 // the push day itself is in neither window
            if (today < afterEnds)
            {
                x.Status = OutcomeStatus.Waiting;
                x.Evidence = $"Live since {live.What} on {pushDay:yyyy-MM-dd}; measured from {afterEnds:yyyy-MM-dd} ({o.WindowDays} days after against {o.WindowDays} before).";
                waiting++;
                continue;
            }

            var series = await LoadAsync(d, pushDay, ct);
            var (f, w) = Measure(series, pushDay, o);
            x.GpPerDayBefore = Math.Round(f.GpPerDayBefore, 2);
            x.GpPerDayAfter = Math.Round(f.GpPerDayAfter, 2);
            x.UnitsPerDayBefore = Math.Round(f.UnitsPerDayBefore, 2);
            x.UnitsPerDayAfter = Math.Round(f.UnitsPerDayAfter, 2);
            x.HoldoutGpRatio = R(f.HoldoutGpRatio, 4);
            x.HoldoutUnitsRatio = R(f.HoldoutUnitsRatio, 4);
            x.ExpectedGpPerDayAfter = R(f.ExpectedGpPerDayAfter, 2);
            x.GpVsExpectedPct = R(f.GpVsExpectedPct, 2);
            x.UnitsVsExpectedPct = R(f.UnitsVsExpectedPct, 2);
            x.Verdict = f.Verdict;
            x.MeasuredUtc = now;
            x.Status = today >= afterEnds.AddDays(o.RemeasureDays) ? OutcomeStatus.Final : OutcomeStatus.Provisional;
            x.Evidence = Truncate($"{char.ToUpperInvariant(live.What[0])}{live.What[1..]} on {pushDay:yyyy-MM-dd}. {Describe(f, w, series.Holdouts)}" +
                                  (x.Status == OutcomeStatus.Provisional ? " Provisional: late cancellations still post." : ""));
            measured++;

            if (f.Verdict == "drop" && o.AutoRollback && x.Action is null or "rollback-waits")
            {
                var done = await RollBackAsync(d, x, f, ct);
                if (done) rolledBack++; else if (x.Action == "rollback-waits") rollbackWaits++;
            }
        }
        await _db.SaveChangesAsync(CancellationToken.None);
        return new OutcomeUpdate(decisions.Count, notLive, waiting, measured, rolledBack, rollbackWaits);
    }

    /// <summary>
    /// Read-only check of the measurement on real sales lines: what the tracker would find for <paramref name="decisionId"/>
    /// had its prices gone live on <paramref name="liveDay"/>. Writes nothing; never rolls back.
    /// </summary>
    public async Task<(OutcomeFigures Figures, string Basis)> ProbeAsync(long decisionId, DateOnly liveDay, CancellationToken ct = default)
    {
        var d = await _db.AgentDecisions.AsNoTracking().SingleAsync(x => x.Id == decisionId, ct);
        var series = await LoadAsync(d, liveDay, ct);
        var (f, w) = Measure(series, liveDay, _options.Outcomes);
        return (f, $"band {d.BandId} x {d.Category}, live day {liveDay:yyyy-MM-dd}. {Describe(f, w, series.Holdouts)}");
    }

    /// <summary>Window sums of a daily series: before = [day − n, day), after = [day + 1, day + 1 + n); campaign days left out.</summary>
    public static WindowPair Windows(DailySeries s, DateOnly day, int n)
    {
        (WindowSales Row, WindowSales Holdout, int Days) Sum(DateOnly from, DateOnly to)
        {
            var days = s.Days.Where(x => x.Day >= from && x.Day < to && !s.CampaignDays.Contains(x.Day)).ToList();
            var normal = Enumerable.Range(0, to.DayNumber - from.DayNumber).Count(i => !s.CampaignDays.Contains(from.AddDays(i)));
            return (new WindowSales(days.Sum(x => x.RowUnits), days.Sum(x => x.RowGrossProfit)),
                    new WindowSales(days.Sum(x => x.HoldoutUnits), days.Sum(x => x.HoldoutGrossProfit)), normal);
        }
        var before = Sum(day.AddDays(-n), day);
        var after = Sum(day.AddDays(1), day.AddDays(1 + n));
        return new WindowPair(before.Row, after.Row, before.Holdout, after.Holdout, before.Days, after.Days);
    }

    /// <summary>
    /// The change's comparison, judged against the normal variation: the same comparison on
    /// <see cref="OutcomeOptions.PlaceboWindows"/> no-change windows before it, in weekly steps; the latest one's
    /// after-window ends six days before the push day, so none of them contains the change.
    /// </summary>
    public static (OutcomeFigures Figures, WindowPair Window) Measure(DailySeries s, DateOnly liveDay, OutcomeOptions o)
    {
        var placeboGp = new List<decimal>();
        var placeboUnits = new List<decimal>();
        for (var k = 3; k < 3 + o.PlaceboWindows; k++)
        {
            var p = Windows(s, liveDay.AddDays(-7 * k), o.WindowDays);
            var f = Judge(p.Before, p.After, p.HoldoutBefore, p.HoldoutAfter, p.DaysBefore, p.DaysAfter, o);
            if (f is { Verdict: not "thin", GpVsExpectedPct: { } gp, UnitsVsExpectedPct: { } units })
            {
                placeboGp.Add(gp);
                placeboUnits.Add(units);
            }
        }
        var w = Windows(s, liveDay, o.WindowDays);
        return (Judge(w.Before, w.After, w.HoldoutBefore, w.HoldoutAfter, w.DaysBefore, w.DaysAfter, o, placeboGp, placeboUnits), w);
    }

    /// <summary>
    /// The comparison, pure. Not attributable (thin) when either window of the row or of the holdout has fewer than
    /// <see cref="OutcomeOptions.MinUnits"/> units, or the holdout made no profit before. A drop: gross profit, or units,
    /// at least <see cref="OutcomeOptions.DropPct"/> % under the season-adjusted expectation; a units fall while gross
    /// profit is at or over the gain line is a raise doing its job (fewer units at a better margin), not a drop. Gain:
    /// gross profit at least <see cref="OutcomeOptions.GainPct"/> % over it. With no-change windows given
    /// (<paramref name="placeboGp"/>), a drop or a gain must also lie outside all of them, and with fewer than
    /// <see cref="OutcomeOptions.MinPlaceboWindows"/> of them the verdict is "unclear" (never rolled back).
    /// </summary>
    public static OutcomeFigures Judge(WindowSales before, WindowSales after, WindowSales holdoutBefore, WindowSales holdoutAfter,
        int daysBefore, int daysAfter, OutcomeOptions o, IReadOnlyList<decimal>? placeboGp = null, IReadOnlyList<decimal>? placeboUnits = null)
    {
        if (daysBefore <= 0 || daysAfter <= 0)
            return new OutcomeFigures(0, 0, 0, 0, null, null, null, null, null, null, "thin", "no normal days in a window");
        decimal gpB = before.GrossProfit / daysBefore, gpA = after.GrossProfit / daysAfter;
        decimal uB = before.Units / daysBefore, uA = after.Units / daysAfter;
        if (before.Units < o.MinUnits || after.Units < o.MinUnits || holdoutBefore.Units < o.MinUnits || holdoutAfter.Units < o.MinUnits
            || holdoutBefore.GrossProfit <= 0)
            return new OutcomeFigures(gpB, gpA, uB, uA, null, null, null, null, null, null, "thin",
                $"under {o.MinUnits} units in a window, or no holdout profit before: not attributable, counts as no effect");

        var gpRatio = holdoutAfter.GrossProfit / daysAfter / (holdoutBefore.GrossProfit / daysBefore);
        var unitsRatio = holdoutAfter.Units / daysAfter / (holdoutBefore.Units / daysBefore);
        var expGp = gpB * gpRatio;
        var expUnits = uB * unitsRatio;
        decimal? gpVs = expGp == 0 ? null : 100m * (gpA - expGp) / Math.Abs(expGp);
        decimal? unitsVs = expUnits == 0 ? null : 100m * (uA - expUnits) / expUnits;
        var figures = new OutcomeFigures(gpB, gpA, uB, uA, gpRatio, unitsRatio, expGp, expUnits, gpVs, unitsVs, "neutral", null);

        var gpDrop = gpVs <= -o.DropPct;
        var unitsDrop = unitsVs <= -o.DropPct && !(gpVs >= o.GainPct);
        var gain = gpVs >= o.GainPct;
        if (placeboGp is null)
            return figures with
            {
                Verdict = gpDrop || unitsDrop ? "drop" : gain ? "gain" : "neutral",
                Why = gpDrop ? $"gross profit {gpVs:0}% vs expected" : unitsDrop ? $"units {unitsVs:0}% vs expected without a gross-profit gain" : null,
            };

        figures = figures with
        {
            PlaceboWindows = placeboGp.Count,
            PlaceboGpMinPct = placeboGp.Count > 0 ? placeboGp.Min() : null,
            PlaceboGpMaxPct = placeboGp.Count > 0 ? placeboGp.Max() : null,
            PlaceboUnitsMinPct = placeboUnits is { Count: > 0 } ? placeboUnits.Min() : null,
        };
        if (placeboGp.Count < o.MinPlaceboWindows)
            return figures with { Verdict = "unclear", Why = $"only {placeboGp.Count} measurable no-change fortnights before it: a drop can't be told from normal variation" };
        if (gpDrop && gpVs < figures.PlaceboGpMinPct)
            return figures with { Verdict = "drop", Why = $"gross profit {gpVs:0}% vs expected, worse than all {placeboGp.Count} no-change fortnights before (worst {figures.PlaceboGpMinPct:0}%)" };
        if (unitsDrop && unitsVs < figures.PlaceboUnitsMinPct)
            return figures with { Verdict = "drop", Why = $"units {unitsVs:0}% vs expected without a gross-profit gain, worse than all {placeboGp.Count} no-change fortnights before (worst {figures.PlaceboUnitsMinPct:0}%)" };
        if (gain && gpVs > figures.PlaceboGpMaxPct)
            return figures with { Verdict = "gain", Why = $"gross profit {gpVs:0}% vs expected, better than all {placeboGp.Count} no-change fortnights before" };
        return figures with { Verdict = "neutral", Why = $"within the normal variation of the {placeboGp.Count} no-change fortnights before ({figures.PlaceboGpMinPct:0}% to {figures.PlaceboGpMaxPct:0}% gross profit vs expected)" };
    }

    private static string Describe(OutcomeFigures f, WindowPair w, string holdouts) =>
        $"{w.DaysBefore} normal days before vs {w.DaysAfter} after: GP/day {f.GpPerDayBefore:0} -> {f.GpPerDayAfter:0} EUR " +
        $"(expected {N(f.ExpectedGpPerDayAfter)} with the holdout's ratio {N(f.HoldoutGpRatio, "0.00")}, {N(f.GpVsExpectedPct, "0.0")}%), " +
        $"units/day {f.UnitsPerDayBefore:0.0} -> {f.UnitsPerDayAfter:0.0} (expected {N(f.ExpectedUnitsPerDayAfter, "0.0")}, {N(f.UnitsVsExpectedPct, "0.0")}%). " +
        $"Holdout: {holdouts}. Booked COGS, VAT excl., campaign days left out. Verdict {f.Verdict}{(f.Why is null ? "" : ": " + f.Why)}.";

    /// <summary>Restores the rule's floor and target to what they were before the decision, when the rule still holds the
    /// decision's values, and pauses the band × category. Returns true when it rolled back.</summary>
    private async Task<bool> RollBackAsync(AgentDecision d, AgentOutcome x, OutcomeFigures f, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var stop = await _killSwitch.GetAsync(ct);
        if (stop.Stopped)
        {
            x.Action = "rollback-waits";
            x.Evidence = Truncate($"{x.Evidence} Rollback due, but the kill switch is pulled ({stop.Reason}): it waits.");
            return false;
        }

        var rule = await _tool.BandCategoryRules.SingleOrDefaultAsync(r => r.LayerId == d.LayerId && r.BandId == d.BandId && r.Category == d.Category, ct);
        if (rule is null || Math.Abs((rule.MarginFloorPct ?? -1m) - d.ProposedFloorPct) > 0.005m
                         || Math.Abs((rule.TargetMarginPct ?? -1m) - (d.ProposedTargetPct ?? -1m)) > 0.005m)
        {
            x.Action = "rollback-skipped";
            x.ActionUtc = now;
            x.Evidence = Truncate($"{x.Evidence} Rollback skipped: the rule changed since this decision; the later change is measured on its own.");
            return false;
        }

        var reason = $"Automatic rollback of decision #{d.Id} ({d.Category}, band {d.BandId}): {f.Why}. Floor {d.ProposedFloorPct:0.##}% -> " +
                     $"{d.CurrentFloorPct:0.##}%, target {d.ProposedTargetPct:0.##}% -> {d.CurrentTargetPct:0.##}%.";
        _tool.AuditLog.Add(new AuditLogEntry
        {
            LayerId = d.LayerId, TimestampUtc = now, UserName = Source, Category = AgentModeStore.AuditCategory,
            Action = "Per-category rule rolled back (outcome drop)", EntityType = nameof(BandCategoryRule), EntityId = $"{d.BandId} {d.Category}",
            OldValue = $"floor {rule.MarginFloorPct:0.##}, target {rule.TargetMarginPct:0.##}",
            NewValue = $"floor {d.CurrentFloorPct:0.##}, target {d.CurrentTargetPct:0.##}",
        });
        rule.MarginFloorPct = d.CurrentFloorPct;
        rule.TargetMarginPct = d.CurrentTargetPct;
        rule.UpdatedUtc = now;
        rule.UpdatedBy = Source;
        await _tool.SaveChangesAsync(ct);

        await _db.SaveChangesAsync(CancellationToken.None);      // the outcome's id, for the pause
        _db.CategoryPauses.Add(new CategoryPause
        {
            LayerId = d.LayerId, BandId = d.BandId, Category = d.Category, OutcomeId = x.Id, PausedUtc = now, Reason = Truncate(reason, 1000),
        });
        x.Action = "rolled-back";
        x.ActionUtc = now;
        _logger.LogWarning("{Reason}", reason);
        return true;
    }

    /// <summary>The daily series a measurement and its no-change windows need: one query for the row and the holdout.</summary>
    private Task<DailySeries> LoadAsync(AgentDecision d, DateOnly liveDay, CancellationToken ct)
    {
        var o = _options.Outcomes;
        var from = liveDay.AddDays(-7 * (2 + o.PlaceboWindows) - o.WindowDays);
        var to = liveDay.AddDays(1 + o.WindowDays);
        return (DailySource ?? LoadDailyAsync)(d, from, to, ct);
    }

    private async Task<DailySeries> LoadDailyAsync(AgentDecision d, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var holdouts = _options.Limits.Holdouts.TryGetValue(d.LayerId.ToString(CultureInfo.InvariantCulture), out var h) ? h : new List<string>();
        var days = await _reader.GetDailyRowAndHoldoutAsync(d.LayerId, d.BandId, d.Category, holdouts, from, to, ct);
        var campaignDays = new HashSet<DateOnly>();
        foreach (var c in await _reader.GetCampaignWindowsAsync(d.LayerId, from, to, ct))
            for (var day = c.StartDate; day <= c.EndDate; day = day.AddDays(1)) campaignDays.Add(day);
        return new DailySeries(days, campaignDays, holdouts.Count == 0 ? "none configured" : string.Join(", ", holdouts));
    }

    private static decimal? R(decimal? v, int digits) => v is { } x ? Math.Round(x, digits) : null;
    private static string N(decimal? v, string format = "0") => v is { } x ? x.ToString(format, CultureInfo.InvariantCulture) : "n/a";
    private static string Truncate(string s, int max = 2000) => s.Length <= max ? s : s[..max];
}
