using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PricingTool.Agent;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Campaigns;
using PricingTool.Agent.ClaudeCode;
using PricingTool.Agent.Storage;
using PricingTool.Agent.Tools;
using PricingTool.Core.Domain;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;

namespace PricingTool.Tests;

/// <summary>
/// Campaign planning: code computes the plans (never below cost, never at or above today's price, within the depth cap,
/// a fixed campaign holdout, the minimum margin), Alpha chooses and explains, Nova accepts or adjusts within two rounds,
/// the thread is stored and an accepted plan is published as time-boxed discounts.
/// </summary>
public class CampaignPlanningTests : IDisposable
{
    private static readonly DepthEffect Depth = new(3m, 3, 6, 25m, 0m, new Dictionary<string, decimal>(), new Dictionary<string, decimal> { ["Black Friday"] = 4m }, "test");
    private static readonly IReadOnlyDictionary<int, PriceBandConfig> NoRounding = new Dictionary<int, PriceBandConfig>();

    private static CampaignCandidate C(string sku, decimal regular, decimal current, decimal cost, decimal perDay = 1m, string category = "Kozmetikë") =>
        new(sku, null, category, "Brand", regular, current, cost, perDay, 5, 0, false, false);

    private static PlanSpec Spec(decimal minMargin = 0m, decimal maxDepth = 25m, string goal = "gross-profit", decimal goalValue = 100m) =>
        new(1, 4, goal, goalValue, minMargin, maxDepth, 18m, 4m, new HashSet<string>());

    [Fact]
    public void Code_never_prices_below_cost_or_at_today_and_keeps_the_depth_cap_and_a_stable_holdout()
    {
        var candidates = Enumerable.Range(0, 200).Select(i => C($"S{i}", 100m, 100m, 60m)).Append(C("THIN", 100m, 100m, 96m)).Append(C("DISC", 100m, 80m, 50m)).ToList();
        foreach (var strategy in CampaignPlanner.Strategies)
        {
            var r = CampaignPlanner.Plan(strategy, Spec(maxDepth: 20m), candidates, Depth, NoRounding, 5000, new Dictionary<string, int>());
            Assert.All(r.Lines.Where(l => l.CampaignPrice is not null), l =>
            {
                Assert.True(l.CampaignPrice >= l.Cost, $"{strategy} {l.Sku} below cost");
                Assert.True(l.CampaignPrice < l.CurrentPrice, $"{strategy} {l.Sku} not under today's price");
                Assert.True(l.DepthPct <= 20m, $"{strategy} {l.Sku} past the depth cap");
            });
            Assert.DoesNotContain(r.Lines, l => l.Sku == "THIN" && l.CampaignPrice is not null);   // 5 % off (95) is under cost 96
            var holdout = r.Lines.Count(l => l.CampaignHoldout);
            Assert.InRange(holdout, 10, 35);                                                        // about 10 % of 202
            Assert.Equal(r.Lines.Where(l => l.CampaignHoldout).Select(l => l.Sku).OrderBy(s => s),
                candidates.Where(c => CampaignPlanner.IsHoldout(1, c.Sku)).Select(c => c.Sku).OrderBy(s => s));
            Assert.Equal(202, r.Totals.InScope);                                                   // totals cover every SKU in scope
        }
    }

    [Fact]
    public void Campaign_prices_take_the_layers_charm_rounding()
    {
        var bands = new Dictionary<int, PriceBandConfig>
        {
            [8] = new() { BandId = 8, Name = "Mall", MinPrice = 0, MaxPrice = 500, MarginFloorPct = 5, Rounding = RoundingConvention.GjmCharm, RoundingEnabled = true },
            [7] = new() { BandId = 7, Name = "Gj50", MinPrice = 0, MaxPrice = 500, MarginFloorPct = 5, Rounding = RoundingConvention.Gj50Charm, RoundingEnabled = true },
        };
        var candidates = new[]
        {
            new CampaignCandidate("MALL", 8, "Kozmetikë", "B", 16.48m, 13.99m, 10.99m, 2m, 5, 0, false, false),
            new CampaignCandidate("GJ50", 7, "Aksesorë", "B", 49.50m, 49.50m, 30m, 2m, 5, 0, false, false),
        };
        var key = Enumerable.Range(1, 100).First(k => !CampaignPlanner.IsHoldout(k, "MALL") && !CampaignPlanner.IsHoldout(k, "GJ50"));
        var r = CampaignPlanner.Plan("visible", Spec() with { CampaignKey = key }, candidates, Depth, bands, 5000, new Dictionary<string, int>());
        var mall = r.Lines.Single(l => l.Sku == "MALL");
        var gj50 = r.Lines.Single(l => l.Sku == "GJ50");
        Assert.NotNull(mall.CampaignPrice);
        Assert.Contains(mall.CampaignPrice!.Value % 1m, new[] { 0.99m, 0.49m });             // GjmCharm: …99 / …49, never 13.184
        Assert.True(mall.CampaignPrice < 13.99m && mall.CampaignPrice >= 10.99m);
        Assert.Equal(0.50m, gj50.CampaignPrice!.Value % 1m);                                  // Gj50Charm: .50 tiers
    }

