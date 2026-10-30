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
                     ("TEAM", 60m, 70m, 100m), ("FINE", 60m, 90m, 100m), ("DOWN", 60m, 70m, 100m), ("DEAD", 60m, 50m, 100m), ("LOCK", 60m, 70m, 100m),
                     ("HOLD", 60m, 70m, 100m), ("TINY", 60m, 70m, 80m) })
            _tool.DailySnapshots.Add(new DailySnapshot { LayerId = _layer, SnapshotDate = run.StartedUtc.Date, Sku = sku, Pptcv = cost, CurrentPrice = current,
                AnchorPrice = anchor, OldPrice = anchor, LocalWarehouseStock = 5 });
        // The run's proposals: a dead-stock SKU under the floor on purpose, a clearance lock, and the holdout SKU (product 7, "Gaming").
        _tool.ProposedPrices.AddRange(
            new ProposedPrice { Id = 1, PricingRunId = run.Id, LayerId = _layer, Sku = "DEAD", DeadStockStage = PricingTool.Core.Domain.DeadStockStage.Stage2, ReasonCodes = "DEAD_STOCK_MARKDOWN", GuardrailFlags = "" },
            new ProposedPrice { Id = 2, PricingRunId = run.Id, LayerId = _layer, Sku = "LOCK", ReasonCodes = "", GuardrailFlags = "CLEARANCE_LOCK_HELD" },
            new ProposedPrice { Id = 3, PricingRunId = run.Id, LayerId = _layer, Sku = "HOLD", ProductId = 7, ReasonCodes = "", GuardrailFlags = "" });
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
        public List<(long Key, string Sku)> CampaignsEnded { get; } = new();
        public Task<long?> EndCampaignDiscountAsync(int layerId, long campaignKey, string sku, CancellationToken ct)
        {
            CampaignsEnded.Add((campaignKey, sku));
            return Task.FromResult<long?>(11);
        }
    }

    private sealed class Switch : IKillSwitch
    {
        public KillSwitchState State { get; set; } = KillSwitchState.Running;
        public Task<KillSwitchState> GetAsync(CancellationToken ct = default) => Task.FromResult(State);
    }

    private sealed class Map : ICategoryMapReader
    {
        public Task<IReadOnlyDictionary<string, IReadOnlyList<PricingTool.Core.Domain.SkuListing>>?> ReadAsync(int layerId, bool isMall, IReadOnlyCollection<string> categories, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<PricingTool.Core.Domain.SkuListing>>?>(
                new Dictionary<string, IReadOnlyList<PricingTool.Core.Domain.SkuListing>> { ["HOLD"] = new[] { new PricingTool.Core.Domain.SkuListing(7, "Gaming") } });
    }

    private CostWatcher Watcher() => new(_ai, _tool, new BandConfigProvider(_tool), _publisher, _switch, Options.Create(new CostWatcherOptions()),
        Options.Create(new AgentOptions { Limits = new SafetyLimitOptions { Holdouts = { [_layer.ToString()] = new() { "Gaming" } } } }),
        Options.Create(new PricingTool.Core.Options.PricingEngineOptions()), NullLogger<CostWatcher>.Instance, categoryMap: new Map());

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
    public async Task A_sku_in_a_live_campaign_is_judged_on_the_campaigns_minimum_margin_not_the_floor()
    {
        // A live, published campaign (key 18, min margin 8 %) with RISE at 66 and FINE at 80; its batch not rolled back.
        var brief = new CampaignBrief { LayerId = _layer, CalendarId = 18, Name = "Black Friday 2026", Kind = "calendar", ScopeKind = "all",
            StartDate = DateOnly.FromDateTime(DateTime.Now).AddDays(-1), EndDate = DateOnly.FromDateTime(DateTime.Now).AddDays(2), GoalType = "gross-profit",
            GoalValue = 1m, MinMarginPct = 8m, MaxDepthPct = 25m, Exclusions = "[]", Status = BriefStatus.Planned, CreatedUtc = DateTime.UtcNow };
        _ai.CampaignBriefs.Add(brief);
        var batch = new PublishBatch { LayerId = _layer, Db = "GjirafaEcommerce", StoreId = 2, Target = "Sandbox", ShopSchema = "shop_sandbox", Kind = "Campaign", Status = "Published", StartedUtc = DateTime.UtcNow };
        _ai.PublishBatches.Add(batch);
        await _ai.SaveChangesAsync();
        var plan = new CampaignPlan { BriefId = brief.Id, CalendarId = 18, LayerId = _layer, Round = 1, Strategy = "visible", Status = PlanStatus.Published, PublishBatchId = batch.Id, CreatedUtc = DateTime.UtcNow };
        _ai.CampaignPlans.Add(plan);
        await _ai.SaveChangesAsync();
        _ai.CampaignPlanLines.AddRange(new CampaignPlanLine { PlanId = plan.Id, Sku = "RISE", CampaignPrice = 66m, RegularPrice = 100m, CurrentPrice = 75m, Cost = 60m },
                                       new CampaignPlanLine { PlanId = plan.Id, Sku = "FINE", CampaignPrice = 80m, RegularPrice = 100m, CurrentPrice = 90m, Cost = 60m });
        Observe("RISE", 63m, "replica");      // campaign floor 63 / 0.92 = 68.48 > 66: end our campaign discount on it
        Observe("FINE", 70m, "replica");      // 70 / 0.92 = 76.09 <= 80: the campaign price still holds its minimum (the band floor would say 77.78)
        await _ai.SaveChangesAsync();

        var r = await Watcher().WatchAsync();

        var outcomes = await _ai.CostObservations.ToDictionaryAsync(o => o.Sku, o => o.Outcome);
        Assert.Equal(("ended-campaign-discount", "campaign"), (outcomes["RISE"], outcomes["FINE"]));
        Assert.Equal(new[] { (18L, "RISE") }, _publisher.CampaignsEnded);
        Assert.Empty(_publisher.Raised);                                                       // no ai-price raise, no team-discount hold
        Assert.Equal((0, 0, 1), (r.Raised, r.Held, r.Ended));
        var d = await _ai.AgentDecisions.SingleAsync();
        Assert.Equal(("end-campaign-discount", DecisionStatus.Applied, "campaign-under-min-margin"), (d.Action, d.Status, d.Limit));
        Assert.Empty((await new ExceptionsInbox(_ai, _tool).LoadAsync()).CostHolds);

        // Once the campaign's batch is rolled back, the SKU is judged on the band floor again.
        batch.RolledBackUtc = DateTime.UtcNow;
        Observe("FINE", 82m, "replica");     // band floor 82 / 0.9 = 91.11 above the live 90: raised
        await _ai.SaveChangesAsync();
        await Watcher().WatchAsync();
        Assert.Contains(_publisher.Raised, x => x.Sku == "FINE");
    }

    [Fact]
    public async Task Prices_another_rule_owns_are_left_to_the_daily_run_and_a_token_discount_is_ended()
    {
        Observe("DEAD", 70m, "replica");      // dead stock: the tunnel prices it under the floor on purpose
        Observe("LOCK", 70m, "replica");      // clearance lock
        Observe("HOLD", 70m, "replica");      // holdout category: the control group
        Observe("TINY", 70m, "replica");      // floor price 77.78 is 2.8 % off the shelf 80: no token discount
        await _ai.SaveChangesAsync();

        var r = await Watcher().WatchAsync();

        var outcomes = await _ai.CostObservations.ToDictionaryAsync(o => o.Sku, o => o.Outcome);
        Assert.Equal(("dead-stock-lane", "dead-stock-lane", "holdout"), (outcomes["DEAD"], outcomes["LOCK"], outcomes["HOLD"]));
        Assert.Equal("ended-our-discount", outcomes["TINY"]);
        Assert.Equal((0, 1, 3), (r.Raised, r.Ended, r.NoAction));
        Assert.Empty(_publisher.Raised);
        Assert.Equal(new[] { "TINY" }, _publisher.Ended);
    }

    [Fact]
    public async Task A_mall_price_that_follows_gjirafa50_is_left_to_gjirafa50()
    {
        var mall = new Layer { Brand = "GjirafaMall", CountryCode = "KS", DisplayName = "GjirafaMall — Kosovo", OperationalDatabase = "GjirafaMall", StoreId = 2, Currency = "EUR", IsActive = true };
        _tool.Layers.Add(mall);
        _tool.SaveChanges();
        _tool.PriceBands.Add(new PriceBand { LayerId = mall.Id, Name = "all", MinPrice = 0, MaxPrice = 500, MarginFloorPct = 10 });
        var run = new PricingRun { LayerId = mall.Id, Status = RunStatus.Succeeded, StartedUtc = DateTime.UtcNow.AddHours(-1), TriggeredBy = "daily cycle" };
        _tool.PricingRuns.Add(run);
        _tool.SaveChanges();
        _tool.DailySnapshots.Add(new DailySnapshot { LayerId = mall.Id, SnapshotDate = run.StartedUtc.Date, Sku = "SYNC", Pptcv = 60m, CurrentPrice = 70m, AnchorPrice = 100m, OldPrice = 100m });
        _tool.ProposedPrices.Add(new ProposedPrice { Id = 9, PricingRunId = run.Id, LayerId = mall.Id, Sku = "SYNC", ReasonCodes = "SYNCED_TO_GJ50", GuardrailFlags = "" });
        _tool.SaveChanges();
        _ai.CostObservations.Add(new CostObservation { LayerId = mall.Id, Sku = "SYNC", Pptcv = 70m, ObservedUtc = DateTime.UtcNow, Source = "replica" });
        await _ai.SaveChangesAsync();

        await Watcher().WatchAsync();

        Assert.Equal("synced-to-gj50", (await _ai.CostObservations.SingleAsync()).Outcome);
        Assert.Empty(_publisher.Raised);
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
