using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PricingTool.Agent;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Operations;
using PricingTool.Agent.Storage;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;
using PricingTool.NopIntegration.CostWatch;

namespace PricingTool.Tests;

/// <summary>
/// The cost watcher (plan section 5, "CostWatcher"): a cost rise that puts the live price under the floor is raised to the
/// floor within one watch; the three cases where it doesn't just raise (a data-error jump, a cost above the shelf, a
/// cheaper team discount) are held for a person; each action is a CostWatcher decision; the kill switch stops it.
/// </summary>
public class CostWatcherTests : IDisposable
{
    private readonly AgentDbContext _ai = new(new DbContextOptionsBuilder<AgentDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly PricingToolDbContext _tool = new(new DbContextOptionsBuilder<PricingToolDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly FakePublisher _publisher = new();
    private readonly Switch _switch = new();
    private int _layer;

    public CostWatcherTests()
    {
        var layer = new Layer { Brand = "Gjirafa50", CountryCode = "KS", DisplayName = "Gjirafa50 — Kosovo", OperationalDatabase = "GjirafaEcommerce", StoreId = 2, Currency = "EUR", IsActive = true };
        _tool.Layers.Add(layer);
        _tool.SaveChanges();
        _layer = layer.Id;
        _tool.PriceBands.Add(new PriceBand { LayerId = _layer, Name = "50-100", MinPrice = 0, MaxPrice = 500, MarginFloorPct = 10, RoundingEnabled = false });
        var run = new PricingRun { LayerId = _layer, Status = RunStatus.Succeeded, StartedUtc = DateTime.UtcNow.AddHours(-1), TriggeredBy = "daily cycle" };
        _tool.PricingRuns.Add(run);
        _tool.SaveChanges();
        // sku, cost, current price, anchor
        foreach (var (sku, cost, current, anchor) in new[] { ("RISE", 60m, 75m, 100m), ("JUMP", 60m, 75m, 100m), ("SHELF", 60m, 70m, 72m),
                     ("TEAM", 60m, 70m, 100m), ("FINE", 60m, 90m, 100m), ("DOWN", 60m, 70m, 100m) })
            _tool.DailySnapshots.Add(new DailySnapshot { LayerId = _layer, SnapshotDate = run.StartedUtc.Date, Sku = sku, Pptcv = cost, CurrentPrice = current, AnchorPrice = anchor, LocalWarehouseStock = 5 });
        _tool.SaveChanges();
    }

    public void Dispose()
    {
        _ai.Dispose();
        _tool.Dispose();
    }

    private sealed class FakePublisher : ICostPublisher
    {
        public List<(string Sku, decimal Price)> Raised { get; } = new();
        public List<string> Ended { get; } = new();
        public Task<(long BatchId, string Status, bool CheaperTeamDeal)> RaiseAsync(int layerId, string sku, decimal price, decimal shelfPrice, CancellationToken ct)
        {
            Raised.Add((sku, price));
            return Task.FromResult((100L + Raised.Count, "Published", sku == "TEAM"));
        }
        public Task<long?> EndOurDiscountAsync(int layerId, string sku, CancellationToken ct)
        {
            Ended.Add(sku);
            return Task.FromResult<long?>(9);
        }
    }

    private sealed class Switch : IKillSwitch
    {
        public KillSwitchState State { get; set; } = KillSwitchState.Running;
        public Task<KillSwitchState> GetAsync(CancellationToken ct = default) => Task.FromResult(State);
    }

    private CostWatcher Watcher() => new(_ai, _tool, new BandConfigProvider(_tool), _publisher, _switch, Options.Create(new CostWatcherOptions()),
        NullLogger<CostWatcher>.Instance);

    private void Observe(string sku, decimal cost, string source = "[INJECTED-TEST] unit test") =>
        _ai.CostObservations.Add(new CostObservation { LayerId = _layer, Sku = sku, Pptcv = cost, ObservedUtc = DateTime.UtcNow, Source = source });

    [Fact]
    public async Task A_rise_under_the_floor_is_raised_and_the_three_other_cases_wait_for_a_person()
    {
        Observe("RISE", 70m, "replica");      // floor price 70 / 0.9 = 77.78 > live 75: raise
        Observe("JUMP", 120m, "replica");     // +100 %: a data error
        Observe("SHELF", 70m, "replica");     // floor price 77.78 above max(anchor 72, current 70)
        Observe("TEAM", 66m, "replica");      // floor price 73.33 > 70, but a cheaper team deal wins
        Observe("FINE", 63m, "replica");      // floor price 70 < live 90
        Observe("DOWN", 55m, "replica");      // cost fell
        Observe("GHOST", 50m, "replica");     // not in the run
        await _ai.SaveChangesAsync();

        var r = await Watcher().WatchAsync();

        Assert.Equal((7, 1, 2, 1, 3), (r.Observed, r.Raised, r.Held, r.Ended, r.NoAction));
        var outcomes = await _ai.CostObservations.ToDictionaryAsync(o => o.Sku, o => o.Outcome);
        Assert.Equal("raised", outcomes["RISE"]);
        Assert.Equal("held-data-error", outcomes["JUMP"]);
        Assert.Equal("ended-our-discount", outcomes["SHELF"]);
        Assert.Equal("held-team-discount", outcomes["TEAM"]);
        Assert.Equal(("above-floor", "not-a-rise", "unknown-sku"), (outcomes["FINE"], outcomes["DOWN"], outcomes["GHOST"]));
        Assert.Equal(new[] { ("RISE", 77.78m), ("TEAM", 73.34m) }, _publisher.Raised);
        Assert.Equal(new[] { "SHELF" }, _publisher.Ended);

        var decisions = await _ai.AgentDecisions.ToListAsync();
        Assert.All(decisions, d => Assert.Equal(CostWatcher.Source, d.Source));
        Assert.Equal(1, decisions.Count(d => d.Status == DecisionStatus.Applied && d.Action == "raise-to-floor"));
        Assert.Equal(new[] { "cost-above-shelf", "data-error-cost-jump", "team-discount" }, decisions.Where(d => d.Status == DecisionStatus.Held).Select(d => d.Limit!).OrderBy(l => l));
        var pass = await _ai.AgentPasses.SingleAsync();
        Assert.Equal((CostWatcher.Source, false, 1, 3), (pass.Source, pass.IsTest, pass.Applied, pass.Held));
        var inbox = await new ExceptionsInbox(_ai, _tool).LoadAsync();
        Assert.Equal((3, 0), (inbox.CostHolds.Count, inbox.Decisions.Count));     // holds are listed apart, never approvable as floor decisions

        // The next watch sees the price it published: the same cost again needs nothing more.
        Observe("RISE", 70m, "replica");
        await _ai.SaveChangesAsync();
        Assert.Equal(1, (await Watcher().WatchAsync()).NoAction);
        Assert.Single(_publisher.Raised, x => x.Sku == "RISE");
    }

    [Fact]
    public async Task With_the_kill_switch_pulled_observations_wait()
    {
        Observe("RISE", 70m);
        await _ai.SaveChangesAsync();
        _switch.State = new KillSwitchState(true, "pulled (test)");

        var r = await Watcher().WatchAsync();

        Assert.Equal((0, "kill switch: pulled (test)"), (r.Observed, r.Skipped));
        Assert.Null((await _ai.CostObservations.SingleAsync()).ProcessedUtc);
        Assert.Empty(_publisher.Raised);

        _switch.State = KillSwitchState.Running;
        Assert.Equal(1, (await Watcher().WatchAsync()).Raised);
        Assert.True((await _ai.AgentPasses.SingleAsync()).IsTest);             // an injected observation makes a test pass
    }
}