    [Fact]
    public void Gross_profit_from_skus_outside_the_plan_counts_toward_a_whole_mall_goal()
    {
        var candidates = Enumerable.Range(0, 20).Select(i => C($"S{i}", 100m, 100m, 60m)).ToList();
        var alone = CampaignPlanner.Plan("max-gross-profit", Spec(goalValue: 1m), candidates, Depth, NoRounding, 5000, new Dictionary<string, int>());
        var goal = alone.Totals.GrossProfit + 500m;
        Assert.False(CampaignPlanner.Plan("max-gross-profit", Spec(goalValue: goal), candidates, Depth, NoRounding, 5000, new Dictionary<string, int>()).Feasible);
        var whole = CampaignPlanner.Plan("max-gross-profit", Spec(goalValue: goal) with { GrossProfitOutsidePlan = 600m }, candidates, Depth, NoRounding, 5000,
            new Dictionary<string, int>());
        Assert.True(whole.Feasible);
        Assert.Contains("from SKUs it doesn't price", whole.Feasibility);
        Assert.Equal(alone.Totals.GrossProfit, whole.Totals.GrossProfit);                      // the plan's own totals stay its own
    }

    [Fact]
    public void A_gross_profit_goal_is_judged_on_the_backtest_calibrated_estimate_with_its_range()
    {
        var candidates = Enumerable.Range(0, 20).Select(i => C($"S{i}", 100m, 100m, 60m)).ToList();
        var raw = CampaignPlanner.Plan("max-gross-profit", Spec(goalValue: 1m), candidates, Depth, NoRounding, 5000, new Dictionary<string, int>()).Totals.GrossProfit;
        var cal = new CampaignCalibration(1.25m, 1.05m, 7, 0.55m, 1.72m, 44m, "test");
        PlanResult At(decimal goal) => CampaignPlanner.Plan("max-gross-profit", Spec(goalValue: goal) with { Calibration = cal }, candidates, Depth, NoRounding, 5000,
            new Dictionary<string, int>());
        Assert.Equal((true, GoalVerdicts.Reachable), (At(raw).Feasible, At(raw).GoalVerdict));                          // the conservative x1.05 clears it
        var stretch = At(raw * 1.2m);
        Assert.Equal((true, GoalVerdicts.Stretch), (stretch.Feasible, stretch.GoalVerdict));                             // only the median x1.25 does
        Assert.Contains("median estimate", stretch.Feasibility);
        Assert.Contains("conservative", stretch.Feasibility);
        Assert.Equal((false, GoalVerdicts.NotReachable), (At(raw * 1.3m).Feasible, At(raw * 1.3m).GoalVerdict));
    }

    [Fact]
    public void The_backtest_replays_a_campaign_with_the_planners_arithmetic_and_calibrates_on_usable_ones()
    {
        var w = new CampaignWindow(1, "Për ty, Oct 2025", new DateOnly(2025, 10, 8), new DateOnly(2025, 10, 12), "main");
        var rows = new[]
        {
            new ReplaySkuRow(1, "SEEN", 28m, 2800m, 560m, 50m, 4500m, 900m),     // 1/day normally at 100; 50 sold at 90 (10 % deeper)
            new ReplaySkuRow(1, "IDLE", 14m, 700m, 140m, 0m, 0m, 0m),            // sold normally, not in the campaign
            new ReplaySkuRow(1, "TAIL", 0m, 0m, 0m, 10m, 800m, 300m),            // no normal-day sales: the planner can't see it
        };
        var r = CampaignReplay.Replay(w, 28, rows, Depth, representative: true);
        // SEEN: 1/day × 5 days × Për ty lift 1 (none in Depth) × exp(3 × 0.10) × 18/unit; IDLE: 0.5 × 5 × 1 × 10/unit
        var expected = Math.Round(5m * (decimal)Math.Exp(0.3) * 18m + 2.5m * 10m, 0);
        Assert.Equal((1200m, 900m, 300m, 1, 2, expected), (r.ActualGp, r.SeenActualGp, r.TailGp, r.TailSkus, r.SeenSkus, r.PredictedSeenGp));
        Assert.Equal(25.0m, r.TailSharePct);
        Assert.True(r.Usable);

        var results = new[] { 0.6m, 1.2m, 1.7m, 0.3m }.Select((k, i) => r with { CampaignId = i, PredictedSeenGp = Math.Round(1200m / k, 0) }).ToList();
        Assert.Null(CampaignReplay.Calibrate(results.Take(3)));                                  // fewer than 4 usable
        var cal = CampaignReplay.Calibrate(results.Append(r with { Representative = false, PredictedSeenGp = 1m }))!;
        Assert.Equal((4, 0.3m, 1.7m), (cal.Campaigns, cal.Low, cal.High));                        // the unrepresentative one left out
        Assert.Equal(0.9m, cal.Factor);                                                           // median of 0.3, 0.6, 1.2, 1.7
        Assert.Equal(0.6m, cal.Conservative);                                                     // fewer than 8: the second-lowest
    }

