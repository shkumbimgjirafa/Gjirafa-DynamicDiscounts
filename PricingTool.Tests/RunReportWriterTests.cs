using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PricingTool.Agent;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.ClaudeCode;
using PricingTool.Agent.Operations;
using PricingTool.Agent.Storage;
using PricingTool.Data;
using PricingTool.Data.Entities;

namespace PricingTool.Tests;

/// <summary>A published run's report: the numbers code stores, Alpha's account of them, and what a failed or missing answer leaves.</summary>
public class RunReportWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pt-report-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AgentDbContext _db = new(new DbContextOptionsBuilder<AgentDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly PricingToolDbContext _tool = new(new DbContextOptionsBuilder<PricingToolDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Runner _runner = new();
    private readonly DateTime _now = DateTime.UtcNow;

    public RunReportWriterTests()
    {
        Directory.CreateDirectory(_dir);
        _tool.Layers.Add(new Layer { Id = 4, Brand = "Gjirafa50", CountryCode = "KS", DisplayName = "Gjirafa50 — Kosovo", StoreId = 2, Currency = "EUR", VatRatePct = 18m, IsActive = true });
        _tool.PricingRunBandTargets.AddRange(
            new PricingRunBandTarget { PricingRunId = 1109, LayerId = 4, BandId = 25, Name = "0–10", MinPrice = 0, MaxPrice = 10, MarginFloorPct = 15m, TargetMarginPct = 35m },
            new PricingRunBandTarget { PricingRunId = 1109, LayerId = 4, BandId = 26, Name = "10–50", MinPrice = 10, MaxPrice = 50, MarginFloorPct = 13m, TargetMarginPct = 23m });
        _tool.PricingRunCategoryRules.Add(new PricingRunCategoryRule { PricingRunId = 1109, LayerId = 4, BandId = 26, Category = "Aksesorë", MarginFloorPct = 14m, TargetMarginPct = 24m, Skus = 300 });
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
        private readonly Queue<string> _stdout = new();
        public int Calls { get; private set; }
        public void Answer(string stdout) => _stdout.Enqueue(stdout);

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new ProcessResult(0, _stdout.Dequeue(), "", false, TimeSpan.FromSeconds(20)));
        }
    }

    private sealed class Facts : IRunFacts
    {
        public Task<IReadOnlyList<RunGridCell>?> GridAsync(long runId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RunGridCell>?>(new[]
            {
                new RunGridCell("Aksesorë", 26, 300, 120, 100, 20),
                new RunGridCell("Gaming", 26, 200, 50, 10, 40),           // a holdout category: changed, never published
                new RunGridCell("Aksesorë", 25, 100, 0, 0, 0),
            });

        public List<IReadOnlyCollection<string>> Holdouts { get; } = new();

        public Task<ShelfEffect?> ShelfEffectAsync(long batchId, long runId, int layerId, decimal vatRatePct, IReadOnlyCollection<string> holdouts, CancellationToken ct = default)
        {
            Holdouts.Add(holdouts);
            return Task.FromResult<ShelfEffect?>(new ShelfEffect(12845, 229, 554, 925, 1534m, 11920, -3582m, -2047m, -1756m, -1352m));
        }
    }

    private static string Envelope(JsonNode structured) => new JsonObject
    {
        ["type"] = "result", ["subtype"] = "success", ["is_error"] = false, ["duration_ms"] = 20000, ["num_turns"] = 1,
        ["result"] = "", ["total_cost_usd"] = 0.3m,
        ["usage"] = new JsonObject { ["input_tokens"] = 3000, ["output_tokens"] = 600 },
        ["structured_output"] = structured,
    }.ToJsonString();

    private void AlphaWrites(string headline) => _runner.Answer(Envelope(new JsonObject
    {
        ["headline"] = headline,
        ["report"] = "I kept every floor and target this week. The run changed 170 prices, mostly raises in Aksesorë 10–50; 900 of 1,200 reached the shelf, and cheaper team deals kept the rest off it. Expected −€189 a month, an estimate with no change in volume.",
        ["watch"] = new JsonArray("Aksesorë 10–50: the raises a team deal blocks"),
    }));

    private readonly Facts _facts = new();

    private RunReportWriter Writer() => new(_db, _tool, _facts,
        new ClaudeCodeClient(Microsoft.Extensions.Options.Options.Create(AgentSettings()), _runner, _db, NullLogger<ClaudeCodeClient>.Instance),
        Microsoft.Extensions.Options.Options.Create(AgentSettings()), NullLogger<RunReportWriter>.Instance);

    private AgentOptions AgentSettings() => new()
    {
        Enabled = true,
        RuntimeDirectory = Path.Combine(_dir, "runtime"),
        Limits = new SafetyLimitOptions { Holdouts = new() { ["4"] = new() { "Gaming" } } },
        Cycle = new() { Layers = new() { 4 } },
    };

    private PublishBatch Batch(long id, long? run, string status = "Published", string kind = "Regular", DateTime? at = null) => new()
    {
        Id = id, LayerId = 4, Db = "GjirafaEcommerce", StoreId = 2, Target = "Sandbox", ShopSchema = "shop_sandbox_GjirafaEcommerce",
        Kind = kind, Status = status, EngineRunId = run, StartedUtc = at ?? _now, FinishedUtc = at ?? _now,
        Requested = 1200, Created = 400, Updated = 480, Unchanged = 13, TierRaised = 7, Skipped = 300,
    };

    private async Task SeedPublishAsync()
    {
        _db.PublishBatches.AddRange(Batch(10100, 1106, at: _now.AddHours(-1)), Batch(10102, 1109), Batch(9, null, "Failed"));
        _db.PublishExceptions.AddRange(
            new PublishException { BatchId = 10102, Sku = "A", Kind = "cheaper-deal", Detail = "", CreatedUtc = _now },
            new PublishException { BatchId = 10102, Sku = "A", Kind = "cheaper-deal", Detail = "", CreatedUtc = _now },   // the same SKU twice
            new PublishException { BatchId = 10102, Sku = "B", Kind = "cheaper-deal", Detail = "", CreatedUtc = _now },
            new PublishException { BatchId = 10102, Sku = "C", Kind = "changed-since-run", Detail = "", CreatedUtc = _now });
        _db.AgentRunChecks.Add(new AgentRunCheck { RunId = 1109, LayerId = 4, StartedUtc = _now, Status = "Succeeded", Verdict = "warn", Warns = 6770 });
        _db.AgentPasses.Add(new AgentPass { LayerId = 4, Source = FloorTargetAgent.Source, Mode = "Autonomous", StartedUtc = _now.AddMinutes(-10), Status = "Succeeded", Summary = "All keeps." });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_publish_gets_its_numbers_by_code_and_Alpha_writes_about_them_once()
    {
        await SeedPublishAsync();
        AlphaWrites("A run of raises that mostly didn't reach the shelf.");

        var report = await Writer().WriteAsync(10102);

        Assert.NotNull(report);
        Assert.Equal((1109L, 10100L, 600, 170, 110, 60, 50), (report!.RunId, report.PreviousBatchId!.Value, report.PricedSkus, report.Changed, report.Raised, report.Lowered, report.HoldoutChanged));
        Assert.Equal((1200, 900, 300, 6770), (report.Requested, report.OnShelf, report.LeftOut, report.HeldByCheck));
        Assert.Equal("""{"cheaper-deal":2,"changed-since-run":1}""", report.LeftOutByKind);   // once per SKU
        Assert.Equal((-2047m, 12845, 229), (report.ExpectedGp30Eur!.Value, report.ExpectedSkus, report.ExpectedSoldSkus));
        Assert.Equal((925, 1534m, 11920, -3582m), (report.ExpectedRaises, report.ExpectedFromRaisesEur!.Value, report.ExpectedCuts, report.ExpectedFromCutsEur!.Value));
        Assert.Equal((-1756m, -1352m), (report.ExpectedE1Eur!.Value, report.ExpectedE2Eur!.Value));
        Assert.Equal(new[] { "Gaming" }, _facts.Holdouts[0]);                                                  // the layer's holdout categories, never published
        Assert.Equal(("written", "A run of raises that mostly didn't reach the shelf."), (report.AlphaStatus, report.Headline));
        Assert.Equal("""["Aksesorë 10–50: the raises a team deal blocks"]""", report.AlphaWatch);
        Assert.NotNull(report.AlphaPassId);

        var cells = await _db.RunReportCells.Where(c => c.ReportId == report.Id).ToListAsync();
        var set = cells.Single(c => c.Category == "Aksesorë" && c.BandId == 26);
        Assert.Equal((14m, 24m, true), (set.FloorPct!.Value, set.TargetPct!.Value, set.ByAlpha));            // Alpha's category rule
        var band = cells.Single(c => c.Category == "Aksesorë" && c.BandId == 25);
        Assert.Equal((15m, 35m, false), (band.FloorPct!.Value, band.TargetPct!.Value, band.ByAlpha));          // the band's own
        Assert.True(cells.Single(c => c.Category == "Gaming").Holdout);

        var again = await Writer().WriteAsync(10102);
        Assert.Equal(report.Id, again!.Id);
        Assert.Equal(1, _runner.Calls);                                                                         // written once
    }

    [Fact]
    public async Task A_failed_answer_keeps_the_numbers_and_says_why_and_a_publish_without_a_run_gets_none()
    {
        await SeedPublishAsync();
        _runner.Answer(Envelope(new JsonObject { ["headline"] = "", ["report"] = "", ["watch"] = new JsonArray() }));
        _runner.Answer(Envelope(new JsonObject { ["headline"] = "", ["report"] = "", ["watch"] = new JsonArray() }));

        var report = await Writer().WriteAsync(10102);

        Assert.Equal(900, report!.OnShelf);
        Assert.Null(report.AlphaText);
        Assert.StartsWith("failed:", report.AlphaStatus);
        Assert.Null(await Writer().WriteAsync(9, withAlpha: false));                                            // the cost watcher's one-SKU publish
    }

    [Fact]
    public async Task A_backlog_costs_one_call_per_layer_for_its_newest_publish()
    {
        await SeedPublishAsync();
        AlphaWrites("The newest run.");

        var written = await Writer().WriteMissingAsync();

        Assert.Equal(new long[] { 10100, 10102 }, written.Select(r => r.BatchId));                     // oldest first
        Assert.Equal("not asked", written[0].AlphaStatus);
        Assert.Equal("written", written[1].AlphaStatus);
        Assert.Equal(10100, written[1].PreviousBatchId);
        Assert.Equal(1, _runner.Calls);
        Assert.Empty(await Writer().WriteMissingAsync());                                                       // nothing left
    }
}
