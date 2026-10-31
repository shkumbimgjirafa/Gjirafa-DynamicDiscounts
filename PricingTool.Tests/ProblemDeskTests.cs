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
using PricingTool.Data.Entities;
using PricingTool.Data.Services;

namespace PricingTool.Tests;

/// <summary>Alpha's problem pass: code settles first, Alpha names who can fix the rest, and only its one action goes through.</summary>
public class ProblemDeskTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pt-problems-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AgentDbContext _db = new(new DbContextOptionsBuilder<AgentDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly PricingToolDbContext _tool = new(new DbContextOptionsBuilder<PricingToolDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Queue<string> _answers = new();
    private int _calls;

    public ProblemDeskTests()
    {
        Directory.CreateDirectory(_dir);
        _tool.Layers.Add(new Layer { Id = 1, Brand = "GjirafaMall", CountryCode = "KS", DisplayName = "GjirafaMall — Kosovo", StoreId = 2, Currency = "EUR", IsActive = true });
        _tool.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _tool.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class Runner : IProcessRunner
    {
        private readonly ProblemDeskTests _t;
        public Runner(ProblemDeskTests t) => _t = t;
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default)
        {
            _t._calls++;
            return Task.FromResult(new ProcessResult(0, _t._answers.Dequeue(), "", false, TimeSpan.FromSeconds(30)));
        }
    }

    private sealed class Resolver : IHoldResolver
    {
        public List<long> Confirmed { get; } = new();
        public HoldReview Review { get; set; } = new(0, 0, 0, Array.Empty<string>());
        public Task<HoldReview> ReviewAsync(CancellationToken ct = default) => Task.FromResult(Review);
        public Task<HoldReview> ConfirmRealRiseAsync(IReadOnlyCollection<long> decisionIds, CancellationToken ct = default)
        {
            Confirmed.AddRange(decisionIds);
            return Task.FromResult(new HoldReview(0, decisionIds.Count, 0, Array.Empty<string>()));
        }
    }

    private ProblemDesk Desk(Resolver resolver)
    {
        var options = Options.Create(new AgentOptions { Enabled = true, RuntimeDirectory = Path.Combine(_dir, "runtime") });
        var client = new ClaudeCodeClient(options, new Runner(this), _db, NullLogger<ClaudeCodeClient>.Instance);
        var reader = new PricingDataReader(new AgentDataSource("unused"));
        var planning = new CampaignPlanning(_db, _tool, new SqlCampaignCandidateSource(new AgentDataSource("unused")), new AgentDataTools(reader, options, new AgentToolLog(_db), _db),
            new BandConfigProvider(_tool), client, new ConfigKillSwitch(options.Value), options, NullLogger<CampaignPlanning>.Instance);
        var chat = new AgentChat(_db, client, planning, options, NullLogger<AgentChat>.Instance);
        return new ProblemDesk(_db, _tool, new ExceptionsInbox(_db, _tool), client, chat, options, NullLogger<ProblemDesk>.Instance, resolver);
    }

    private AgentDecision Hold(long passId, string sku, string limit) => new()
    {
        PassId = passId, LayerId = 1, BandId = 3, Category = "Teknologji", Source = "CostWatcher", Action = "hold", Status = DecisionStatus.Held,
        Limit = limit, Reason = $"{limit} on {sku}", EvidenceRefs = $$"""{"sku":"{{sku}}","newCost":60.0,"floorPrice":70.0}""", CreatedUtc = DateTime.UtcNow,
    };

    private static string Envelope(JsonNode structured) => new JsonObject
    {
        ["type"] = "result", ["subtype"] = "success", ["is_error"] = false, ["duration_ms"] = 30000, ["num_turns"] = 3,
        ["result"] = "", ["total_cost_usd"] = 0.4m,
        ["usage"] = new JsonObject { ["input_tokens"] = 4000, ["output_tokens"] = 800 },
        ["structured_output"] = structured,
    }.ToJsonString();

    [Fact]
    public async Task Alpha_names_who_can_fix_each_hold_and_only_a_data_error_can_be_confirmed_as_real()
    {
        _db.AgentPasses.Add(new AgentPass { Id = 1, LayerId = 1, Source = "CostWatcher", Mode = "Code", StartedUtc = DateTime.UtcNow, Status = "Succeeded" });
        var shelf = Hold(1, "A", "cost-above-shelf");
        var jump = Hold(1, "B", "data-error-cost-jump");
        _db.AgentDecisions.AddRange(shelf, jump);
        await _db.SaveChangesAsync();
        _answers.Enqueue(Envelope(new JsonObject
        {
            ["post"] = "Two SKUs are still open: one needs its regular price changed, one cost rise is real.",
            ["items"] = new JsonArray(
                new JsonObject { ["key"] = $"hold:{shelf.Id}", ["action"] = "confirm-real-rise", ["owner"] = "regular-prices", ["note"] = "Cost 60 is above the 58.99 shelf." },
                new JsonObject { ["key"] = $"hold:{jump.Id}", ["action"] = "confirm-real-rise", ["owner"] = "cost-data", ["note"] = "The supplier raised 12 SKUs alike." },
                new JsonObject { ["key"] = "hold:999", ["action"] = "report", ["owner"] = "pricing-tool", ["note"] = "not one of ours" }),
        }));
        var resolver = new Resolver { Review = new HoldReview(3, 2, 2, new[] { "X: ended" }) };

        var r = await Desk(resolver).RunAsync();

        Assert.Equal(("Succeeded", 1, 1), (r.Status, r.Reported, r.Confirmed));
        Assert.Equal(new[] { jump.Id }, resolver.Confirmed);                         // the confirm on the cost-above-shelf hold is ignored
        var reported = await _db.AgentDecisions.SingleAsync(d => d.Id == shelf.Id);
        Assert.Equal(("Category manager (sets regular prices)", "Cost 60 is above the 58.99 shelf."), (reported.ReportedTo, reported.ReportNote));
        var post = await _db.ChatMessages.SingleAsync();
        Assert.Equal("Alpha", post.From);
        Assert.StartsWith("I ended the team discounts that kept 2 SKUs under the floor", post.Text);
        Assert.Contains("3 held SKUs are back at or above the floor", post.Text);
        Assert.Contains("Two SKUs are still open", post.Text);
        Assert.Equal(1, _calls);
    }

    [Fact]
    public async Task With_nothing_open_there_is_no_call_and_what_code_settled_is_still_said()
    {
        var resolver = new Resolver { Review = new HoldReview(1, 0, 0, Array.Empty<string>()) };

        var r = await Desk(resolver).RunAsync();

        Assert.Equal(0, _calls);
        Assert.Equal("Succeeded", r.Status);
        Assert.Contains("Nothing else needs a person", (await _db.ChatMessages.SingleAsync()).Text);

        var quiet = await Desk(new Resolver()).RunAsync();
        Assert.Equal(("Skipped", 0), (quiet.Status, _calls));
    }
}