    [Fact]
    public void A_campaign_scored_without_an_event_lift_does_not_move_the_factor_for_types_with_one()
    {
        var w = new CampaignWindow(1, "Për ty, Oct 2025", new DateOnly(2025, 10, 8), new DateOnly(2025, 10, 12), "main");
        var r = CampaignReplay.Replay(w, 28, new[] { new ReplaySkuRow(1, "SEEN", 28m, 2800m, 560m, 50m, 4500m, 900m) },
            Depth with { EventLiftByType = new Dictionary<string, decimal> { ["Për ty"] = 3m } }, representative: true);
        Assert.True(r.SeenType);
        var seen = new[] { 0.6m, 1.2m, 1.7m, 0.9m }.Select((k, i) => r with { CampaignId = i, PredictedSeenGp = Math.Round(r.ActualGp / k, 0) }).ToList();
        var unseen = r with { CampaignId = 9, EventLift = null, PredictedSeenGp = Math.Round(r.ActualGp / 11.8m, 0) };   // its only window left out: lift x1
        Assert.False(unseen.SeenType);
        var all = seen.Append(unseen).ToList();
        Assert.Equal(CampaignReplay.Calibrate(seen, seenType: true)!.Factor, CampaignReplay.Calibrate(all, seenType: true)!.Factor);
        Assert.Null(CampaignReplay.Calibrate(all, seenType: false));                               // one unseen-type replay: no factor
        var backtest = new CampaignBacktest(all, CampaignReplay.Calibrate(all, seenType: true), CampaignReplay.Calibrate(all, seenType: false), all, null);
        Assert.Same(backtest.SeenType, backtest.For(planHasEventLift: true));
        Assert.Null(backtest.For(planHasEventLift: false));
    }

    [Fact]
    public void No_single_sku_goes_under_the_code_margin_floor_whatever_the_blended_minimum()
    {
        var candidates = new[] { C("A", 100m, 100m, 80m), C("B", 100m, 100m, 96.5m), C("C", 100m, 100m, 40m) };   // B: 3.5 % today
        foreach (var strategy in CampaignPlanner.Strategies)
        {
            var r = CampaignPlanner.Plan(strategy, Spec(minMargin: 0m) with { SkuMarginFloorPct = 3m }, candidates, Depth, NoRounding, 100, new Dictionary<string, int>());
            foreach (var l in r.Lines.Where(l => l.CampaignPrice is { } p && p < l.CurrentPrice))
                Assert.True((l.CampaignPrice!.Value - l.Cost) / l.CampaignPrice.Value >= 0.03m, $"{strategy} {l.Sku} {l.CampaignPrice}");
            Assert.DoesNotContain(r.Lines, l => l.Sku == "B" && l.CampaignPrice is { } p && p < 100m);    // any discount takes B under 3 %
        }
    }

    [Fact]
    public void Spend_to_margin_buys_volume_down_to_the_margin_floor_and_no_further()
    {
        var candidates = Enumerable.Range(0, 60).Select(i => C($"S{i}", 100m, 100m, 55m + i % 20, 1m + i % 3)).ToList();
        var visible = CampaignPlanner.Plan("visible", Spec(minMargin: 20m), candidates, Depth, NoRounding, 5000, new Dictionary<string, int>());
        var spend = CampaignPlanner.Plan("spend-to-margin", Spec(minMargin: 20m, goal: "margin", goalValue: 20m), candidates, Depth, NoRounding, 5000,
            new Dictionary<string, int>());
        Assert.True(spend.Totals.MarginPct >= 20m);                                                // the floor holds
        Assert.True(spend.Totals.MarginPct < visible.Totals.MarginPct);                            // margin spent
        Assert.True(spend.Totals.Units > visible.Totals.Units);                                     // on volume
        Assert.True(spend.Totals.AverageDepthPct > visible.Totals.AverageDepthPct);
        Assert.Contains("A 20.0% margin is reachable", spend.Feasibility);
    }

