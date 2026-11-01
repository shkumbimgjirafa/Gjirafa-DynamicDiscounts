using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PricingTool.Agent;
using PricingTool.Agent.Checker;
using PricingTool.Agent.ClaudeCode;
using PricingTool.Agent.Storage;
using PricingTool.Core.Domain;
using PricingTool.Data;
using PricingTool.Data.Entities;

namespace PricingTool.Tests;

/// <summary>
/// The run check end to end on injected, labelled bad rows ([INJECTED-TEST], in this in-memory test database
/// only) against a fake Claude Code process: the verdict, what code does with the model's answer, and that a
/// block stops the export.
/// </summary>
public class RunCheckerTests : IDisposable
{
    private const long Run = 5001;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pt-check-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AgentDbContext _db;
    private readonly PricingToolDbContext _tool;
    private readonly Queue<string> _answers = new();
    private int _calls;

    public RunCheckerTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new AgentDbContext(new DbContextOptionsBuilder<AgentDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _db.Database.EnsureCreated();
        _tool = new PricingToolDbContext(new DbContextOptionsBuilder<PricingToolDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _tool.Layers.Add(new Layer { Id = 4, Brand = "Gjirafa50", CountryCode = "KS", DisplayName = "Gjirafa50 — Kosovo", OperationalDatabase = "GjirafaEcommerce", StoreId = 2, Currency = "EUR", VatRatePct = 18m });
        _tool.PriceBands.Add(new PriceBand { Id = 26, LayerId = 4, Name = "10–50", MinPrice = 10, MaxPrice = 50, MarginFloorPct = 13, TargetMarginPct = 23,
            RoundingConvention = (int)RoundingConvention.Gj50Charm, RoundingEnabled = true });
        _tool.PricingRuns.Add(new PricingRun { Id = Run, LayerId = 4, Status = RunStatus.Succeeded, StartedUtc = new DateTime(2026, 10, 30, 16, 6, 0) });
        _tool.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _tool.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class Runner : IProcessRunner
    {
        private readonly RunCheckerTests _t;
        public Runner(RunCheckerTests t) => _t = t;
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default)
        {
            _t._calls++;
            return Task.FromResult(new ProcessResult(0, _t._answers.Dequeue(), "", false, TimeSpan.FromSeconds(20)));
        }
    }

    /// <summary>An injected, labelled test price (never in the hackathon database).</summary>
    private void Injected(long id, string sku, decimal price, decimal current, decimal cost, decimal old = 60m, decimal anchor = 60m)
    {
        _tool.ProposedPrices.Add(new ProposedPrice
        {
            Id = id, PricingRunId = Run, LayerId = 4, Sku = sku, ProductName = $"[INJECTED-TEST] {sku}", PriceBandId = 26, HasChange = true,
            ProposedPriceValue = price, CurrentPrice = current, OldPrice = old, AnchorPrice = anchor, Pptcv = cost,
            ChangePct = 100m * (price - current) / current, ReasonCodes = "", GuardrailFlags = "",
        });
        _tool.SaveChanges();
    }

    private void InjectTheFourBadRows()
    {
        Injected(1, "INJ-OK", 49.50m, 52.50m, 30m);                 // clean
        Injected(2, "INJ-X10", 495.00m, 49.50m, 30m);              // a price ×10
        Injected(3, "INJ-COST62", 49.50m, 52.50m, 30m * 62);       // a cost ×62 (denars in a euro layer)
        Injected(4, "INJ-UNCHARMED", 49.37m, 50.50m, 30m);         // off the .50 grid
        Injected(5, "INJ-BELOWCOST", 19.50m, 22.50m, 25m);         // below cost
    }

    private static JsonObject V(string sku, string severity, string reason, decimal? fix = null) => new()
    {
        ["sku"] = sku, ["band"] = "10–50", ["new_price"] = 1m, ["margin_pct"] = null, ["floor_pct"] = 13m,
        ["severity"] = severity, ["reason"] = reason, ["fix_price"] = fix,
    };

    private void ModelSays(string verdict, params JsonObject[] violations) => _answers.Enqueue(new JsonObject
    {
        ["type"] = "result", ["subtype"] = "success", ["is_error"] = false, ["num_turns"] = 2, ["result"] = "", ["total_cost_usd"] = 0.05m,
        ["usage"] = new JsonObject { ["input_tokens"] = 900, ["output_tokens"] = 600 },
        ["structured_output"] = new JsonObject { ["run_id"] = Run, ["verdict"] = verdict, ["violations"] = new JsonArray(violations.Select(v => (JsonNode)v).ToArray()) },
    }.ToJsonString());

    private RunChecker Checker(bool enabled = true, int cap = 150)
    {
        var options = Options.Create(new AgentOptions { Enabled = enabled, RuntimeDirectory = Path.Combine(_dir, "runtime"), RunCheckerModelCap = cap });
        var client = new ClaudeCodeClient(options, new Runner(this), _db, NullLogger<ClaudeCodeClient>.Instance);
        return new RunChecker(_tool, _db, client, options, NullLogger<RunChecker>.Instance);
    }

    private sealed class Holdouts : IHoldoutLookup
    {
        public HashSet<long> Ids { get; } = new();
        /// <summary>Other categories' proposals (a paused band × category), asked for one category at a time.</summary>
        public Dictionary<string, HashSet<long>> InCategory { get; } = new();
        public Task<IReadOnlySet<long>> HoldoutProposalIdsAsync(long runId, int layerId, IReadOnlyCollection<string> categories, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<long>>(categories.Count == 1 && InCategory.TryGetValue(categories.First(), out var ids) ? ids : Ids);
    }

    private readonly Holdouts _holdouts = new();

    private CheckedExport Exporter() => new(_tool, _db, _holdouts, Options.Create(new AgentOptions()));

    private Task<ExportResult> Export() => Exporter().ExportAsync(Run, Path.Combine(_dir, "exports"));

    [Fact]
    public async Task Injected_bad_rows_are_blocked_and_held_and_the_rest_of_the_run_is_exported()
    {
        InjectTheFourBadRows();
        void ModelBlocks() => ModelSays("block",
            V("INJ-X10", "block", "495.00 is ten times today's 49.50: a data error."),
            V("INJ-COST62", "block", "Cost 1860.00 is 62x the shelf price: denars in a euro layer."),
            V("INJ-BELOWCOST", "block", "19.50 is below the cost of 25.00."),
            V("INJ-UNCHARMED", "warn", "49.37 is off the .50 grid.", fix: 49.50m));
        ModelBlocks();

        var result = await Checker().CheckAsync(Run, isTest: true);

        Assert.Equal((Severity.Block, "Succeeded", 4, 4, 3), (result.Verdict, result.Status, result.Flagged, result.Sent, result.Blocks));
        Assert.Equal(1, result.Fixed);                                               // 49.50 passes every rule
        var rows = await _db.AgentRunCheckRows.ToDictionaryAsync(r => r.Sku);
        Assert.Contains("x10-jump", rows["INJ-X10"].Flags);
        Assert.Contains("cost-scale", rows["INJ-COST62"].Flags);
        Assert.Equal(("off-charm", Severity.Warn, Severity.Fixed, true), (rows["INJ-UNCHARMED"].Flags, rows["INJ-UNCHARMED"].RuleSeverity, rows["INJ-UNCHARMED"].FinalSeverity, rows["INJ-UNCHARMED"].FixAccepted));
        Assert.False(rows.ContainsKey("INJ-OK"));
        Assert.Equal("block", (await _db.AgentRunChecks.SingleAsync()).ModelVerdict);
        Assert.StartsWith("[INJECTED-TEST]", (await _db.AiCallLog.SingleAsync()).Purpose);
        Assert.Empty(await _db.TimeSheetEntries.ToListAsync());                     // tests never reach the time sheet

        var export = await Export();                                                 // a test check never gates a real export
        Assert.False(export.Exported);
        Assert.Contains("has not been checked", export.Reason);

        ModelBlocks();
        var real = await Checker().CheckAsync(Run);                                  // the same rows in a real check are blocked...
        Assert.Equal(Severity.Block, real.Verdict);
        var set = await Exporter().CheckedSetAsync(Run);
        Assert.Null(set.Blocked);                                                    // ...held, and the rest goes out (the user's decision, 1 Nov)
        Assert.Equal(new[] { "INJ-BELOWCOST", "INJ-COST62", "INJ-X10" }, set.BlockedSkus.Order());
        Assert.Equal(new[] { "INJ-OK", "INJ-UNCHARMED" }, set.Prices.Select(p => p.Sku).Order());
        Assert.Equal(49.50m, set.Prices.Single(p => p.Sku == "INJ-UNCHARMED").Price);
        export = await Export();
        Assert.True(export.Exported);
        Assert.Equal(2, export.Rows);
        var lines = await File.ReadAllLinesAsync(export.FilePath!);
        Assert.DoesNotContain(lines, l => l.StartsWith("INJ-X10") || l.StartsWith("INJ-COST62") || l.StartsWith("INJ-BELOWCOST"));
    }

    [Fact]
    public async Task A_check_that_did_not_finish_still_stops_the_whole_run()
    {
        InjectTheFourBadRows();
        _db.AgentRunChecks.Add(new AgentRunCheck { RunId = Run, LayerId = 4, Verdict = Severity.Block, Status = "Failed", Error = "the model call failed twice" });
        await _db.SaveChangesAsync();

        var set = await Exporter().CheckedSetAsync(Run);
        Assert.Equal("the model call failed twice", set.Blocked);
        Assert.Empty(set.Prices);
        Assert.False((await Export()).Exported);
    }

    [Fact]
    public async Task A_warned_row_stays_out_of_the_export_and_a_fixed_row_goes_out_at_the_fixed_price()
    {
        Injected(1, "INJ-OK", 49.50m, 52.50m, 30m);
        Injected(4, "INJ-UNCHARMED", 49.37m, 50.50m, 30m);
        Injected(6, "INJ-BIGCHANGE", 39.50m, 52.50m, 30m);           // −24.8 %: a person confirms it
        ModelSays("warn",
            V("INJ-BIGCHANGE", "warn", "A 24.8 % cut needs a person."),
            V("INJ-UNCHARMED", "warn", "Off the grid.", fix: 49.50m));

        var result = await Checker().CheckAsync(Run);

        Assert.Equal((Severity.Warn, 1, 1), (result.Verdict, result.Warns, result.Fixed));
        var export = await Export();
        Assert.True(export.Exported);
        Assert.Equal((2, 1), (export.Rows, export.LeftOut));
        var csv = await File.ReadAllLinesAsync(export.FilePath!);
        Assert.Contains(csv, l => l.StartsWith("INJ-UNCHARMED,60.00,50.50,49.50,") && l.EndsWith("fixed by the run checker"));
        Assert.DoesNotContain(csv, l => l.StartsWith("INJ-BIGCHANGE"));
        Assert.Equal(new[] { AgentDbContext.TaskTypes.PrePublishRunCheck, AgentDbContext.TaskTypes.CreateAndPushDiscounts },
            await _db.TimeSheetEntries.OrderBy(e => e.Id).Select(e => e.TaskTypeId).ToListAsync());   // the real check and the export are logged
    }

    [Fact]
    public async Task A_fix_that_breaks_a_rule_is_refused()
    {
        Injected(4, "INJ-UNCHARMED", 49.37m, 50.50m, 30m);
        ModelSays("warn", V("INJ-UNCHARMED", "warn", "Off the grid.", fix: 29.50m));   // below cost

        await Checker().CheckAsync(Run);

        var row = await _db.AgentRunCheckRows.SingleAsync();
        Assert.Equal((false, Severity.Warn), (row.FixAccepted, row.FinalSeverity));
        Assert.Contains("refused: below-cost", row.Detail);
    }

    [Fact]
    public async Task A_block_stays_a_block_whatever_the_model_says_and_a_warn_may_be_raised()
    {
        Injected(5, "INJ-BELOWCOST", 19.50m, 22.50m, 25m);
        Injected(4, "INJ-UNCHARMED", 49.37m, 50.50m, 30m);
        ModelSays("block",
            V("INJ-BELOWCOST", "warn", "Clearance price agreed with the supplier; hold it for a person, don't stop the run."),
            V("INJ-UNCHARMED", "block", "Looks like a data error."));

        var result = await Checker().CheckAsync(Run);

        var rows = await _db.AgentRunCheckRows.ToDictionaryAsync(r => r.Sku);
        Assert.Equal(Severity.Block, rows["INJ-BELOWCOST"].FinalSeverity);                                // the model's warn doesn't lift it
        Assert.Equal(Severity.Block, rows["INJ-UNCHARMED"].FinalSeverity);
        Assert.Equal(Severity.Block, result.Verdict);
    }

    [Fact]
    public async Task No_valid_answer_blocks_the_run()
    {
        InjectTheFourBadRows();
        _answers.Enqueue("""{"type":"result","subtype":"success","is_error":false,"result":"looks fine to me"}""");
        _answers.Enqueue("""{"type":"result","subtype":"success","is_error":false,"structured_output":{"run_id":5001,"verdict":"pass"}}""");

        var result = await Checker().CheckAsync(Run);

        Assert.Equal(("Failed", Severity.Block, 2), (result.Status, result.Verdict, _calls));
        Assert.Contains("InvalidOutput", result.Error);
        Assert.False((await Export()).Exported);
    }

    [Fact]
    public async Task The_kill_switch_means_no_check_and_no_export()
    {
        InjectTheFourBadRows();

        var result = await Checker(enabled: false).CheckAsync(Run);

        Assert.Equal(("Skipped", Severity.Block, 0), (result.Status, result.Verdict, _calls));
        Assert.False((await Export()).Exported);
    }

    [Fact]
    public async Task Rows_over_the_cap_keep_their_rule_severity_and_the_check_says_so()
    {
        InjectTheFourBadRows();
        ModelSays("block", V("INJ-X10", "block", "Tenfold."));

        var result = await Checker(cap: 1).CheckAsync(Run);

        Assert.Equal((4, 1), (result.Flagged, result.Sent));
        var check = await _db.AgentRunChecks.SingleAsync();
        Assert.Contains("3 flagged rows over the cap of 1", check.Notes);
        Assert.Equal(1, await _db.AgentRunCheckRows.CountAsync(r => r.ModelSeverity != null));
        Assert.Equal(Severity.Warn, (await _db.AgentRunCheckRows.SingleAsync(r => r.Sku == "INJ-UNCHARMED")).FinalSeverity);
    }

    [Fact]
    public async Task The_holdout_is_left_out_and_a_pushed_export_becomes_the_tools_approval_once()
    {
        Injected(1, "INJ-OK", 49.50m, 52.50m, 30m);
        Injected(7, "INJ-HOLDOUT", 24.50m, 25.50m, 15m);
        _holdouts.Ids.Add(7);
        _tool.DailySnapshots.Add(new DailySnapshot { LayerId = 4, SnapshotDate = new DateTime(2026, 10, 30), Sku = "INJ-OK", Qty30 = 10 });
        _tool.SaveChanges();
        await Checker().CheckAsync(Run);                                                  // nothing flagged: no model call

        var export = await Export();

        Assert.Equal((1, 1), (export.Rows, export.LeftOutHoldout));
        var batch = await _db.ExportBatches.SingleAsync();
        Assert.Equal(Math.Round((49.50m - 52.50m) / 1.18m * 10, 2), batch.ExpectedGp30Eur);   // estimated, for reference only
        var approvalId = await Exporter().MarkPushedAsync(batch.Id, "team push (test)");
        Assert.Equal(approvalId, await Exporter().MarkPushedAsync(batch.Id, "team push (test)"));   // idempotent
        var approval = await _tool.Approvals.Include(a => a.Items).SingleAsync();
        Assert.Equal((Run, 1, 0, 1), (approval.SourceRunId, approval.SkuCount, approval.IncreaseCount, approval.DecreaseCount));
        Assert.Equal(("INJ-OK", 52.50m, 49.50m), (approval.Items[0].Sku, approval.Items[0].OldPrice, approval.Items[0].NewPrice));
    }

    [Fact]
    public async Task A_paused_band_and_category_gets_no_raises_but_keeps_its_cuts_and_each_price_carries_its_ceiling()
    {
        Injected(1, "INJ-CUT", 49.50m, 52.50m, 30m);                                     // a cut in the paused category: still goes
        Injected(2, "INJ-RAISE", 49.50m, 47.50m, 30m);                                   // a raise in it: held back
        Injected(3, "INJ-RAISE-BAND27", 54.50m, 52.50m, 30m, anchor: 56m);               // the same category, another band: goes
        _tool.ProposedPrices.Find(3L)!.PriceBandId = 27;
        _tool.SaveChanges();
        _holdouts.InCategory["Aksesorë"] = new HashSet<long> { 1, 2, 3 };
        _db.CategoryPauses.Add(new CategoryPause { LayerId = 4, BandId = 26, Category = "Aksesorë", PausedUtc = DateTime.UtcNow, Reason = "outcome drop (test)" });
        _db.SaveChanges();
        await Checker().CheckAsync(Run);                                                  // nothing flagged: no model call

        var set = await Exporter().CheckedSetAsync(Run);

        Assert.Equal(new[] { "INJ-CUT", "INJ-RAISE-BAND27" }, set.Prices.Select(p => p.Sku).OrderBy(s => s));
        Assert.Equal(1, set.PausedRaisesLeftOut);
        Assert.Equal((60m, 56m), (set.Prices.Single(p => p.Sku == "INJ-CUT").Ceiling, set.Prices.Single(p => p.Sku == "INJ-RAISE-BAND27").Ceiling));
        Assert.Contains("1 raises in paused categories left out", (await Export()).Reason);
        var check = await _db.AgentRunChecks.SingleAsync();
        Assert.Contains("3 changed prices checked: 2 raises (they go out as the regular price), 1 cuts (as discounts)", check.Notes);
    }

    [Fact]
    public async Task Margin_trim_cuts_are_held_back_and_raises_markdowns_and_a_persons_price_still_go()
    {
        Injected(1, "TRIM-CUT", 49.50m, 52.50m, 30m);
        Injected(2, "TRIM-RAISE", 54.50m, 52.50m, 30m);
        Injected(3, "MARKDOWN", 49.50m, 52.50m, 30m);
        Injected(4, "OVERRIDE-CUT", 49.50m, 52.50m, 30m);
        Injected(5, "SYNCED-TRIM-CUT", 49.50m, 52.50m, 30m);
        foreach (var (id, codes) in new[] { (1L, "MARGIN_TARGET_TRIM"), (2L, "MARGIN_TARGET_TRIM"), (3L, "DEAD_STOCK_MARKDOWN"), (4L, "MARGIN_TARGET_TRIM"), (5L, "SYNCED_TO_GJ50,MARGIN_TARGET_TRIM") })
            _tool.ProposedPrices.Find(id)!.ReasonCodes = codes;
        _tool.ProposedPrices.Find(4L)!.ManualOverrideBy = "manager@test";
        _tool.SaveChanges();
        _db.AgentRunChecks.Add(new AgentRunCheck { RunId = Run, LayerId = 4, StartedUtc = DateTime.UtcNow, Status = "Succeeded", Verdict = Severity.Pass });   // a clean check
        _db.SaveChanges();

        var set = await Exporter().CheckedSetAsync(Run);

        Assert.Equal(new[] { "MARKDOWN", "OVERRIDE-CUT", "TRIM-RAISE" }, set.Prices.Select(p => p.Sku).OrderBy(s => s));
        Assert.Equal(new[] { "SYNCED-TRIM-CUT", "TRIM-CUT" }, set.HeldBackTrimCutSkus!.OrderBy(s => s));
        Assert.Contains("2 margin-trim cuts held back", PricingTool.NopIntegration.CheckedPricePublisher.Chosen(set));

        var off = await new CheckedExport(_tool, _db, _holdouts, Options.Create(new AgentOptions { HoldBackTrimCuts = false })).CheckedSetAsync(Run);
        Assert.Equal(5, off.Prices.Count);                                                // the switch off: every checked price goes
        Assert.Null(off.HeldBackTrimCutSkus);
    }

    private sealed class Shelf : IShelfSource
    {
        public List<ShelfRow> Rows { get; } = new();
        public Task<IReadOnlyList<ShelfRow>> SoldAsync(long runId, int layerId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ShelfRow>>(Rows);
    }

    [Fact]
    public async Task A_shops_effective_margin_target_raises_the_products_under_the_minimum_it_takes_and_nothing_else()
    {
        Injected(1, "CUT", 49.50m, 52.50m, 30m);                                              // in the set: 39 % after the cut
        _tool.ProposedPrices.Add(new ProposedPrice { Id = 2, PricingRunId = Run, LayerId = 4, Sku = "LOW", ProductName = "[INJECTED-TEST] LOW", PriceBandId = 26,
            HasChange = false, ProposedPriceValue = 52.50m, CurrentPrice = 52.50m, OldPrice = 60m, AnchorPrice = 60m, Pptcv = 45m, ReasonCodes = "", GuardrailFlags = "" });
        _tool.SaveChanges();
        _db.AgentRunChecks.Add(new AgentRunCheck { RunId = Run, LayerId = 4, StartedUtc = DateTime.UtcNow, Status = "Succeeded", Verdict = Severity.Pass });
        var today = DateOnly.FromDateTime(DateTime.Now);
        _db.ChatRules.Add(new ChatRule { LayerId = 4, Kind = ChatRuleKinds.MarginTarget, Value = "22", FromDate = today, ToDate = today, Instruction = "test",
            CreatedBy = "manager@test", CreatedUtc = DateTime.UtcNow });
        _db.SaveChanges();
        var shelf = new Shelf();
        shelf.Rows.AddRange(new[]
        {
            new ShelfRow(1, "CUT", 60m, 52.50m, 60m, 30m, 26, false, false, false, 1m),
            new ShelfRow(2, "LOW", 60m, 52.50m, 60m, 45m, 26, false, false, false, 10m),      // 14.3 %, most of the units
            new ShelfRow(3, "HIGH", 60m, 50m, 60m, 25m, 26, false, false, false, 2m),
            new ShelfRow(4, "DEAD", 60m, 40m, 60m, 38m, 26, true, false, false, 5m),          // 5 %, dead stock: its own lane
        });
        var exporter = new CheckedExport(_tool, _db, _holdouts, Options.Create(new AgentOptions()), null, shelf, new PricingTool.Data.Services.BandConfigProvider(_tool));

        var set = await exporter.CheckedSetAsync(Run);

        Assert.Equal(new[] { ("CUT", 49.50m), ("LOW", 59.50m) }, set.Prices.OrderBy(p => p.Sku).Select(p => (p.Sku, p.Price)));   // 57.36 needed: charm x5.50 / x9.50, under the 60 ceiling
        Assert.Equal(new[] { "LOW" }, set.MarginTargetRaised!.Keys);
        Assert.Contains("Effective margin target 22% on Gjirafa50 — Kosovo today", set.MarginTargetNote);
        Assert.Contains("17.7% → 23.8% at these prices; 1 products under 21.", set.MarginTargetNote);

        shelf.Rows.RemoveAll(r => r.Sku != "HIGH");                                          // a shop already above it
        var above = await exporter.CheckedSetAsync(Run);
        Assert.Equal(new[] { "CUT" }, above.Prices.Select(p => p.Sku));
        Assert.Contains("already at 50.0% at these prices, nothing raised", above.MarginTargetNote);
    }

    [Fact]
    public async Task A_superseded_export_cannot_be_marked_pushed()
    {
        Injected(1, "INJ-OK", 49.50m, 52.50m, 30m);
        await Checker().CheckAsync(Run);
        await Export();
        await Export();                                                                   // the run re-exported, e.g. after a fix
        var older = await _db.ExportBatches.MinAsync(b => b.Id);
        var newer = await _db.ExportBatches.MaxAsync(b => b.Id);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Exporter().MarkPushedAsync(older, "team push (test)"));

        Assert.Contains($"superseded by export #{newer}", ex.Message);
        Assert.Empty(_tool.Approvals);
        await Exporter().MarkPushedAsync(newer, "team push (test)");
        Assert.Single(_tool.Approvals);
    }

    [Fact]
    public async Task An_unchecked_run_is_not_exported()
    {
        Injected(1, "INJ-OK", 49.50m, 52.50m, 30m);
        var export = await Export();
        Assert.False(export.Exported);
        Assert.Contains("has not been checked", export.Reason);
    }
}
