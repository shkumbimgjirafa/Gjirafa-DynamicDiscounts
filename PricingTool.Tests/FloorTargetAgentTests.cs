using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PricingTool.Agent;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.ClaudeCode;
using PricingTool.Agent.Storage;
using PricingTool.Agent.Tools;
using PricingTool.Data;
using PricingTool.Data.Entities;

namespace PricingTool.Tests;

/// <summary>
/// Alpha's weekly pass end to end against a fake Claude Code process: what gets applied, held, rejected or
/// queued; that thin and holdout rows are kept by code; that a failed answer changes nothing; the kill switch;
/// and the overrides, audit rows and time-sheet row it writes.
/// </summary>
public class FloorTargetAgentTests : IDisposable
{
    private const int Layer = 4;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pt-alpha-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AgentDbContext _db;
    private readonly PricingToolDbContext _tool;
    private readonly FakeRunner _runner = new();

    public FloorTargetAgentTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new AgentDbContext(new DbContextOptionsBuilder<AgentDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _db.Database.EnsureCreated();                                           // seeds the time-sheet task type
        _tool = new PricingToolDbContext(new DbContextOptionsBuilder<PricingToolDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    }

    public void Dispose()
    {
        _db.Dispose();
        _tool.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeRunner : IProcessRunner
    {
        private readonly Queue<string> _stdout = new();
        public int Calls { get; private set; }
        public void Answer(string stdout) => _stdout.Enqueue(stdout);

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new ProcessResult(0, _stdout.Dequeue(), "", false, TimeSpan.FromSeconds(42)));
        }
    }

    private sealed class FixedScorecard : IAlphaInputSource
    {
        public Task<AlphaScorecard> BuildAsync(int layerId, CancellationToken ct = default)
        {
            var bands = new[] { new BandConfigRow(26, "10–50", 10m, 50m, 13m, 23m, 1) };
            var sales = new[]
            {
                Row("Aksesorë", 100), Row("Celular, Tablet & Navigim", 100), Row("SMART", 100), Row("Gaming", 10), Row("Unknown", 0),
            };
            var w = DateWindow.Ending(new DateOnly(2026, 10, 30), 30, 14);
            return Task.FromResult(AlphaScorecard.Build(new LayerInfo(Layer, "Gjirafa50 — Kosovo", "Gjirafa50", "EUR", 18m), w,
                w.SameWeeksLastYear(), new ScorecardOptions(), bands, sales, Array.Empty<StockAggregate>(), Array.Empty<CostChangeRow>(),
                Array.Empty<CampaignWindow>(), null));

            static SalesAggregate Row(string cat, decimal units) => new(26, cat, 0, false, units, units * 30, units * 6, 0, 0, 0, 5, 0, 0);
        }
    }

    private FloorTargetAgent Agent(bool enabled = true)
    {
        var options = Options.Create(new AgentOptions
        {
            Enabled = enabled,
            RuntimeDirectory = Path.Combine(_dir, "runtime"),
            TimeSheetOwnerEmail = "owner@example.test",
            Limits = new SafetyLimitOptions
            {
                MaxStepPpPerWeek = 1m,
                HardMinFloorPct = new() { ["4"] = 4m },
                Holdouts = new() { ["4"] = new() { "SMART" } },
            },
        });
        var client = new ClaudeCodeClient(options, _runner, _db, NullLogger<ClaudeCodeClient>.Instance);
        return new FloorTargetAgent(new FixedScorecard(), client, _db, _tool, new AgentModeStore(_tool), options,
            NullLogger<FloorTargetAgent>.Instance);
    }

    private async Task SetMode(AutonomyMode mode) => await new AgentModeStore(_tool).SetAsync(Layer, mode, "test");

    private static JsonObject Decision(string category, string action, decimal floor, decimal target) => new()
    {
        ["bandId"] = 26, ["category"] = category, ["action"] = action, ["newFloorPct"] = floor, ["newTargetPct"] = target,
        ["confidence"] = 0.7m, ["reason"] = $"{category}: test reason.", ["evidenceRefs"] = new JsonArray("scorecard"),
    };

    private void ModelAnswers(params JsonObject[] decisions) => _runner.Answer(Envelope(new JsonObject
    {
        ["layer"] = Layer, ["decisions"] = new JsonArray(decisions.Select(d => (JsonNode)d).ToArray()), ["summary"] = "Test pass.",
    }.ToJsonString()));