    [Fact]
    public void The_strategies_trade_profit_for_volume_and_the_minimum_margin_holds()
    {
        var candidates = Enumerable.Range(0, 50).Select(i => C($"S{i}", 100m, 100m, 60m)).ToList();
        var gp = CampaignPlanner.Plan("max-gross-profit", Spec(), candidates, Depth, NoRounding, 5000, new Dictionary<string, int>());
        var volume = CampaignPlanner.Plan("volume-at-no-loss", Spec(), candidates, Depth, NoRounding, 5000, new Dictionary<string, int>());
        var visible = CampaignPlanner.Plan("visible", Spec(), candidates, Depth, NoRounding, 5000, new Dictionary<string, int>());
        Assert.True(gp.Totals.GrossProfit >= volume.Totals.GrossProfit);
        Assert.True(volume.Totals.Units >= gp.Totals.Units);
        Assert.True(volume.Totals.GrossProfit >= volume.Totals.GrossProfitWithoutDiscounts - 0.5m);  // no loss against no discounts
        Assert.True(visible.Totals.Discounted >= volume.Totals.Discounted);

        var strict = CampaignPlanner.Plan("visible", Spec(minMargin: 38m), candidates, Depth, NoRounding, 5000, new Dictionary<string, int>());
        Assert.True(strict.Totals.MarginPct >= 38m);
        Assert.True(strict.Totals.Discounted < visible.Totals.Discounted);

        var capped = CampaignPlanner.Plan("visible", Spec(), candidates, Depth, NoRounding, 10, new Dictionary<string, int>());
        Assert.Equal(10, capped.Totals.Discounted);
        Assert.True(capped.LeftOut.ContainsKey("over the cap of 10 discounted SKUs"));
    }

    // ---- the thread: Alpha chooses, Nova accepts or adjusts, the plan is published ----

