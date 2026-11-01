using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Operations;
using PricingTool.Agent.Storage;
using PricingTool.NopIntegration;
using Xunit;

namespace PricingTool.Tests;

/// <summary>"Run week": the loop's steps in order, and the things that stop it (a block, the kill switch, a Live target).</summary>
public class RunWeekTests
{
    private sealed class Steps : IRunWeekSteps
    {
        public List<string> Calls { get; } = new();
        public string CheckStatus { get; set; } = StepStatus.Succeeded;

        public Task<RunWeekStepResult> SizeCheckAsync(CancellationToken ct)
        {
            Calls.Add("size");
            return Task.FromResult(new RunWeekStepResult(StepStatus.Succeeded, "5.6 GB"));
        }

        public Task<RunWeekStepResult> AlphaAsync(int layerId, CancellationToken ct)
        {
            Calls.Add("alpha");
            return Task.FromResult(new RunWeekStepResult(StepStatus.Succeeded, "pass #7: 2 changed", "band 25 Aksesorë: raise", 7));
        }

        public Task<RunWeekStepResult> EngineAsync(int layerId, string triggeredBy, CancellationToken ct)
        {
            Calls.Add("engine");
            Assert.StartsWith(RunWeekLoop.TriggeredBy, triggeredBy);                                // never taken for the daily cycle's run
            return Task.FromResult(new RunWeekStepResult(StepStatus.Succeeded, "run #1200", null, 1200));
        }

        public Task<RunWeekStepResult> CheckAsync(long runId, CancellationToken ct)
        {
            Calls.Add("check");
            return Task.FromResult(new RunWeekStepResult(CheckStatus, CheckStatus == StepStatus.Failed ? "check #9: verdict block" : "check #9: verdict warn", null, 9));
        }

        public Task<(IReadOnlyList<PublishItem> Items, string Detail)> CheckedPricesAsync(long runId, CancellationToken ct)
        {
            Calls.Add("prices");
            return Task.FromResult(((IReadOnlyList<PublishItem>)new[] { new PublishItem("S1", 9.50m, 10.50m), new PublishItem("S2", 19.50m, 21.50m) }, "2 checked prices"));
        }

        public Task<PublishOutcome> PublishAsync(int layerId, long runId, IReadOnlyList<PublishItem> items, CancellationToken ct)
        {
            Calls.Add("publish");
            return Task.FromResult(new PublishOutcome(55, "Published", 2, 0, 0, 0, 2, 0, false, Array.Empty<PublishException>()));
        }

        public Task<RunWeekStepResult> PriceListAsync(long runWeekId, int layerId, long batchId, IReadOnlyList<PublishItem> onShelf, CancellationToken ct)
        {
            Calls.Add("price-list");
            Assert.Equal(55, batchId);
            Assert.Equal(new[] { "S1", "S2" }, onShelf.Select(i => i.Sku));                      // nothing was left to a cheaper deal
            return Task.FromResult(new RunWeekStepResult(StepStatus.Succeeded, $"2 prices: {RunWeekSteps.PriceListPath(runWeekId)}"));
        }

        public Task<RunWeekStepResult> ReportAsync(long batchId, CancellationToken ct)
        {
            Calls.Add("report");
            Assert.Equal(55, batchId);                                                               // the batch just published
            return Task.FromResult(new RunWeekStepResult(StepStatus.Succeeded, "report #3", "Two prices on the shelf.", 3));
        }
    }

    private sealed class Switch : IKillSwitch
    {
        public bool Stopped { get; set; }
        public Task<KillSwitchState> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(Stopped ? new KillSwitchState(true, "pulled by a manager") : KillSwitchState.Running);
    }

