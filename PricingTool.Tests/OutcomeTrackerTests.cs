using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PricingTool.Agent;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Operations;
using PricingTool.Agent.Storage;
using PricingTool.Agent.Tools;
using PricingTool.Data;
using PricingTool.Data.Entities;

namespace PricingTool.Tests;

/// <summary>
/// The outcome tracker: a change is measured only once its prices went live (a pushed export of a run that priced
/// with its rule), against the holdout's season; a drop rolls the rule back and pauses the band × category, and the
/// pause holds the pricing agent's next change there for a person.
/// </summary>
public class OutcomeTrackerTests : IDisposable
{
    private static readonly DateTime Applied = new(2026, 11, 2, 7, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime Pushed = new(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly PushDay = DateOnly.FromDateTime(Pushed);
    private readonly AgentDbContext _ai = new(new DbContextOptionsBuilder<AgentDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly PricingToolDbContext _tool = new(new DbContextOptionsBuilder<PricingToolDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose()
    {
        _ai.Dispose();
        _tool.Dispose();
    }

    private static readonly OutcomeOptions O = new();
    private static WindowSales S(decimal units, decimal gp) => new(units, gp);

    [Fact]
    public void Judge_compares_against_the_holdouts_season_and_reads_a_units_fall_with_more_profit_as_the_raise_working()
    {
        // Holdout flat: expected = before. GP/day 100 -> 75 is -25 %: a drop.
        var drop = OutcomeTracker.Judge(S(140, 1400), S(140, 1050), S(700, 7000), S(700, 7000), 14, 14, O);
        Assert.Equal(("drop", -25m), (drop.Verdict, Math.Round(drop.GpVsExpectedPct!.Value)));

        // Holdout -30 % (the season): GP/day 100 -> 75 is above the expected 70: a gain, not a drop.
        var season = OutcomeTracker.Judge(S(140, 1400), S(140, 1050), S(700, 7000), S(490, 4900), 14, 14, O);
        Assert.Equal("gain", season.Verdict);
        Assert.Equal(70m, Math.Round(season.ExpectedGpPerDayAfter!.Value));

        // Units -30 % but GP +10 %: fewer units at a better margin.
        Assert.Equal("gain", OutcomeTracker.Judge(S(140, 1400), S(98, 1540), S(700, 7000), S(700, 7000), 14, 14, O).Verdict);
        // Units -30 % with GP flat: a drop.
        Assert.Equal("drop", OutcomeTracker.Judge(S(140, 1400), S(98, 1400), S(700, 7000), S(700, 7000), 14, 14, O).Verdict);
        Assert.Equal("neutral", OutcomeTracker.Judge(S(140, 1400), S(140, 1420), S(700, 7000), S(700, 7000), 14, 14, O).Verdict);
        Assert.Equal("thin", OutcomeTracker.Judge(S(20, 400), S(20, 100), S(700, 7000), S(700, 7000), 14, 14, O).Verdict);
    }

    [Fact]
    public void A_drop_must_also_be_worse_than_every_no_change_window_before_it()
    {
        decimal[] Noisy(params decimal[] v) => v;
        var calm = Noisy(-8, -5, -3, 0, 2, 4, 6, 9);
        var noisy = Noisy(-60, -35, -10, 0, 12, 30, 46, 217);       // the spread seen on real rule groups, Mar-Sep 2026

        var outside = OutcomeTracker.Judge(S(140, 1400), S(140, 1050), S(700, 7000), S(700, 7000), 14, 14, O, calm, calm);
        Assert.Equal(("drop", 8, -8m), (outside.Verdict, outside.PlaceboWindows, outside.PlaceboGpMinPct!.Value));

        var inside = OutcomeTracker.Judge(S(140, 1400), S(140, 1050), S(700, 7000), S(700, 7000), 14, 14, O, noisy, noisy);
        Assert.Equal("neutral", inside.Verdict);                    // -25 % is ordinary here: no rollback
        Assert.Contains("normal variation", inside.Why);

        var tooLittle = OutcomeTracker.Judge(S(140, 1400), S(140, 700), S(700, 7000), S(700, 7000), 14, 14, O, calm[..5], calm[..5]);
        Assert.Equal("unclear", tooLittle.Verdict);                 // -50 %, but 5 windows can't say it's not noise

        var gainInside = OutcomeTracker.Judge(S(140, 1400), S(140, 1680), S(700, 7000), S(700, 7000), 14, 14, O, noisy, noisy);
        Assert.Equal("neutral", gainInside.Verdict);                // +20 % is inside the noise too: no gain claimed
    }

    private sealed class Clock : TimeProvider
    {
        public DateTime Now { get; set; }
        public override DateTimeOffset GetUtcNow() => new(Now);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class Switch : IKillSwitch
    {
        public KillSwitchState State { get; set; } = KillSwitchState.Running;
        public Task<KillSwitchState> GetAsync(CancellationToken ct = default) => Task.FromResult(State);
    }

    /// <summary>The publisher's restore of raised regular prices, recorded.</summary>
    private sealed class Restorer : IRaiseRestorer
    {
        public List<(int Layer, int Band, string Category, DateTime Since)> Calls { get; } = new();
        public Task<string> RestoreRaisesAsync(int layerId, int bandId, string category, DateTime sinceUtc, string reason, CancellationToken ct = default)
        {
            Calls.Add((layerId, bandId, category, sinceUtc));
            return Task.FromResult("Regular prices raised in band 26 × Aksesorë since 2026-10-01: 3 restored from the publish audit (batch #12).");
        }
    }

    private readonly Restorer _restorer = new();

    private (OutcomeTracker Tracker, Clock Clock, Switch Switch) Tracker(WindowSales before, WindowSales after)
    {
        var clock = new Clock { Now = Pushed.AddDays(16) };
        var sw = new Switch();
        var options = Options.Create(new AgentOptions { Limits = new SafetyLimitOptions { Holdouts = { ["4"] = new() { "Gaming" } } } });
        var tracker = new OutcomeTracker(_ai, _tool, new PricingDataReader(new AgentDataSource("unused")), sw, options,
            NullLogger<OutcomeTracker>.Instance, clock, _restorer)
        {
            // A steady daily series (so every no-change window reads 0 %) that changes from the day after the push:
            // per day the row sells before/14 until the push and after/14 from the next day; the holdout is flat.
            DailySource = (_, from, to, _) => Task.FromResult(new DailySeries(
                Enumerable.Range(0, to.DayNumber - from.DayNumber).Select(i => from.AddDays(i)).Select(day =>
                {
                    var w = day > PushDay ? after : before;
                    return new DaySales(day, w.Units / 14, w.GrossProfit / 14, 50m, 500m);
                }).ToList(),
                new HashSet<DateOnly>(), "Gaming")),
        };
        return (tracker, clock, sw);
    }

    private void Seed(bool pushed = true)
    {
        _ai.AgentPasses.Add(new AgentPass { Id = 2, LayerId = 4, Source = "Alpha", Mode = "Autonomous", StartedUtc = Applied, Status = "Succeeded" });
        _ai.AgentDecisions.Add(new AgentDecision
        {
            Id = 10, PassId = 2, LayerId = 4, BandId = 26, Category = "Aksesorë", Source = "Alpha", Action = "raise", Status = DecisionStatus.Applied,
            CurrentFloorPct = 13m, ProposedFloorPct = 14m, CurrentTargetPct = 23m, ProposedTargetPct = 24m, Reason = "test", EvidenceRefs = "[]",
            CreatedUtc = Applied, AppliedUtc = Applied,
        });
        _tool.BandCategoryRules.Add(new BandCategoryRule { LayerId = 4, BandId = 26, Category = "Aksesorë", MarginFloorPct = 14m, TargetMarginPct = 24m, UpdatedBy = "Alpha", AgentDecisionId = 10 });
        _tool.PricingRuns.Add(new PricingRun { Id = 9001, LayerId = 4, Status = RunStatus.Succeeded, StartedUtc = Applied.AddMinutes(10), TriggeredBy = DailyCycle.TriggeredBy });
        _tool.PricingRunCategoryRules.Add(new PricingRunCategoryRule { PricingRunId = 9001, LayerId = 4, BandId = 26, Category = "Aksesorë", MarginFloorPct = 14m, TargetMarginPct = 24m, Skus = 100 });
        _tool.SaveChanges();
        _ai.ExportBatches.Add(new ExportBatch
        {
            Id = 5, LayerId = 4, RunId = 9001, CheckId = 3, FilePath = "x.csv", Rows = 100, CreatedUtc = Applied.AddMinutes(30),
            PushedUtc = pushed ? Pushed : null, PushedBy = pushed ? "team" : null, ApprovalId = pushed ? 77 : null,
        });
        _ai.SaveChanges();
    }

    [Fact]
    public async Task A_drop_rolls_the_rule_back_pauses_the_category_and_the_next_change_there_waits_for_a_person()
    {
        Seed();
        var (tracker, _, _) = Tracker(S(140, 1400), S(140, 1050));

        var r = await tracker.UpdateAsync();

        Assert.Equal((1, 1, 1), (r.Decisions, r.Measured, r.RolledBack));
        var x = await _ai.AgentOutcomes.SingleAsync();
        Assert.Equal(("drop", "rolled-back", OutcomeStatus.Provisional, 5L), (x.Verdict, x.Action, x.Status, x.LiveExportBatchId));
        Assert.Equal((100m, 75m), (x.GpPerDayBefore, x.GpPerDayAfter));
        var rule = await _tool.BandCategoryRules.SingleAsync();
        Assert.Equal((13m, 23m, OutcomeTracker.Source), (rule.MarginFloorPct, rule.TargetMarginPct, rule.UpdatedBy));
        var pause = await _ai.CategoryPauses.SingleAsync();
        Assert.Equal((26, "Aksesorë", x.Id), (pause.BandId, pause.Category, pause.OutcomeId));
        Assert.Contains(await _tool.AuditLog.ToListAsync(), a => a.UserName == OutcomeTracker.Source && a.EntityId == "26 Aksesorë");
        Assert.Equal(1, (await new ExceptionsInbox(_ai, _tool).LoadAsync()).WaitingCount);
        Assert.Equal((4, 26, "Aksesorë", Applied), Assert.Single(_restorer.Calls));     // its raises go back too, from the audit
        Assert.Contains("3 restored from the publish audit", x.Evidence);

        Assert.Equal(1, (await tracker.UpdateAsync()).Measured);                  // re-measured, never rolled back twice
        Assert.Single(await _ai.CategoryPauses.ToListAsync());

        var verdict = SafetyLimits.Evaluate(new LimitInput(4, "Aksesorë", false, false, 13m, 23m, 13m, 23m, "raise", 14m, 24m, IsPaused: true),
            new SafetyLimitOptions());
        Assert.Equal((DecisionStatus.Held, "paused"), (verdict.Status, verdict.Limit));
    }

    [Fact]
    public async Task With_the_kill_switch_pulled_the_rollback_waits()
    {
        Seed();
        var (tracker, _, sw) = Tracker(S(140, 1400), S(140, 1050));
        sw.State = new KillSwitchState(true, "pulled (test)");

        var r = await tracker.UpdateAsync();

        Assert.Equal((0, 1), (r.RolledBack, r.RollbacksWaiting));
        Assert.Equal("rollback-waits", (await _ai.AgentOutcomes.SingleAsync()).Action);
        Assert.Equal(14m, (await _tool.BandCategoryRules.SingleAsync()).MarginFloorPct);
        Assert.Empty(await _ai.CategoryPauses.ToListAsync());
        Assert.Empty(_restorer.Calls);                                            // no price written while the switch is pulled

        sw.State = KillSwitchState.Running;
        Assert.Equal(1, (await tracker.UpdateAsync()).RolledBack);               // released: the rollback goes ahead
    }

    [Fact]
    public async Task A_change_no_pushed_export_priced_with_is_not_live_and_not_measured()
    {
        Seed(pushed: false);
        var (tracker, _, _) = Tracker(S(140, 1400), S(140, 1050));

        var r = await tracker.UpdateAsync();

        Assert.Equal((1, 0, 0), (r.NotLive, r.Measured, r.RolledBack));
        var x = await _ai.AgentOutcomes.SingleAsync();
        Assert.Equal((OutcomeStatus.NotLive, (string?)null), (x.Status, x.Verdict));
        Assert.Equal(14m, (await _tool.BandCategoryRules.SingleAsync()).MarginFloorPct);
    }

    [Fact]
    public async Task A_live_publish_with_the_rules_prices_counts_as_live_and_a_sandbox_one_never_does()
    {
        Seed(pushed: false);
        void Publish(long id, string target, string status = "Published", DateTime? rolledBack = null) => _ai.PublishBatches.Add(new PublishBatch
        {
            Id = id, LayerId = 4, Db = "GjirafaEcommerce", StoreId = 2, Target = target, ShopSchema = target == "Live" ? "dbo" : "shop_sandbox", Kind = "Regular",
            EngineRunId = 9001, Status = status, StartedUtc = Pushed.AddMinutes(-5), FinishedUtc = Pushed, RolledBackUtc = rolledBack,
        });
        Publish(31, "Sandbox");                                                          // the hackathon's: never reaches a customer
        Publish(32, "Live", "RolledBack", Pushed.AddHours(1));                           // undone
        await _ai.SaveChangesAsync();
        var (tracker, clock, _) = Tracker(S(140, 1400), S(140, 1420));
        clock.Now = Pushed.AddDays(5);

        Assert.Equal(1, (await tracker.UpdateAsync()).NotLive);

        Publish(33, "Live");
        await _ai.SaveChangesAsync();
        Assert.Equal(1, (await tracker.UpdateAsync()).Waiting);                          // live from the publish, its window still open
        var x = await _ai.AgentOutcomes.SingleAsync();
        Assert.Equal((Pushed, (long?)33, (long?)null), (x.LiveFromUtc!.Value, x.LivePublishBatchId, x.LiveExportBatchId));
        Assert.Contains("live publish batch #33", x.Evidence);
    }

    [Fact]
    public async Task A_live_change_waits_for_its_window_then_is_measured()
    {
        Seed();
        var (tracker, clock, _) = Tracker(S(140, 1400), S(140, 1420));
        clock.Now = Pushed.AddDays(5);

        Assert.Equal(1, (await tracker.UpdateAsync()).Waiting);
        Assert.Equal(OutcomeStatus.Waiting, (await _ai.AgentOutcomes.SingleAsync()).Status);

        clock.Now = Pushed.AddDays(40);
        Assert.Equal(1, (await tracker.UpdateAsync()).Measured);
        var x = await _ai.AgentOutcomes.SingleAsync();
        Assert.Equal((OutcomeStatus.Final, "neutral", (string?)null), (x.Status, x.Verdict, x.Action));
    }
}
