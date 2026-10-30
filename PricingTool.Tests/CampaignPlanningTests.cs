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
        { DepthSource = (_, _) => Task.FromResult(Depth) };
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
}