    private static AgentDbContext Db() =>
        new(new DbContextOptionsBuilder<AgentDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(RunWeek Week, List<RunWeekStep> Rows)> RunAsync(AgentDbContext db, Steps steps, Switch ks, string target = PublisherOptions.SandboxTarget)
    {
        var loop = new RunWeekLoop(db, steps, ks, Options.Create(new PublisherOptions { Target = target }), NullLogger<RunWeekLoop>.Instance);
        var id = await loop.StartAsync(4, "manager@test");
        var week = await loop.RunAsync(id);
        return (week, await db.RunWeekSteps.Where(s => s.RunWeekId == id).OrderBy(s => s.Id).ToListAsync());
    }

    [Fact]
    public async Task The_loop_runs_alpha_engine_checker_publish_and_the_read_back_in_order()
    {
        await using var db = Db();
        var steps = new Steps();

        var (week, rows) = await RunAsync(db, steps, new Switch());

        Assert.Equal(new[] { "size", "alpha", "engine", "check", "prices", "publish", "report", "price-list" }, steps.Calls);
        Assert.Equal(new[] { "size-check", "alpha", "engine", "check", "publish", "verify", "report", "price-list" }, rows.Select(r => r.Step));
        Assert.All(rows, r => Assert.Equal(StepStatus.Succeeded, r.Status));
        Assert.Equal(("Succeeded", 7L, 1200L, 9L, 55L), (week.Status, week.PassId!.Value, week.EngineRunId!.Value, week.CheckId!.Value, week.PublishBatchId!.Value));
        Assert.Contains("2 of 2 read back", rows.Single(r => r.Step == "verify").Detail);
        Assert.Equal(("report #3", "Two prices on the shelf."), (rows[^2].Detail, rows[^2].Why));  // the run's report, then...
        Assert.Equal($"2 prices: /Run/PriceList/{week.Id}", rows[^1].Detail);                      // ...its price list, last
        Assert.Contains("Aksesorë", rows[1].Why);                                                  // the reasons are kept with the step
    }

    [Fact]
    public async Task A_blocked_check_stops_the_publish()
    {
        await using var db = Db();
        var steps = new Steps { CheckStatus = StepStatus.Failed };

        var (week, rows) = await RunAsync(db, steps, new Switch());

        Assert.DoesNotContain("publish", steps.Calls);
        Assert.Equal(new[] { "size-check", "alpha", "engine", "check" }, rows.Select(r => r.Step));
        Assert.Equal("Stopped", week.Status);
        Assert.Contains("Nothing was published", week.Outcome);
    }

    [Fact]
    public async Task A_pulled_kill_switch_stops_it_before_alpha()
    {
        await using var db = Db();
        var steps = new Steps();

        var (week, rows) = await RunAsync(db, steps, new Switch { Stopped = true });

        Assert.Empty(steps.Calls);
        Assert.Equal(("kill-switch", StepStatus.Failed), (rows.Single().Step, rows.Single().Status));
        Assert.Equal("Stopped", week.Status);
        Assert.Contains("before alpha", week.Outcome);
    }

    [Fact]
    public async Task It_waits_while_the_daily_cycle_runs()
    {
        await using var db = Db();
        db.DailyCycleSteps.Add(new DailyCycleStep { CycleDate = DateOnly.FromDateTime(DateTime.Now), Step = "engine-L4", Status = StepStatus.Running, StartedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var steps = new Steps();

        var (week, rows) = await RunAsync(db, steps, new Switch());

        Assert.Empty(steps.Calls);
        Assert.Equal("daily-cycle", rows.Single().Step);
        Assert.Equal("Stopped", week.Status);
    }

    [Fact]
    public async Task A_live_target_makes_the_publish_refuse()
    {
        await using var db = Db();
        var steps = new Steps();

        var (week, rows) = await RunAsync(db, steps, new Switch(), PublisherOptions.LiveTarget);

        Assert.DoesNotContain("publish", steps.Calls);
        Assert.Equal(("publish", StepStatus.Failed), (rows[^1].Step, rows[^1].Status));
        Assert.Contains("Refused", rows[^1].Detail);
        Assert.Equal("Stopped", week.Status);
    }

    private sealed class NoHoldouts : PricingTool.Agent.Checker.IHoldoutLookup
    {
        public Task<IReadOnlySet<long>> HoldoutProposalIdsAsync(long runId, int layerId, IReadOnlyCollection<string> categories, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<long>>(new HashSet<long>());
    }

    [Fact]
    public async Task The_daily_cycles_sandbox_publish_refuses_a_live_target_and_an_unchecked_run()
    {
        var tool = new PricingTool.Data.PricingToolDbContext(new DbContextOptionsBuilder<PricingTool.Data.PricingToolDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var db = Db();
        var exporter = new PricingTool.Agent.Checker.CheckedExport(tool, db, new NoHoldouts(), Options.Create(new PricingTool.Agent.AgentOptions()));

        var live = await new CheckedPricePublisher(exporter, null!, db, Options.Create(new PublisherOptions { Target = PublisherOptions.LiveTarget }))
            .PublishCheckedAsync(4, 1105);
        var notChecked = await new CheckedPricePublisher(exporter, null!, db, Options.Create(new PublisherOptions())).PublishCheckedAsync(4, 1105);

        Assert.Equal(StepStatus.Skipped, live.Status);
        Assert.Contains("only to the shop sandbox", live.Detail);
        Assert.Equal(StepStatus.Skipped, notChecked.Status);
        Assert.Contains("has no check", notChecked.Detail);
    }

    [Fact]
    public async Task The_daily_cycles_publish_skips_a_run_older_than_one_already_on_the_sandbox()
    {
        var db = Db();
        db.PublishBatches.Add(new PublishBatch { LayerId = 4, Db = "GjirafaEcommerce", StoreId = 2, Target = "Sandbox", ShopSchema = "shop_sandbox_GjirafaEcommerce",
            Kind = PublishKinds.Regular, EngineRunId = 1106, Status = "Published", StartedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var tool = new PricingTool.Data.PricingToolDbContext(new DbContextOptionsBuilder<PricingTool.Data.PricingToolDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var exporter = new PricingTool.Agent.Checker.CheckedExport(tool, db, new NoHoldouts(), Options.Create(new PricingTool.Agent.AgentOptions()));

        var (status, detail) = await new CheckedPricePublisher(exporter, null!, db, Options.Create(new PublisherOptions())).PublishCheckedAsync(4, 1103);

        Assert.Equal(StepStatus.Skipped, status);
        Assert.Contains("run #1106 is already on the sandbox", detail);
    }
}
