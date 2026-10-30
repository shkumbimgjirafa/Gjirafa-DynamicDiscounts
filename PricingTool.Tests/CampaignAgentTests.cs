using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PricingTool.Agent;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Campaigns;
using PricingTool.Agent.ClaudeCode;
using PricingTool.Agent.Operations;
using PricingTool.Agent.Storage;
using PricingTool.Agent.Tools;
using PricingTool.Data;

namespace PricingTool.Tests;

/// <summary>
/// Nova's weekly pass against a scripted Claude Code answer: code judges every brief against the campaign limits
/// (rejects, holds, caps the depth), puts a proposed mini-campaign on the calendar, records finished campaigns' results,
/// and a failed answer or a test pass changes nothing on the calendar.
/// </summary>
public class CampaignAgentTests : IDisposable
{
    private const int Layer = 1;
    private static readonly DateOnly Today = new(2026, 11, 2);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pt-nova-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AgentDbContext _db;
    private readonly PricingToolDbContext _tool;
    private readonly Queue<string> _answers = new();
    private long _blackFriday;
    private long _finished;

    public CampaignAgentTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new AgentDbContext(new DbContextOptionsBuilder<AgentDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _db.Database.EnsureCreated();
        _tool = new PricingToolDbContext(new DbContextOptionsBuilder<PricingToolDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var bf = Entry("Black Friday 2026", "main", new DateOnly(2026, 11, 27), new DateOnly(2026, 11, 30), CampaignStatus.Planned);
        var done = Entry("Kozmetikë week", "mini", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5), CampaignStatus.Done);
        var planned = Entry("Vegla Pune days", "mini", new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 13), CampaignStatus.Planned);
        _db.CampaignCalendar.AddRange(bf, done, planned);
        _db.SaveChanges();
        (_blackFriday, _finished) = (bf.Id, done.Id);
    }

    private static CampaignCalendarEntry Entry(string name, string type, DateOnly start, DateOnly end, string status) => new()
    {
        LayerId = Layer, Name = name, Type = type, StartDate = start, EndDate = end, Status = status,
        ScopeKind = type == "main" ? "all" : "category", ScopeValue = type == "main" ? null : name.Split(' ')[0], CreatedBy = "test", CreatedUtc = DateTime.UtcNow,
    };

    public void Dispose()
    {
        _db.Dispose();
        _tool.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class Runner : IProcessRunner
    {
        private readonly CampaignAgentTests _t;
        public Runner(CampaignAgentTests t) => _t = t;
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default) =>
            Task.FromResult(new ProcessResult(0, _t._answers.Dequeue(), "", false, TimeSpan.FromSeconds(30)));
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Today.ToDateTime(new TimeOnly(7, 30)), TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private CampaignAgent Nova()
    {
        var options = Options.Create(new AgentOptions
        {
            Enabled = true, RuntimeDirectory = Path.Combine(_dir, "runtime"),
            Limits = new SafetyLimitOptions { Holdouts = new() { ["1"] = new() { "Sport", "Vegla Pune" } } },
        });
        var clock = new Clock();
        var reader = new PricingDataReader(new AgentDataSource("unused"));
        var client = new ClaudeCodeClient(options, new Runner(this), _db, NullLogger<ClaudeCodeClient>.Instance);
        var tools = new AgentDataTools(reader, options, new AgentToolLog(_db, clock), _db, clock);
        return new CampaignAgent(client, _db, reader, tools, new CampaignCalendar(_db, reader, options, clock), new AgentModeStore(_tool),
            new ConfigKillSwitch(options.Value), options, NullLogger<CampaignAgent>.Instance, clock)
        {
            InputSource = (_, _, _) => Task.FromResult("Category trends: (test data)"),
        };
    }

    private static JsonObject Brief(string kind, long? calendarId, string name, string scopeKind, string? scope, string start, string end,
        decimal maxDepth = 20m) => new()
    {
        ["kind"] = kind, ["calendarId"] = calendarId, ["name"] = name, ["scopeKind"] = scopeKind, ["scopeValue"] = scope,
        ["startDate"] = start, ["endDate"] = end, ["goalType"] = "gross-profit", ["goalValue"] = 15000m, ["minMarginPct"] = 8m,
        ["maxDepthPct"] = maxDepth, ["exclusions"] = new JsonArray("Apple"), ["rationale"] = $"{name}: test rationale.", ["evidenceRefs"] = new JsonArray("trends"),
    };

    private void ModelSays(int layer, params JsonObject[] briefs) => _answers.Enqueue(new JsonObject
    {
        ["type"] = "result", ["subtype"] = "success", ["is_error"] = false, ["num_turns"] = 4, ["result"] = "", ["total_cost_usd"] = 0.4m,
        ["usage"] = new JsonObject { ["input_tokens"] = 3000, ["output_tokens"] = 1500 },
        ["structured_output"] = new JsonObject
        {
            ["layer"] = layer, ["briefs"] = new JsonArray(briefs.Select(b => (JsonNode)b).ToArray()),
            ["resultsReview"] = new JsonArray(new JsonObject { ["calendarId"] = _finished, ["lesson"] = "Kozmetikë sells on depth; keep it short." }),
            ["summary"] = "Test pass.",
        },
    }.ToJsonString());

    [Fact]
    public async Task Code_judges_every_brief_and_puts_only_proposed_mini_campaigns_on_the_calendar()
    {
        ModelSays(Layer,
            Brief("calendar", _blackFriday, "Black Friday 2026", "all", null, "2026-11-27", "2026-11-30"),
            Brief("mini", null, "Kozmetikë days", "category", "Kozmetikë", "2026-11-09", "2026-11-13", maxDepth: 40m),   // depth capped
            Brief("mini", null, "Sport week", "category", "Sport", "2026-11-10", "2026-11-14"),                          // holdout
            Brief("mini", null, "Fëmijë before Black Friday", "category", "Fëmijë", "2026-11-24", "2026-11-28"),        // overlaps a main
            Brief("mini", null, "Shtëpi days", "category", "Shtëpi", "2026-11-10", "2026-11-12"),                       // a third at once
            Brief("calendar", _blackFriday, "Black Friday long", "all", null, "2026-11-20", "2026-11-30"),              // outside its window
            Brief("mini", null, "Tomorrow", "category", "Teknologji", "2026-11-03", "2026-11-05"));                      // no lead time

        var r = await Nova().RunPassAsync(Layer);

        Assert.Equal("Succeeded", r.Status);
        var b = r.Briefs.ToDictionary(x => x.Name);
        Assert.Equal((BriefStatus.Proposed, (string?)null), (b["Black Friday 2026"].Status, b["Black Friday 2026"].Limit));
        Assert.Equal((BriefStatus.Proposed, "max-depth", 25m), (b["Kozmetikë days"].Status, b["Kozmetikë days"].Limit, b["Kozmetikë days"].MaxDepthPct));
        Assert.Equal((BriefStatus.Rejected, "holdout"), (b["Sport week"].Status, b["Sport week"].Limit));
        Assert.Equal((BriefStatus.Rejected, "overlaps-main"), (b["Fëmijë before Black Friday"].Status, b["Fëmijë before Black Friday"].Limit));
        Assert.Equal((BriefStatus.Held, "max-concurrent"), (b["Shtëpi days"].Status, b["Shtëpi days"].Limit));
        Assert.Equal((BriefStatus.Rejected, "outside-calendar"), (b["Black Friday long"].Status, b["Black Friday long"].Limit));
        Assert.Equal((BriefStatus.Rejected, "lead-time"), (b["Tomorrow"].Status, b["Tomorrow"].Limit));

        var proposed = await _db.CampaignCalendar.SingleAsync(c => c.CreatedBy == CampaignAgent.Source);
        Assert.Equal(("Kozmetikë days", CampaignStatus.Proposed, "mini", proposed.Id), (proposed.Name, proposed.Status, proposed.Type, b["Kozmetikë days"].CalendarId!.Value));
        var bf = await _db.CampaignCalendar.SingleAsync(c => c.Id == _blackFriday);
        Assert.Equal(("gross-profit", 15000m), (bf.GoalType, bf.GoalValue!.Value));     // the calendar campaign takes the brief's goal

        var result = await _db.CampaignResults.SingleAsync();                              // the finished mini-campaign
        Assert.Equal((_finished, "Kozmetikë sells on depth; keep it short."), (result.CalendarId, result.Lesson));
        var pass = await _db.AgentPasses.SingleAsync();
        Assert.Equal((CampaignAgent.Source, 2, 1, 4), (pass.Source, pass.Applied, pass.Held, pass.Rejected));
        Assert.Single(new ExceptionsInbox(_db, _tool).LoadAsync().Result.HeldBriefs);
    }

    [Fact]
    public async Task A_failed_answer_or_a_test_pass_puts_nothing_on_the_calendar()
    {
        ModelSays(4, Brief("mini", null, "Wrong layer", "category", "Kozmetikë", "2026-11-09", "2026-11-13"));
        var failed = await Nova().RunPassAsync(Layer);
        Assert.Equal("Failed", failed.Status);
        Assert.Empty(await _db.CampaignBriefs.ToListAsync());

        ModelSays(Layer, Brief("mini", null, "Test mini", "category", "Kozmetikë", "2026-11-09", "2026-11-13"));
        var test = await Nova().RunPassAsync(Layer, isTest: true);
        Assert.Equal((BriefStatus.Proposed, true), (test.Briefs.Single().Status, test.Briefs.Single().IsTest));
        Assert.Equal(3, await _db.CampaignCalendar.CountAsync());                          // nothing added
        Assert.Empty(await _db.CampaignResults.ToListAsync());
    }
}