    private static string Envelope(string structured) => new JsonObject
    {
        ["type"] = "result", ["subtype"] = "success", ["is_error"] = false, ["duration_ms"] = 40000, ["num_turns"] = 5,
        ["result"] = "", ["total_cost_usd"] = 0.5m,
        ["usage"] = new JsonObject { ["input_tokens"] = 1000, ["output_tokens"] = 2000 },
        ["structured_output"] = JsonNode.Parse(structured),
    }.ToJsonString();

    private void ModelAnswersStandardWeek() => ModelAnswers(
        Decision("Aksesorë", "raise", 14m, 24m),                     // within the limits
        Decision("Celular, Tablet & Navigim", "raise", 18m, 28m),    // +5 pt: held
        Decision("SMART", "lower", 12m, 22m),                        // holdout: kept
        Decision("Gaming", "raise", 14m, 24m));                      // thin: kept

    [Fact]
    public async Task Autonomous_applies_what_is_within_the_limits_and_holds_the_rest()
    {
        await SetMode(AutonomyMode.Autonomous);
        ModelAnswersStandardWeek();

        var result = await Agent().RunPassAsync(Layer);

        Assert.Equal("Succeeded", result.Status);
        var d = result.Decisions.ToDictionary(x => x.Category);
        Assert.Equal((DecisionStatus.Applied, "raise", 14m, 24m), (d["Aksesorë"].Status, d["Aksesorë"].Action, d["Aksesorë"].ProposedFloorPct, d["Aksesorë"].ProposedTargetPct!.Value));
        Assert.NotNull(d["Aksesorë"].AppliedUtc);
        Assert.Equal((DecisionStatus.Held, "max-step"), (d["Celular, Tablet & Navigim"].Status, d["Celular, Tablet & Navigim"].Limit));
        Assert.Equal(("keep", "holdout", 13m), (d["SMART"].Action, d["SMART"].Limit, d["SMART"].ProposedFloorPct));
        Assert.Equal(("keep", "thin-data", 13m), (d["Gaming"].Action, d["Gaming"].Limit, d["Gaming"].ProposedFloorPct));
        Assert.False(d.ContainsKey("Unknown"));                                // no sales, no decision asked

        var rule = Assert.Single(await _tool.BandCategoryRules.ToListAsync());  // only the applied change is written
        Assert.Equal((Layer, 26, "Aksesorë", 14m, 24m, "Alpha", d["Aksesorë"].Id),
            (rule.LayerId, rule.BandId, rule.Category, rule.MarginFloorPct!.Value, rule.TargetMarginPct!.Value, rule.UpdatedBy, rule.AgentDecisionId!.Value));
        var audit = await _tool.AuditLog.Where(a => a.Category == "Agent" && a.EntityType == "BandCategoryRule").SingleAsync();
        Assert.Equal(("floor 13%, target 23%", "floor 14%, target 24%"), (audit.OldValue, audit.NewValue));

        var pass = await _db.AgentPasses.SingleAsync();
        Assert.Equal((3, 1, 0, 0, "Autonomous"), (pass.Applied, pass.Held, pass.Rejected, pass.Pending, pass.Mode));
        Assert.Equal(pass.CallTag, (await _db.AiCallLog.SingleAsync()).CallTag);
        Assert.Contains("\"row\":\"26/Aksesorë\"", d["Aksesorë"].EvidenceRefs);

        var sheet = await _db.TimeSheetEntries.SingleAsync();
        Assert.Equal((AgentDbContext.TaskTypes.WeeklyFloorTargetReview, (int?)null, "owner@example.test", "app"),
            (sheet.TaskTypeId, sheet.MinutesBefore, sheet.UserEmail, sheet.Source));
        Assert.Contains("1 changed, 1 held", sheet.What);
        Assert.StartsWith($"ai.AgentPass #{pass.Id}", sheet.Proof);
    }

