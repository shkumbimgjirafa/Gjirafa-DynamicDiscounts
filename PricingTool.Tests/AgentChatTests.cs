using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PricingTool.Agent;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Campaigns;
using PricingTool.Agent.Chat;
using PricingTool.Agent.ClaudeCode;
using PricingTool.Agent.Operations;
using PricingTool.Agent.Storage;
using PricingTool.Agent.Tools;
using PricingTool.Data;
using PricingTool.Data.Services;

namespace PricingTool.Tests;

/// <summary>
/// The Agent tab's chat against scripted Claude Code answers: Nova answers by default and "@alpha" asks Alpha; Nova hands a
/// question to Alpha and answers with Alpha's reply in the thread; an instruction becomes a rule code checks; a campaign request
/// becomes a brief code judges against the campaign limits before the planning loop runs; the Monday report is written by code.
/// </summary>
public class AgentChatTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pt-chat-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AgentDbContext _db = new(new DbContextOptionsBuilder<AgentDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly PricingToolDbContext _tool = new(new DbContextOptionsBuilder<PricingToolDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Queue<string> _answers = new();
    private readonly List<string> _prompts = new();
    private readonly List<long> _planned = new();

    public AgentChatTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _db.Dispose();
        _tool.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class Runner : IProcessRunner
    {
        private readonly AgentChatTests _t;
        public Runner(AgentChatTests t) => _t = t;
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default)
        {
            _t._prompts.Add(spec.StandardInput);
            return Task.FromResult(new ProcessResult(0, _t._answers.Dequeue(), "", false, TimeSpan.FromSeconds(5)));
        }
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private AgentChat Chat()
    {
        var options = Options.Create(new AgentOptions { Enabled = true, RuntimeDirectory = Path.Combine(_dir, "runtime") });
        var client = new ClaudeCodeClient(options, new Runner(this), _db, NullLogger<ClaudeCodeClient>.Instance);
        var reader = new PricingDataReader(new AgentDataSource("unused"));
        var planning = new CampaignPlanning(_db, _tool, new SqlCampaignCandidateSource(new AgentDataSource("unused")), new AgentDataTools(reader, options, new AgentToolLog(_db), _db),
            new BandConfigProvider(_tool), client, new ConfigKillSwitch(options.Value), options, NullLogger<CampaignPlanning>.Instance);
        return new AgentChat(_db, client, planning, options, NullLogger<AgentChat>.Instance, new Clock())
        {
            PlanSource = (id, _) =>
            {
                _planned.Add(id);
                return Task.FromResult(new PlanningResult(id, PlanStatus.Published, new[]
                {
                    new CampaignPlan { Id = 5, BriefId = id, Strategy = "visible", DiscountedSkus = 120, AverageDepthPct = 12m, ExpectedGrossProfit = 4300m,
                        ExpectedMarginPct = 16m, Feasibility = "4,000 EUR gross profit is reachable on the arithmetic.", Status = PlanStatus.Published },
                }, null));
            },
        };
    }

    private void Says(string reply, string? askAlpha = null, JsonObject? brief = null, JsonObject? rule = null, JsonObject? goal = null) => _answers.Enqueue(new JsonObject
    {
        ["type"] = "result", ["subtype"] = "success", ["is_error"] = false, ["num_turns"] = 2, ["result"] = "", ["total_cost_usd"] = 0.03m,
        ["usage"] = new JsonObject { ["input_tokens"] = 900, ["output_tokens"] = 120 },
        ["structured_output"] = new JsonObject { ["reply"] = reply, ["askAlpha"] = askAlpha, ["brief"] = brief, ["rule"] = rule, ["goal"] = goal },
    }.ToJsonString());

    private static JsonObject Brief(string start, string end) => new()
    {
        ["layer"] = 1, ["name"] = "Laptops 3 days", ["scopeKind"] = "category", ["scopeValue"] = "Teknologji", ["startDate"] = start, ["endDate"] = end,
        ["goalType"] = "gross-profit", ["goalValue"] = 4000m, ["minMarginPct"] = 10m, ["maxDepthPct"] = 20m, ["rationale"] = "Asked for in the chat.",
    };

    [Fact]
    public async Task Nova_answers_by_default_and_at_alpha_asks_alpha()
    {
        Says("Black Friday is planned from 27 Nov.");
        var t1 = await Chat().AskAsync(null, "When is Black Friday?", "manager@test");
        Says("Band 26 Aksesorë's floor is 13 %.");
        var t2 = await Chat().AskAsync(t1.ConversationId, "@alpha what is the floor of band 26 Aksesorë?", "manager@test");

        Assert.Equal(new[] { "user", "Nova" }, t1.Added.Select(m => m.From));
        Assert.Equal(new[] { "user", "Alpha" }, t2.Added.Select(m => m.From));
        Assert.Contains("You are Alpha", _prompts[1]);
        Assert.Contains("[user] When is Black Friday?", _prompts[1]);                                 // the conversation so far
        Assert.Equal(("Nova", "Alpha"), ((await _db.AiCallLog.OrderBy(c => c.Id).FirstAsync()).Agent, (await _db.AiCallLog.OrderBy(c => c.Id).LastAsync()).Agent));
    }

    [Fact]
    public async Task The_agents_see_their_own_campaign_records_which_the_data_tools_dont_show()
    {
        var entry = new CampaignCalendarEntry { LayerId = 1, Name = "Black Friday 2026", StartDate = new DateOnly(2026, 11, 27), EndDate = new DateOnly(2026, 11, 30),
            Status = CampaignStatus.Planned, GoalType = "gross-profit", GoalValue = 57456m, GoalSetBy = "user, 2026-10-30", CreatedBy = "test", CreatedUtc = Now };
        _db.CampaignCalendar.Add(entry);
        await _db.SaveChangesAsync();
        var brief = new CampaignBrief { PassId = 1, LayerId = 1, CalendarId = entry.Id, Name = "Black Friday 2026", Kind = "calendar", GoalType = "gross-profit", GoalValue = 57456m,
            StartDate = entry.StartDate, EndDate = entry.EndDate, Status = BriefStatus.Planned, Exclusions = "[]", CreatedUtc = Now };
        _db.CampaignBriefs.Add(brief);
        await _db.SaveChangesAsync();
        _db.CampaignPlans.Add(new CampaignPlan { BriefId = brief.Id, LayerId = 1, Strategy = "visible", Status = PlanStatus.Held, GoalVerdict = GoalVerdicts.NotReachable,
            ExpectedGrossProfit = 29828m, Feasibility = "57,456 EUR gross profit is not reachable on the arithmetic.", CreatedUtc = Now });
        await _db.SaveChangesAsync();
        Says("It's held: the plan expects 49k against your 57k.");

        await Chat().AskAsync(null, "Where does Black Friday stand?", "manager@test");

        Assert.Contains("Black Friday 2026, layer 1", _prompts[0]);
        Assert.Contains("set by a person", _prompts[0]);
        Assert.Contains("held (not-reachable)", _prompts[0]);
        Assert.Contains("is not reachable on the arithmetic", _prompts[0]);
    }

    [Fact]
    public async Task Nova_hands_a_pricing_question_to_alpha_and_answers_with_its_reply()
    {
        Says("Let me ask Alpha.", askAlpha: "What margin does Kozmetikë make at 15 % off?");
        Says("About 9 %, estimated from the floor rule.");
        Says("Alpha says about 9 % at 15 % off, so 10 % off keeps it above the floor.");

        var t = await Chat().AskAsync(null, "How deep can Kozmetikë go?", "manager@test");

        Assert.Null(t.Error);
        Assert.Equal(new[] { "user", "Nova", "Alpha", "Nova" }, t.Added.Select(m => m.From));
        Assert.StartsWith("Asking Alpha:", t.Added[1].Text);
        Assert.Contains("Nova hands you this question", _prompts[1]);
        Assert.Contains("[Alpha] About 9 %", _prompts[2]);                                             // Nova sees Alpha's answer
    }

    [Fact]
    public async Task An_instruction_becomes_a_rule_and_a_bad_one_is_refused()
    {
        Says("Samsung stays out of campaigns this week.", rule: new JsonObject { ["layer"] = null, ["kind"] = "exclude-brand", ["value"] = "Samsung", ["fromDate"] = "2026-11-02", ["toDate"] = "2026-11-08" });
        var t = await Chat().AskAsync(null, "Keep Samsung out this week", "manager@test");
        var rule = await _db.ChatRules.SingleAsync();
        Assert.Equal(("exclude-brand", "Samsung", new DateOnly(2026, 11, 8), "manager@test"), (rule.Kind, rule.Value, rule.ToDate, rule.CreatedBy));
        Assert.Equal(rule.Id, t.Added[1].RuleId);
        Assert.Contains($"Rule #{rule.Id} in force", t.Added[2].Text);

        Says("Campaigns keep at least lots.", rule: new JsonObject { ["layer"] = 1, ["kind"] = "min-campaign-margin", ["value"] = "lots", ["fromDate"] = "2026-11-02", ["toDate"] = "2026-11-30" });
        var bad = await Chat().AskAsync(t.ConversationId, "Keep a good margin", "manager@test");
        Assert.StartsWith("No rule recorded", bad.Added[^1].Text);
        Assert.Single(await _db.ChatRules.ToListAsync());
    }

    [Fact]
    public async Task A_goal_is_recorded_for_the_whole_period_replaces_the_one_before_and_a_bad_one_is_refused()
    {
        Says("Noted: €180k for GjirafaMall in November.", goal: new JsonObject { ["layer"] = 1, ["period"] = "month", ["start"] = "2026-11-15", ["kind"] = "gross-profit", ["value"] = 180000m });
        var t = await Chat().AskAsync(null, "GjirafaMall should make 180k gross profit this month", "manager@test");
        var first = await _db.Goals.SingleAsync();
        Assert.Equal((1, "month", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), "gross-profit", 180000m, "manager@test"),
            (first.LayerId, first.Period, first.Start, first.End, first.Kind, first.Value, first.SetBy));
        Assert.Contains($"Goal #{first.Id} set", t.Added[^1].Text);

        Says("Raised to €190k.", goal: new JsonObject { ["layer"] = 1, ["period"] = "month", ["start"] = "2026-11-02", ["kind"] = "gross-profit", ["value"] = 190000m });
        await Chat().AskAsync(t.ConversationId, "Make it 190k", "manager@test");
        var active = await Goals.ActiveAsync(_db, new DateOnly(2026, 11, 2));
        Assert.Equal(190000m, Assert.Single(active).Value);                                         // the newer replaces the older
        Assert.NotNull((await _db.Goals.SingleAsync(g => g.Id == first.Id)).RetiredUtc);

        Says("Gjirafa50 at 12% this week.", goal: new JsonObject { ["layer"] = 4, ["period"] = "week", ["start"] = "2026-11-04", ["kind"] = "margin", ["value"] = 12m });
        await Chat().AskAsync(t.ConversationId, "Gjirafa50 12% margin this week", "manager@test");
        var week = await _db.Goals.SingleAsync(g => g.Period == "week");
        Assert.Equal((new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 8)), (week.Start, week.End));  // Monday to Sunday

        Says("Noted.", goal: new JsonObject { ["layer"] = 1, ["period"] = "month", ["start"] = "2026-10-05", ["kind"] = "gross-profit", ["value"] = 150000m });
        var past = await Chat().AskAsync(t.ConversationId, "October should have been 150k", "manager@test");
        Assert.StartsWith("No goal recorded: That month ended", past.Added[^1].Text);
        Assert.Equal(3, await _db.Goals.CountAsync());
    }

    [Fact]
    public async Task A_campaign_request_is_judged_by_code_before_it_is_planned()
    {
        Says("I'll brief a 3-day laptops campaign from 9 Nov.", brief: Brief("2026-11-09", "2026-11-11"));
        var ok = await Chat().AskAsync(null, "Plan a 3-day laptops campaign next week, 4k profit", "manager@test");

        var brief = await _db.CampaignBriefs.SingleAsync();
        Assert.Equal((BriefStatus.Proposed, "mini"), (brief.Status, brief.Kind));
        Assert.Equal(new[] { brief.Id }, _planned);
        Assert.NotNull(brief.CalendarId);                                                             // on the calendar, so the Campaigns tab shows it
        Assert.Equal("manager@test in the chat (conversation 1)", (await _db.CampaignCalendar.SingleAsync(c => c.Id == brief.CalendarId)).GoalSetBy);  // the person's goal: binding
        Assert.Contains("passes the campaign limits", ok.Added[2].Text);
        Assert.Contains("Plan #5 (visible)", ok.Added[3].Text);
        Assert.Contains("a person can stop it", ok.Added[3].Text);

        Says("Tomorrow, then.", brief: Brief("2026-11-03", "2026-11-05"));                            // inside the 3-day lead time
        var tooSoon = await Chat().AskAsync(ok.ConversationId, "Make it tomorrow", "manager@test");
        Assert.Contains("rejected (lead-time)", tooSoon.Added[^1].Text);
        Assert.Single(_planned);                                                                       // not planned
    }

    [Fact]
    public async Task The_weekly_report_is_written_by_code_as_a_new_conversation()
    {
        _db.CampaignCalendar.Add(new CampaignCalendarEntry { LayerId = 1, Name = "Black Friday 2026", StartDate = new DateOnly(2026, 11, 20), EndDate = new DateOnly(2026, 11, 23),
            Status = CampaignStatus.Planned, CreatedBy = "test", CreatedUtc = Now });
        await _db.SaveChangesAsync();
        var t = await Chat().WeeklyReportAsync();
        var m = Assert.Single(t.Added);
        Assert.Equal("Nova", m.From);
        Assert.Contains("Weekly report", m.Text);
        Assert.Contains("Black Friday 2026 from Fri 20 Nov", m.Text);
        Assert.Empty(_prompts);                                                                        // no model call
    }
}