    private readonly AgentDbContext _db = new(new DbContextOptionsBuilder<AgentDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly PricingToolDbContext _tool = new(new DbContextOptionsBuilder<PricingToolDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Queue<string> _answers = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pt-plan-tests-" + Guid.NewGuid().ToString("N"));
    public void Dispose()
    {
        _db.Dispose();
        _tool.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class Runner : IProcessRunner
    {
        private readonly CampaignPlanningTests _t;
        public Runner(CampaignPlanningTests t) => _t = t;
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default) =>
            Task.FromResult(new ProcessResult(0, _t._answers.Dequeue(), "", false, TimeSpan.FromSeconds(10)));
    }

    private sealed class Candidates : ICampaignCandidateSource
    {
        public Task<IReadOnlyList<CampaignCandidate>> LoadAsync(int layerId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CampaignCandidate>>(Enumerable.Range(0, 40).Select(i => C($"S{i}", 100m, 100m, 60m, category: i < 4 ? "Sport" : "Kozmetikë"))
                .Append(C("THIN", 100m, 100m, 97.5m, perDay: 3m))                                         // 2.5 % margin today
                .Append(new CampaignCandidate("DEAD", null, "Kozmetikë", "Brand", 100m, 70m, 60m, 1m, 5, 0, DeadStock: true, FollowsGj50: false)).ToList());
    }

    private sealed class Publisher : ICampaignPublisher
    {
        public List<(long Key, DateTime Start, DateTime End, int Lines)> Calls { get; } = new();
        public Task<(long BatchId, string Status)> PublishAsync(int layerId, long campaignKey, DateTime startUtc, DateTime endUtc,
            IReadOnlyList<(string Sku, decimal Price, decimal Shelf)> lines, CancellationToken ct)
        {
            Calls.Add((campaignKey, startUtc, endUtc, lines.Count));
            return Task.FromResult((77L, "Published"));
        }

        public List<long> RolledBack { get; } = new();
        public Task<int> RollbackAsync(long batchId, CancellationToken ct)
        {
            RolledBack.Add(batchId);
            return Task.FromResult(3);
        }
    }

    private void Says(JsonObject structured) => _answers.Enqueue(new JsonObject
    {
        ["type"] = "result", ["subtype"] = "success", ["is_error"] = false, ["num_turns"] = 1, ["result"] = "", ["total_cost_usd"] = 0.05m,
        ["usage"] = new JsonObject { ["input_tokens"] = 1000, ["output_tokens"] = 300 }, ["structured_output"] = structured,
    }.ToJsonString());

    private (CampaignPlanning Planning, Publisher Publisher, CampaignBrief Brief) Setup()
    {
        var layer = new Layer { Brand = "GjirafaMall", CountryCode = "KS", DisplayName = "GjirafaMall — Kosovo", OperationalDatabase = "GjirafaMall", StoreId = 2, Currency = "EUR", VatRatePct = 18m };
        _tool.Layers.Add(layer);
        _tool.SaveChanges();
        var entry = new CampaignCalendarEntry { LayerId = layer.Id, Name = "Black Friday 2026", Type = "main", StartDate = new DateOnly(2026, 11, 27), EndDate = new DateOnly(2026, 11, 30), Status = CampaignStatus.Planned, CreatedBy = "test" };
        _db.CampaignCalendar.Add(entry);
        _db.SaveChanges();
        var brief = new CampaignBrief
        {
            PassId = 1, LayerId = layer.Id, CalendarId = entry.Id, Name = "Black Friday 2026", Kind = "calendar", ScopeKind = "all",
            StartDate = entry.StartDate, EndDate = entry.EndDate, GoalType = "gross-profit", GoalValue = 500m, MinMarginPct = 5m, MaxDepthPct = 25m,
            Exclusions = "[]", Rationale = "test", Status = BriefStatus.Proposed, CreatedUtc = DateTime.UtcNow,
        };
        _db.CampaignBriefs.Add(brief);
        _db.SaveChanges();
        var options = Options.Create(new AgentOptions
        {
            Enabled = true, RuntimeDirectory = Path.Combine(_dir, "runtime"),
            Limits = new SafetyLimitOptions { Holdouts = { [layer.Id.ToString()] = new() { "Sport" } } },
        });
        var client = new ClaudeCodeClient(options, new Runner(this), _db, NullLogger<ClaudeCodeClient>.Instance);
        var reader = new PricingDataReader(new AgentDataSource("unused"));
        var publisher = new Publisher();
        var planning = new CampaignPlanning(_db, _tool, new Candidates(), new AgentDataTools(reader, options, new AgentToolLog(_db), _db), new BandConfigProvider(_tool),
            client, new ConfigKillSwitch(options.Value), options, NullLogger<CampaignPlanning>.Instance, publisher: publisher)
        { DepthSource = (_, _) => Task.FromResult(Depth), BacktestSource = (_, _) => Task.FromResult<CampaignBacktest?>(null) };
        return (planning, publisher, brief);
    }

    [Fact]
    public async Task Alpha_chooses_Nova_adjusts_once_then_accepts_and_the_accepted_plan_is_published_time_boxed()
    {
        var (planning, publisher, brief) = Setup();
        Says(new JsonObject { ["strategy"] = "max-gross-profit", ["explanation"] = "Profit first." });
        Says(new JsonObject { ["decision"] = "adjust", ["goalValue"] = 400m, ["maxDepthPct"] = 40m, ["minMarginPct"] = null, ["message"] = "Too few SKUs; make it visible." });
        Says(new JsonObject { ["strategy"] = "visible", ["explanation"] = "Visible, as asked." });
        Says(new JsonObject { ["decision"] = "accept", ["goalValue"] = null, ["maxDepthPct"] = null, ["minMarginPct"] = null, ["message"] = "Accepted." });

        var r = await planning.PlanAsync(brief.Id, publish: true);

        Assert.Equal(PlanStatus.Published, r.Status);
        Assert.Equal((PlanStatus.Superseded, "max-gross-profit"), (r.Plans[0].Status, r.Plans[0].Strategy));
        Assert.Equal(("visible", 2), (r.Final!.Strategy, r.Final.Round));
        var b = await _db.CampaignBriefs.SingleAsync();
        Assert.Equal((400m, 25m, BriefStatus.Planned), (b.GoalValue, b.MaxDepthPct, b.Status));             // Nova's 40 % capped at 25
        var call = Assert.Single(publisher.Calls);
        Assert.Equal((b.CalendarId!.Value, r.Final.DiscountedSkus), (call.Key, call.Lines));
        Assert.Equal(new DateTime(2026, 11, 26, 23, 0, 0), call.Start);                                     // 27 Nov 00:00 in Kosovo (CET)
        var lines = await _db.CampaignPlanLines.Where(l => l.PlanId == r.Final.Id).ToListAsync();
        Assert.DoesNotContain(lines, l => l.Sku == "DEAD" || l.Category == "Sport");                         // dead stock and holdout left out
        var thread = await _db.CampaignMessages.OrderBy(m => m.Id).Select(m => m.From).ToListAsync();
        Assert.Equal(new[] { "code", "Alpha", "Nova", "code", "code", "Alpha", "Nova", "code" }, thread);
    }

    [Fact]
    public async Task A_re_plan_rolls_back_the_campaigns_earlier_publish_before_it_publishes()
    {
        var (planning, publisher, brief) = Setup();
        var earlier = new CampaignBrief
        {
            PassId = 1, LayerId = brief.LayerId, CalendarId = brief.CalendarId, Name = brief.Name, Kind = "calendar", ScopeKind = "all", StartDate = brief.StartDate,
            EndDate = brief.EndDate, GoalType = "gross-profit", GoalValue = 500m, MinMarginPct = 5m, MaxDepthPct = 25m, Exclusions = "[]", Rationale = "earlier",
            Status = BriefStatus.Planned, CreatedUtc = DateTime.UtcNow,
        };
        _db.CampaignBriefs.Add(earlier);
        var onShelf = new PublishBatch { LayerId = brief.LayerId, Kind = "Campaign", Status = "Published", Target = "Sandbox", StartedUtc = DateTime.UtcNow };
        var undone = new PublishBatch { LayerId = brief.LayerId, Kind = "Campaign", Status = "RolledBack", Target = "Sandbox", StartedUtc = DateTime.UtcNow, RolledBackUtc = DateTime.UtcNow };
        _db.PublishBatches.AddRange(onShelf, undone);
        await _db.SaveChangesAsync();
        _db.CampaignPlans.AddRange(
            new CampaignPlan { BriefId = earlier.Id, LayerId = brief.LayerId, Strategy = "visible", Status = PlanStatus.Published, PublishBatchId = onShelf.Id, CreatedUtc = DateTime.UtcNow },
            new CampaignPlan { BriefId = earlier.Id, LayerId = brief.LayerId, Strategy = "visible", Status = PlanStatus.Published, PublishBatchId = undone.Id, CreatedUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync();
        Says(new JsonObject { ["strategy"] = "visible", ["explanation"] = "Visible." });
        Says(new JsonObject { ["decision"] = "accept", ["goalValue"] = null, ["maxDepthPct"] = null, ["minMarginPct"] = null, ["message"] = "Accepted." });

        var r = await planning.PlanAsync(brief.Id, publish: true);

        Assert.Equal(PlanStatus.Published, r.Status);
        Assert.Equal(new[] { onShelf.Id }, publisher.RolledBack);                                       // only the publish still on the shelf
        Assert.Single(publisher.Calls);
        Assert.All(await _db.CampaignPlans.Where(p => p.BriefId == earlier.Id).ToListAsync(), p => Assert.Equal(PlanStatus.Superseded, p.Status));
        Assert.Contains(await _db.CampaignMessages.Select(m => m.Text).ToListAsync(), t => t.Contains($"(batch #{onShelf.Id}) rolled back (3 rows restored): plan #{r.Final!.Id} replaces it"));
    }

    [Fact]
    public async Task Without_acceptance_after_two_rounds_the_plan_waits_for_a_person_and_nothing_is_published()
    {
        var (planning, publisher, brief) = Setup();
        for (var i = 0; i < 2; i++)
        {
            Says(new JsonObject { ["strategy"] = "volume-at-no-loss", ["explanation"] = "Volume." });
            Says(new JsonObject { ["decision"] = "adjust", ["goalValue"] = 900m, ["maxDepthPct"] = null, ["minMarginPct"] = null, ["message"] = "Not enough." });
        }

        var r = await planning.PlanAsync(brief.Id, publish: true);

        Assert.Equal(PlanStatus.Held, r.Status);
        Assert.Empty(publisher.Calls);
    }

    [Fact]
    public async Task A_goal_a_person_set_stays_and_a_plan_short_of_it_waits_for_that_person()
    {
        var (planning, publisher, brief) = Setup();
        var entry = await _db.CampaignCalendar.SingleAsync();
        (entry.GoalType, entry.GoalValue, entry.GoalSetBy) = ("gross-profit", 1_000_000m, "user, 2026-10-30");
        brief.GoalValue = 1_000_000m;
        await _db.SaveChangesAsync();
        Says(new JsonObject { ["strategy"] = "visible", ["explanation"] = "Visible." });
        Says(new JsonObject { ["decision"] = "adjust", ["goalValue"] = 100m, ["maxDepthPct"] = null, ["minMarginPct"] = null, ["message"] = "Lower the goal to what the plan reaches." });
        Says(new JsonObject { ["strategy"] = "visible", ["explanation"] = "Visible." });
        Says(new JsonObject { ["decision"] = "accept", ["goalValue"] = null, ["maxDepthPct"] = null, ["minMarginPct"] = null, ["message"] = "Accepted." });

        var r = await planning.PlanAsync(brief.Id, publish: true);

        Assert.Equal(1_000_000m, (await _db.CampaignBriefs.SingleAsync()).GoalValue);                       // Nova can't lower a person's goal
        Assert.Equal(PlanStatus.Held, r.Status);                                                             // short of it: a person decides
        Assert.Empty(publisher.Calls);
        var said = await _db.CampaignMessages.Where(m => m.From == "code").Select(m => m.Text).ToListAsync();
        Assert.Contains(said, t => t.Contains("set by a person") && t.Contains("it stays"));
        Assert.Contains(said, t => t.Contains($"approve-plan {r.Final!.Id}"));

        var approved = await planning.ApproveAsync(r.Final.Id, "user", publish: true);                   // the person approves it as it is
        Assert.Equal(PlanStatus.Published, approved.Status);
        Assert.Single(publisher.Calls);
        Assert.Equal("Refused", (await planning.ApproveAsync(r.Final.Id, "user", publish: true)).Status);
    }

    [Fact]
    public async Task A_persons_rules_keep_skus_out_and_raise_the_minimum_margin_on_their_days_only()
    {
        var (planning, _, brief) = Setup();
        _db.ChatRules.AddRange(
            new ChatRule { Kind = ChatRuleKinds.ExcludeBrand, Value = "Brand", FromDate = brief.StartDate, ToDate = brief.EndDate, Instruction = "test", CreatedBy = "user" },
            new ChatRule { Kind = ChatRuleKinds.MinCampaignMargin, Value = "30", FromDate = brief.StartDate.AddDays(-1), ToDate = brief.StartDate, Instruction = "test", CreatedBy = "user" },
            new ChatRule { Kind = ChatRuleKinds.MinCampaignMargin, Value = "60", FromDate = brief.EndDate.AddDays(1), ToDate = brief.EndDate.AddDays(9), Instruction = "test", CreatedBy = "user" },  // after the campaign
            new ChatRule { Kind = ChatRuleKinds.ExcludeCategory, Value = "Kozmetikë", FromDate = brief.StartDate, ToDate = brief.EndDate, Instruction = "test", CreatedBy = "user",
                RemovedUtc = DateTime.UtcNow, RemovedBy = "user" });                                                                                                     // removed
        await _db.SaveChangesAsync();
        Says(new JsonObject { ["strategy"] = "visible", ["explanation"] = "Visible." });
        Says(new JsonObject { ["decision"] = "accept", ["goalValue"] = null, ["maxDepthPct"] = null, ["minMarginPct"] = null, ["message"] = "Accepted." });

        await planning.PlanAsync(brief.Id, publish: false);

        var said = await _db.CampaignMessages.Where(m => m.From == "code").Select(m => m.Text).ToListAsync();
        Assert.Contains(said, t => t.Contains("raises the minimum margin from 5% to 30%"));                  // 60 % is after the campaign: not applied
        Assert.Contains(said, t => t.Contains("kept out by a person's rule") && t.Contains("exclude-brand Brand"));
        Assert.DoesNotContain(said, t => t.Contains("exclude-category"));                                   // the removed rule
    }

    [Fact]
    public async Task A_goal_asked_for_in_the_chat_binds_a_mini_campaign_too()
    {
        var (planning, publisher, brief) = Setup();
        var entry = await _db.CampaignCalendar.SingleAsync();
        (entry.Type, entry.GoalType, entry.GoalValue, entry.GoalSetBy) = ("mini", "gross-profit", 1_000_000m, "manager@test in the chat (conversation 3)");
        (brief.Kind, brief.GoalValue) = ("mini", 1_000_000m);
        await _db.SaveChangesAsync();
        Says(new JsonObject { ["strategy"] = "max-gross-profit", ["explanation"] = "Profit first." });
        Says(new JsonObject { ["decision"] = "adjust", ["goalValue"] = 900m, ["maxDepthPct"] = null, ["minMarginPct"] = null, ["message"] = "Lower the goal to what the plan reaches." });
        Says(new JsonObject { ["strategy"] = "max-gross-profit", ["explanation"] = "Profit first." });
        Says(new JsonObject { ["decision"] = "accept", ["goalValue"] = null, ["maxDepthPct"] = null, ["minMarginPct"] = null, ["message"] = "Accepted." });

        var r = await planning.PlanAsync(brief.Id, publish: true);

        Assert.Equal(1_000_000m, (await _db.CampaignBriefs.SingleAsync()).GoalValue);                       // Nova can't lower it
        Assert.Equal(PlanStatus.Held, r.Status);
        Assert.Empty(publisher.Calls);
    }

    [Fact]
    public async Task A_plan_that_discounts_no_sku_is_never_published()
    {
        var (planning, publisher, brief) = Setup();
        brief.MinMarginPct = 60m;                                                                           // every candidate makes 40 %: nothing can be discounted
        await _db.SaveChangesAsync();
        Says(new JsonObject { ["strategy"] = "max-gross-profit", ["explanation"] = "Profit first." });
        Says(new JsonObject { ["decision"] = "accept", ["goalValue"] = null, ["maxDepthPct"] = null, ["minMarginPct"] = null, ["message"] = "Accepted." });

        var r = await planning.PlanAsync(brief.Id, publish: true);

        Assert.Equal((PlanStatus.Accepted, 0), (r.Status, r.Final!.DiscountedSkus));
        Assert.Empty(publisher.Calls);
        Assert.Contains(await _db.CampaignMessages.Select(m => m.Text).ToListAsync(), t => t.Contains("discounts no SKU"));
    }

    [Fact]
    public async Task The_minimum_margin_never_goes_under_the_code_floor_from_the_brief_or_the_review()
    {
        var (planning, _, brief) = Setup();
        brief.MinMarginPct = 0m;                                                                            // the model asked for none
        await _db.SaveChangesAsync();
        Says(new JsonObject { ["strategy"] = "volume-at-no-loss", ["explanation"] = "Volume." });
        Says(new JsonObject { ["decision"] = "adjust", ["goalValue"] = null, ["maxDepthPct"] = null, ["minMarginPct"] = 0.5m, ["message"] = "Go deeper." });
        Says(new JsonObject { ["strategy"] = "volume-at-no-loss", ["explanation"] = "Volume." });
        Says(new JsonObject { ["decision"] = "accept", ["goalValue"] = null, ["maxDepthPct"] = null, ["minMarginPct"] = null, ["message"] = "Accepted." });

        var r = await planning.PlanAsync(brief.Id, publish: false);

        Assert.Equal(3m, (await _db.CampaignBriefs.SingleAsync()).MinMarginPct);                           // stored: the cost watcher uses it
        var said = await _db.CampaignMessages.Where(m => m.From == "code").Select(m => m.Text).ToListAsync();
        Assert.Contains(said, t => t.Contains("Minimum margin 0% raised to 3%"));
        Assert.Contains(said, t => t.Contains("Minimum margin 0.5% raised to 3%"));
        var planIds = r.Plans.Select(p => p.Id).ToList();
        var lines = await _db.CampaignPlanLines.Where(l => planIds.Contains(l.PlanId)).ToListAsync();
        Assert.NotEmpty(lines);
        Assert.DoesNotContain(lines, l => l.Sku == "THIN");                                                // any discount would take it under 3 %
        Assert.All(lines, l => Assert.True((l.CampaignPrice - l.Cost) / l.CampaignPrice >= 0.03m, $"{l.Sku}: {l.CampaignPrice} on cost {l.Cost}"));
    }

    [Fact]
    public async Task A_person_goal_met_only_on_the_median_goes_ahead_and_waits_for_that_person()
    {
        var (planning, publisher, brief) = Setup();
        var entry = await _db.CampaignCalendar.SingleAsync();
        (entry.GoalType, entry.GoalValue, entry.GoalSetBy) = ("gross-profit", 5000m, "user, 2026-10-30");
        brief.GoalValue = 5000m;
        await _db.SaveChangesAsync();
        var cal = new CampaignCalibration(1000m, 0.001m, 7, 0.001m, 1000m, 44m, "test");               // Depth has a Black Friday lift: the seen-type factor
        planning.BacktestSource = (_, _) => Task.FromResult<CampaignBacktest?>(new CampaignBacktest(new(), cal, null, new(), null));
        Says(new JsonObject { ["strategy"] = "visible", ["explanation"] = "Visible." });
        Says(new JsonObject { ["decision"] = "accept", ["goalValue"] = null, ["maxDepthPct"] = null, ["minMarginPct"] = null, ["message"] = "Accepted." });

        var r = await planning.PlanAsync(brief.Id, publish: true);

        Assert.Equal((PlanStatus.Published, GoalVerdicts.Stretch, true), (r.Status, r.Final!.GoalVerdict, r.Final.WaitsForPerson));
        Assert.Single(publisher.Calls);
        var inbox = await new PricingTool.Agent.Operations.ExceptionsInbox(_db, _tool).LoadAsync();
        Assert.Equal(r.Final.Id, Assert.Single(inbox.CampaignPlans).Plan.Id);

        var approved = await planning.ApproveAsync(r.Final.Id, "user", publish: true);                      // approved as a stretch: no second publish
        Assert.Equal((PlanStatus.Published, false, "user"), (approved.Status, approved.Final!.WaitsForPerson, approved.Final.ReviewedBy));
        Assert.Single(publisher.Calls);
        Assert.Empty((await new PricingTool.Agent.Operations.ExceptionsInbox(_db, _tool).LoadAsync()).CampaignPlans);
    }
}