    [Fact]
    public async Task Approve_first_queues_the_changes_and_writes_nothing()
    {
        await SetMode(AutonomyMode.ApproveFirst);
        ModelAnswersStandardWeek();

        var result = await Agent().RunPassAsync(Layer);

        Assert.Equal(DecisionStatus.Pending, result.Decisions.Single(x => x.Category == "Aksesorë").Status);
        Assert.Equal(DecisionStatus.Held, result.Decisions.Single(x => x.Category == "Celular, Tablet & Navigim").Status);
        Assert.Empty(await _tool.BandCategoryRules.ToListAsync());
    }

    [Fact]
    public async Task Invalid_answers_are_retried_once_then_the_pass_fails_and_nothing_changes()
    {
        await SetMode(AutonomyMode.Autonomous);
        _runner.Answer(Envelope("""{"layer":4,"decisions":[{"bandId":26,"category":"Aksesorë","action":"raise"}],"summary":"x"}"""));
        _runner.Answer(Envelope("""{"layer":4,"decisions":"not a list","summary":"x"}"""));

        var result = await Agent().RunPassAsync(Layer);

        Assert.Equal("Failed", result.Status);
        Assert.Contains("InvalidOutput", result.Error);
        Assert.Equal(2, _runner.Calls);
        Assert.Empty(await _db.AgentDecisions.ToListAsync());
        Assert.Empty(await _tool.BandCategoryRules.ToListAsync());
        Assert.Equal("Failed", (await _db.AgentPasses.SingleAsync()).Status);
        Assert.Equal(2, await _db.AiCallLog.CountAsync());
    }

    [Fact]
    public async Task The_kill_switch_stops_the_pass_before_anything_happens()
    {
        await SetMode(AutonomyMode.Autonomous);

        var result = await Agent(enabled: false).RunPassAsync(Layer);

        Assert.Equal("Skipped", result.Status);
        Assert.Contains("kill switch", result.Error);
        Assert.Equal(0, _runner.Calls);
        Assert.Empty(await _db.AgentPasses.ToListAsync());
    }

    [Fact]
    public async Task A_layer_switched_off_is_not_run()
    {
        await SetMode(AutonomyMode.Off);
        var result = await Agent().RunPassAsync(Layer);
        Assert.Equal(("Skipped", 0), (result.Status, _runner.Calls));
    }

    [Fact]
    public async Task Missing_rows_and_rows_nobody_asked_about_are_rejected()
    {
        await SetMode(AutonomyMode.Autonomous);
        ModelAnswers(Decision("Aksesorë", "keep", 13m, 23m), Decision("Drones", "raise", 14m, 24m));

        var result = await Agent().RunPassAsync(Layer);

        var d = result.Decisions.ToDictionary(x => x.Category);
        Assert.Equal((DecisionStatus.Rejected, "missing"), (d["Celular, Tablet & Navigim"].Status, d["Celular, Tablet & Navigim"].Limit));
        Assert.Equal((DecisionStatus.Rejected, "unknown-row"), (d["Drones"].Status, d["Drones"].Limit));
        Assert.Equal((DecisionStatus.Applied, "keep"), (d["Aksesorë"].Status, d["Aksesorë"].Action));
        Assert.Empty(await _tool.BandCategoryRules.ToListAsync());
    }

    [Fact]
    public async Task A_second_raise_in_the_same_week_counts_from_last_weeks_value()
    {
        await SetMode(AutonomyMode.Autonomous);
        ModelAnswers(Decision("Aksesorë", "raise", 14m, 24m));
        await Agent().RunPassAsync(Layer);                                   // 13 -> 14 applied

        ModelAnswers(Decision("Aksesorë", "raise", 14.5m, 24.5m));
        var second = await Agent().RunPassAsync(Layer);

        var d = second.Decisions.Single(x => x.Category == "Aksesorë");
        Assert.Equal((14m, DecisionStatus.Held, "max-step"), (d.CurrentFloorPct, d.Status, d.Limit));
        Assert.Equal(14m, (await _tool.BandCategoryRules.SingleAsync()).MarginFloorPct);
    }

    [Fact]
    public async Task Test_passes_write_no_time_sheet_row()
    {
        await SetMode(AutonomyMode.Autonomous);
        ModelAnswersStandardWeek();

        await Agent().RunPassAsync(Layer, isTest: true);

        Assert.True((await _db.AgentPasses.SingleAsync()).IsTest);
        Assert.Empty(await _db.TimeSheetEntries.ToListAsync());
    }
}
