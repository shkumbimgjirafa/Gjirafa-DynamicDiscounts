using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PricingTool.Agent;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Checker;
using PricingTool.Agent.ClaudeCode;
using PricingTool.Agent.Operations;
using PricingTool.Agent.Storage;
using PricingTool.Agent.Tools;
using Microsoft.Data.SqlClient;
using PricingTool.AgentTools;
using PricingTool.Data;
using PricingTool.NopIntegration;
using PricingTool.NopIntegration.Sandbox;

// PricingTool.AgentTools: the agents' data tools as an MCP server, plus commands to check them by hand.
//   mcp --agent <Alpha|Nova|RunChecker|Chat> [--call-tag <tag>]   stdio MCP server (Claude Code starts it)
//   scorecard <layer> [<layer> ...]                                 print Alpha's scorecard and how long it took
//   query [--agent <role>] [--timeout <s>] "<sql>"                  run query_data with its guardrails
//   ask --agent <role> [--schema <file>] "<question>"               one real Claude Code call with the data tools
//   alpha-pass <layer> [--mode Autonomous|ApproveFirst|Off] [--test] Alpha's weekly floor/target pass for one layer
//   nova-pass <layer> [--test] [--preview]                           Nova's weekly campaign pass for one layer (briefs, results);
//                                                                   --preview prints the prompt it would send, no call
//   plan-campaign <brief id> [--publish]                             brief -> plan -> accept/adjust (Alpha + Nova), optionally publish
//   approve-plan <plan id> [--by <who>] [--publish]                  a person approves a plan waiting for them (held, or a stretch)
//   backtest-campaigns <layer> [<layer> ...]                         replay finished campaigns with the planner's arithmetic
//   backtest-levers <layer> [<layer> ...] [--from yyyy-MM-dd] [--to yyyy-MM-dd]   levers 1-3, 5, 6 on booked sales lines
//   reproduce-per-ty --test                                          the Për ty reproduction test: the agents plan the 6 Oct brief
//                                                                   with data cut off at 7 Oct (two model calls; records to the test DB)
//   campaign-note <calendar id> "<text>" [--by <who>] [--not-representative]   a person's context on a campaign
//   lesson <Nova|Alpha> "<text>" [--layer <id>] [--by <who>]       teach an agent: kept in every pass's prompt until retired
//   lessons [<Nova|Alpha>] | lesson-retire <id> [--by <who>]        list the active lessons, or retire one
//   campaign-goal <calendar id> <gross-profit|margin|units> <value> [--by <who>] [--note "<why>"]  a person's binding goal
//   cost-watch [--poll] [--inject <layer> <sku> <cost>]              one cost watch: optionally poll the replica, or inject a labelled
//                                                                   [INJECTED-TEST] cost (with --test its records go to the test DB)
//   sandbox [check|create [--recreate]|script]                      the shop sandbox: contract check, build + seed, DDL
//   publish-canary <layer> --run <id> --category <c> [--max n] [--rollback]   publish a run's sample, show the rows
//   publish-rollback <batch id>                                     restore what a publish batch changed
//   check-run <run id> [--test]                                     the pre-publish run check (one model call)
//   export-run <run id> [--out <dir>]                               the checked export for the push (refused when blocked)
//   mark-pushed <export batch id> [--by <who>]                      record an export as pushed (an Approval): its measurement starts
//   ledger                                                          update the money ledger and print its entries
//   outcomes                                                        measure applied decisions (auto-rollback on a drop) and print them
//   outcomes --probe <decision id> <yyyy-MM-dd>                     read-only: what it would measure had the change gone live that day
//   timesheet                                                       export the hours-saved sheet (xlsx, csv, json)
//   campaign-data <layer> [--category c] [--weeks n] [--results <calendar id>]   sync the campaign calendar and print the campaign tools
//   cycle-status [yyyy-MM-dd]                                       the daily cycle's steps for a day (default today)
//   test-db                                                         create or migrate the test database and list what it holds
// --test (alpha-pass, check-run, test-db): the run is labelled [INJECTED-TEST] and its records go to the test database
// (PricingToolHackathonTest), never the live one; inputs are still read from the live database.
// Settings come from appsettings.json next to the executable (the hackathon DB; no secrets).

var command = args.FirstOrDefault() ?? "";
string? Opt(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var positional = args.Skip(1).Where((a, i) => !a.StartsWith("--") && (i == 0 || !args[i].StartsWith("--"))).ToList();
var role = Enum.Parse<AgentRole>(Opt("--agent") ?? "Alpha", ignoreCase: true);

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    ContentRootPath = AppContext.BaseDirectory,
    DisableDefaults = false,
});
// stdout belongs to the MCP protocol: every log line goes to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
if (Opt("--timeout") is { } timeout)
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Agent:QueryData:TimeoutSeconds"] = timeout });
if (command == "ask" && Environment.ProcessPath is { } self)
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Agent:ToolsExecutable"] = self });

var testRun = args.Contains("--test") || command == "test-db";
if (testRun)
{
    var testDb = AgentServiceCollectionExtensions.TestConnectionString(builder.Configuration);
    var key = $"ConnectionStrings:{AgentServiceCollectionExtensions.AgentDbConnectionName}";
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [key] = testDb });
    // The data tools Claude Code starts for this run inherit it, so their query log lands in the test database too.
    Environment.SetEnvironmentVariable(key.Replace(":", "__"), testDb);
    Console.Error.WriteLine($"Test run: the agents' records go to {new SqlConnectionStringBuilder(testDb).InitialCatalog}.");
}

// Only the cost watch reads the replica (its read-only connection is in user-secrets, shared with the engine and web);
// no other command, and never the MCP server the agents use, loads it.
if (command == "cost-watch")
    builder.Configuration.AddUserSecrets("gjirafa-hackathon-pricing-agent-2026");

builder.Services.AddPricingTool(builder.Configuration);
builder.Services.AddPricingAgent(builder.Configuration);
builder.Services.AddNopPublisher(builder.Configuration);
builder.Services.AddSingleton(new AgentToolContext(role, Opt("--call-tag") ?? (command == "mcp" ? null : "cli")));

switch (command)
{
    case "mcp":
        builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = AgentToolNames.Server, Version = "1.0" })
            .WithStdioServerTransport()
            .WithTools<PricingMcpTools>();
        await builder.Build().RunAsync();
        return 0;

    case "scorecard":
    {
        using var host = builder.Build();
        foreach (var layer in positional.Select(int.Parse))
        {
            var sw = Stopwatch.StartNew();
            var card = await host.Services.GetRequiredService<AlphaInputBuilder>().BuildAsync(layer);
            sw.Stop();
            var table = card.ToPromptTable();
            Console.WriteLine(table);
            Console.WriteLine($"-- layer {layer}: {card.Rows.Count} rows, {table.Length:N0} chars, built in {sw.ElapsedMilliseconds} ms");
            Console.WriteLine();
        }
        return 0;
    }

    case "query":
    {
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AgentToolContext>();
        var result = await scope.ServiceProvider.GetRequiredService<QueryDataService>().RunAsync(ctx, positional.Last());
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        return result.Error is null ? 0 : 1;
    }

    case "ask":
    {
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.MigrateAgentDbAsync();
        var client = scope.ServiceProvider.GetRequiredService<ClaudeCodeClient>();
        var prompts = Path.Combine(AppContext.BaseDirectory, "Prompts");
        var result = await client.CallAsync(new AgentCallRequest(role, "ask (cli)", positional.Last(), Path.Combine(prompts, "pricing-agent-rules.md"))
        {
            AppendSystemPromptFiles = new[] { Path.Combine(prompts, "agent-data-dictionary.md") },
            JsonSchema = Opt("--schema") is { } schema ? await File.ReadAllTextAsync(schema) : null,
            UseDataTools = true,
        });
        Console.WriteLine($"{result.Status}  call tag {result.CallTag}  AiCallLog {string.Join(",", result.CallLogIds)}");
        Console.WriteLine(result.Output?.ToString() ?? result.Error);

        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        foreach (var q in await db.AgentQueryLog.Where(q => q.CallTag == result.CallTag).OrderBy(q => q.Id).ToListAsync())
            Console.WriteLine($"  AgentQueryLog #{q.Id} {q.Tool} rows={q.RowCount} {q.DurationMs} ms {(q.Accepted ? "" : "REFUSED ")}{q.Error}\n    {q.Request.ReplaceLineEndings(" ")}");
        return result.Succeeded ? 0 : 1;
    }

    case "alpha-pass":
    {
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.MigrateAgentDbAsync();
        var layer = int.Parse(positional[0]);
        if (Opt("--mode") is { } mode)
            await scope.ServiceProvider.GetRequiredService<AgentModeStore>().SetAsync(layer, Enum.Parse<AutonomyMode>(mode, true), "cli");
        var sw = Stopwatch.StartNew();
        var result = await scope.ServiceProvider.GetRequiredService<FloorTargetAgent>().RunPassAsync(layer,
            isTest: args.Contains("--test"), injectedTestNote: Opt("--test-note"));
        Console.WriteLine($"Pass #{result.PassId}: {result.Status} in {sw.Elapsed.TotalSeconds:0} s {result.Error}");
        Console.WriteLine($"Applied {result.Count("Applied")} (changed {result.Decisions.Count(d => d.Status == "Applied" && d.Action != "keep")}), " +
                          $"held {result.Count("Held")}, rejected {result.Count("Rejected")}, pending {result.Count("Pending")}");
        foreach (var d in result.Decisions.OrderBy(d => d.BandId).ThenBy(d => d.Category))
            Console.WriteLine($"#{d.Id} B{d.BandId} {d.Category} | {d.Status} {d.Action} floor {d.CurrentFloorPct:0.##}->{d.ProposedFloorPct:0.##} " +
                              $"target {d.CurrentTargetPct:0.##}->{d.ProposedTargetPct:0.##} conf {d.Confidence:0.##} {d.Limit}\n    {d.Reason}" +
                              (d.LimitDetail is null ? "" : $"\n    [limit] {d.LimitDetail}"));
        return result.Status == "Succeeded" ? 0 : 1;
    }

    case "sandbox":
    {
        var sub = positional.FirstOrDefault() ?? "check";
        await using var conn = new SqlConnection(builder.Configuration.GetConnectionString("PricingToolDb"));
        await conn.OpenAsync();
        var snapshot = SandboxSchema.LoadSnapshot();
        var dbs = snapshot.Select(r => r.Db).Distinct().OrderBy(d => d).ToList();
        if (sub == "script")
        {
            Console.Write(SandboxSchema.Script(snapshot));
            return 0;
        }
        if (sub == "create")
        {
            await using var exists = new SqlCommand(
                "SELECT COUNT(*) FROM sys.tables WHERE SCHEMA_NAME(schema_id) LIKE 'shop[_]sandbox[_]%'", conn);
            if ((int)(await exists.ExecuteScalarAsync())! > 0 && !args.Contains("--recreate"))
            {
                Console.Error.WriteLine("The sandbox already exists; 'create' would drop what was published to it. Add --recreate to rebuild it.");
                return 1;
            }
            var sw = Stopwatch.StartNew();
            await SandboxSeeder.CreateAsync(conn, snapshot);
            Console.WriteLine($"Created {string.Join(", ", dbs.Select(SandboxSchema.SchemaFor))} in {sw.ElapsedMilliseconds} ms");
            foreach (var r in await SandboxSeeder.SeedAsync(conn, snapshot))
                Console.WriteLine($"Seeded {r.Db}: {r.Discounts:N0} discounts, {r.Mappings:N0} mappings, {r.Products:N0} products in {r.Milliseconds:N0} ms");
        }
        var failed = false;
        foreach (var db in dbs)
        {
            var diffs = SandboxContract.Differences(snapshot, await SandboxContract.ReadActualAsync(conn, db), db);
            Console.WriteLine(diffs.Count == 0 ? $"Contract {db}: sandbox DDL equals the live snapshot" : $"Contract {db}: {diffs.Count} difference(s)");
            foreach (var d in diffs) Console.WriteLine("  " + d);
            failed |= diffs.Count > 0;
        }
        return failed ? 1 : 0;
    }

    case "publish-canary":
    {
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.MigrateAgentDbAsync();
        var layer = int.Parse(positional[0]);
        var run = long.Parse(Opt("--run") ?? throw new ArgumentException("--run <engine run id> is required"));
        var category = Opt("--category") ?? throw new ArgumentException("--category <category> is required");
        var max = int.Parse(Opt("--max") ?? "20");
        var conn = builder.Configuration.GetConnectionString("PricingToolDb")!;

        // A deterministic sample of the run's changed prices in one category (Mall: main group, Gj50: L1).
        var items = new List<PublishItem>();
        await using (var c = new SqlConnection(conn))
        {
            await c.OpenAsync();
            await using var cmd = new SqlCommand("""
                DECLARE @Mall bit = (SELECT CASE WHEN Brand = 'GjirafaMall' THEN 1 ELSE 0 END FROM PricingTool.Layers WHERE Id = @L);
                SELECT TOP (@Max) pp.Sku, pp.ProposedPriceValue, pp.OldPrice
                FROM PricingTool.ProposedPrices pp
                CROSS APPLY hk.ProductForProposal(pp.LayerId, pp.ProductId, pp.Sku) p
                WHERE pp.PricingRunId = @Run AND pp.LayerId = @L AND pp.HasChange = 1
                  AND (CASE WHEN @Mall = 1 THEN p.MainGroup ELSE p.CategoryL1 END) = @Cat
                ORDER BY CHECKSUM(pp.Sku, 'canary')
                """, c);
            cmd.Parameters.AddWithValue("@L", layer);
            cmd.Parameters.AddWithValue("@Run", run);
            cmd.Parameters.AddWithValue("@Cat", category);
            cmd.Parameters.AddWithValue("@Max", max);
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync()) items.Add(new PublishItem(rd.GetString(0), rd.GetDecimal(1), rd.IsDBNull(2) ? null : rd.GetDecimal(2)));
        }
        Console.WriteLine($"Canary: layer {layer}, engine run #{run}, category '{category}', {items.Count} changed prices");

        var writer = scope.ServiceProvider.GetRequiredService<NopDiscountWriter>();
        var sw = Stopwatch.StartNew();
        var outcome = await writer.PublishAsync(new PublishRequest(layer, items) { EngineRunId = run });
        sw.Stop();
        Console.WriteLine($"Batch #{outcome.BatchId}: {outcome.Status}; created {outcome.Created}, updated {outcome.Updated}, unchanged {outcome.Unchanged}, " +
                          $"skipped {outcome.Skipped}; round-trip {outcome.RoundTripChecked - outcome.RoundTripMismatches}/{outcome.RoundTripChecked} read back; {sw.ElapsedMilliseconds} ms");
        foreach (var g in outcome.Exceptions.GroupBy(e => e.Kind))
            Console.WriteLine($"  {g.Key}: {g.Count()} (e.g. {g.First().Sku}: {g.First().Detail})");

        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        var batch = await db.PublishBatches.SingleAsync(b => b.Id == outcome.BatchId);
        await PrintWrittenAsync(conn, batch, "Rows as written");

        // Only a live publish that stays is a real task for the hours-saved sheet; a sandbox write or a rolled-back
        // canary is a test of the publisher.
        if (batch.Target == "Live" && !args.Contains("--rollback") && outcome.Status == "Published")
        {
            var type = await db.TimeSheetTaskTypes.SingleAsync(t => t.Id == AgentDbContext.TaskTypes.CreateAndPushDiscounts);
            db.TimeSheetEntries.Add(new TimeSheetEntry
            {
                Date = DateOnly.FromDateTime(DateTime.Now), UserEmail = builder.Configuration["Agent:TimeSheetOwnerEmail"] ?? "",
                TaskTypeId = type.Id, MinutesBefore = type.MinutesBefore, MinutesAfter = Math.Round((decimal)sw.Elapsed.TotalMinutes, 2),
                What = $"Layer {layer} canary, engine run #{run}, '{category}': {outcome.Created + outcome.Updated} discounts written to the shop " +
                       $"({batch.ShopSchema}), {outcome.Skipped} skipped",
                Proof = $"ai.PublishBatch #{batch.Id}", Comment = "Canary publish; minutes after = the publish's wall clock.",
            });
            await db.SaveChangesAsync();
        }

        if (args.Contains("--rollback"))
        {
            var restored = await writer.RollbackAsync(outcome.BatchId);
            Console.WriteLine($"Rolled back batch #{outcome.BatchId}: {restored} rows restored from ai.PublishAudit");
            await PrintWrittenAsync(conn, batch, "Rows after the rollback");
        }
        return outcome.Status == "Published" ? 0 : 1;
    }

    case "check-run":
    {
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.MigrateAgentDbAsync();
        var runId = long.Parse(positional[0]);
        var sw = Stopwatch.StartNew();
        var r = await scope.ServiceProvider.GetRequiredService<RunChecker>().CheckAsync(runId, isTest: args.Contains("--test"));
        Console.WriteLine($"Check #{r.CheckId} of run #{runId}: {r.Status}, verdict {r.Verdict.ToUpperInvariant()} in {sw.Elapsed.TotalSeconds:0} s " +
                          $"({r.Flagged:N0} flagged, {r.Sent} sent to the model; {r.Blocks} block, {r.Warns} warn, {r.Fixed} fixed) {r.Error}");
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        var check = await db.AgentRunChecks.SingleAsync(c => c.Id == r.CheckId);
        if (check.Notes is not null) Console.WriteLine("  notes: " + check.Notes);
        foreach (var g in (await db.AgentRunCheckRows.Where(x => x.CheckId == r.CheckId).ToListAsync()).GroupBy(x => (x.RuleSeverity, x.FinalSeverity)).OrderBy(g => g.Key.ToString()))
            Console.WriteLine($"  rule {g.Key.RuleSeverity,-5} -> final {g.Key.FinalSeverity,-5}: {g.Count(),6:N0}");
        foreach (var x in (await db.AgentRunCheckRows.Where(x => x.CheckId == r.CheckId && x.ModelSeverity != null).OrderBy(x => x.Id).Take(8).ToListAsync()))
            Console.WriteLine($"  {x.Sku} {x.NewPrice:0.00} (cost {x.Cost:0.00}) [{x.Flags}] rule {x.RuleSeverity} -> model {x.ModelSeverity}" +
                              (x.FixPrice is { } fp ? $" fix {fp:0.00} {(x.FixAccepted ? "accepted" : "refused")}" : "") + $" -> {x.FinalSeverity}\n      {x.ModelReason}");
        return 0;
    }

    case "export-run":
    {
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var runId = long.Parse(positional[0]);
        var dir = Opt("--out") ?? Path.Combine(AppContext.BaseDirectory, "exports");
        var r = await scope.ServiceProvider.GetRequiredService<CheckedExport>().ExportAsync(runId, dir);
        Console.WriteLine((r.Exported ? "Exported: " : "NOT exported: ") + r.Reason + (r.FilePath is null ? "" : $" -> {r.FilePath}"));
        return r.Exported ? 0 : 1;
    }

    case "lesson":
    case "lessons":
    case "lesson-retire":
    {
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.MigrateAgentDbAsync();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        var book = new PricingTool.Agent.Campaigns.LessonBook(db);
        if (command == "lesson")
        {
            var l = await book.AddAsync(positional[0], Opt("--layer") is { } ly ? int.Parse(ly) : null, positional[1], Opt("--by") ?? "user");
            Console.WriteLine($"Lesson #{l.Id} for {l.Agent} ({(l.LayerId is null ? "every layer" : $"layer {l.LayerId}")}): {l.Text}");
        }
        else if (command == "lesson-retire")
            Console.WriteLine(await book.RetireAsync(long.Parse(positional[0]), Opt("--by") ?? "user") ? $"Lesson #{positional[0]} retired." : "No such active lesson.");
        var agentFilter = command == "lessons" && positional.Count > 0 ? positional[0] : null;
        foreach (var l in await db.AgentLessons.AsNoTracking().Where(l => l.RetiredUtc == null && (agentFilter == null || l.Agent == agentFilter))
                     .OrderBy(l => l.Id).ToListAsync())
            Console.WriteLine($"#{l.Id} {l.Agent} {(l.LayerId is null ? "all" : $"L{l.LayerId}")} {l.Source} {l.CreatedUtc:yyyy-MM-dd} {l.CreatedBy}: {l.Text}");
        return 0;
    }

    case "backtest-campaigns":
    {
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.MigrateAgentDbAsync();
        var tools = scope.ServiceProvider.GetRequiredService<AgentDataTools>();
        foreach (var layer in positional.Select(int.Parse))
        {
            var sw = Stopwatch.StartNew();
            var bt = await tools.ReplayCampaignsAsync(layer);
            var (replays, inSample) = (bt.Replays, bt.InSample);
            Console.WriteLine($"Layer {layer}: {replays.Count} campaigns replayed in {sw.Elapsed.TotalSeconds:0.0} s (booked COGS, EUR VAT excl.; estimated)");
            Console.WriteLine("campaign | days | normal days | actual GP | seen SKUs: actual GP / planned (leave-one-out) | tail SKUs: GP (share) | actual/planned LOO | lift LOO | in-sample | usable");
            foreach (var (r, s) in replays.Zip(inSample))
                Console.WriteLine($"{r.Name} | {r.Days} | {r.NormalDays} | {r.ActualGp:N0} | {r.SeenSkus:N0}: {r.SeenActualGp:N0} / {r.PredictedSeenGp:N0} (x{r.SeenRatio:0.00}) | " +
                                  $"{r.TailSkus:N0}: {r.TailGp:N0} ({r.TailSharePct:0.0}%) | x{r.TotalRatio:0.00} | {(r.SeenType ? $"x{r.EventLift:0.00}" : "none (x1)")} | x{s.TotalRatio:0.00} | " +
                                  $"{(r.Usable ? "yes" : r.Representative ? "too few normal days" : "not representative")}");
            Console.WriteLine(bt.SeenType is { } seen ? $"Types with an event lift: {seen.Basis}." : $"Types with an event lift: fewer than {PricingTool.Agent.Campaigns.CampaignReplay.MinCampaigns} usable replays, no factor.");
            Console.WriteLine(bt.UnseenType is { } unseen ? $"Types without one: {unseen.Basis}." : $"Types without one: fewer than {PricingTool.Agent.Campaigns.CampaignReplay.MinCampaigns} usable replays, no factor.");
            Console.WriteLine(bt.InSampleCalibration is null ? "" : $"For comparison only, {bt.InSampleCalibration.Basis}.");
            Console.WriteLine("Lever 7, the planner's depth per SKU vs the campaign's (EUR GP, estimated; uplift curve leave-one-out, x0.5 and x2 for sensitivity):");
            Console.WriteLine("campaign | strategy | B | actual GP | planned GP | gain | gain/day (B x0.5 / x2) | SKUs scored / changed | depth actual -> planned | flags");
            foreach (var g in bt.DepthGains.Where(g => g.BScale == 1m))
            {
                var half = bt.DepthGains.Single(x => x.CampaignId == g.CampaignId && x.Strategy == g.Strategy && x.BScale == 0.5m);
                var dbl = bt.DepthGains.Single(x => x.CampaignId == g.CampaignId && x.Strategy == g.Strategy && x.BScale == 2m);
                Console.WriteLine($"{g.Name} | {g.Strategy} | {g.B:0.00} | {g.ActualGp:N0} | {g.PlannedGp:N0} | {g.Gain:N0} | {g.GainPerDay:N0} ({half.GainPerDay:N0} / {dbl.GainPerDay:N0}) | " +
                                  $"{g.SkusScored:N0} / {g.SkusChanged:N0} | {g.ActualDepthPct:0.0}% -> {g.PlannedDepthPct:0.0}% | " +
                                  $"{(g.Provisional ? "provisional " : "")}{(g.Representative ? "" : "not representative ")}" +
                                  $"{(g.PlannedDepthPct < g.ActualDepthPct - 0.5m ? "shallower than the campaign (grid or cost limit)" : "")}");
            }
            // A year of campaign days at the median gain per day of the representative, finished campaigns.
            var yearDays = bt.Replays.Where(r => r.Start >= DateOnly.FromDateTime(DateTime.Today).AddDays(-365)).Sum(r => r.Days)
                           + bt.DepthGains.Where(g => g.Provisional && g.BScale == 1m && g.Strategy == "reallocate").Sum(g => g.Days);
            foreach (var strategy in PricingTool.Agent.Backtest.CampaignDepthBacktest.Strategies)
            {
                var perDay = bt.DepthGains.Where(g => g.Strategy == strategy && g.BScale == 1m && g.Representative && !g.Provisional && g.SkusScored > 0)
                    .Select(g => g.GainPerDay).OrderBy(v => v).ToList();
                if (perDay.Count == 0) continue;
                var median = perDay.Count % 2 == 1 ? perDay[perDay.Count / 2] : (perDay[perDay.Count / 2 - 1] + perDay[perDay.Count / 2]) / 2m;
                Console.WriteLine($"Lever 7 {strategy}: median {median:N0} EUR/campaign day over {perDay.Count} campaigns x {yearDays} campaign days in the last 12 months = " +
                                  $"{median * yearDays:N0} EUR a year, {median * yearDays / 12m:N0} a month (estimated).");
            }
        }
        return 0;
    }

    case "backtest-levers":
    {
        using var host = builder.Build();
        var backtester = new PricingTool.Agent.Backtest.LeverBacktester(host.Services.GetRequiredService<PricingDataReader>());
        var from = DateOnly.Parse(Opt("--from") ?? "2026-08-01", System.Globalization.CultureInfo.InvariantCulture);
        var to = DateOnly.Parse(Opt("--to") ?? "2026-10-16", System.Globalization.CultureInfo.InvariantCulture);
        string[] names = { "", "loss-sale block", "per-category floors", "cost reaction", "coverage MK/AL", "cadence", "learning" };
        // Lever 6's experiment (plan: August's run #12, approval 12 on GjirafaMall), rolled back in September where it didn't pay.
        var learning = new PricingTool.Agent.Backtest.LeverBacktester.LearningExperiment(12, new DateOnly(2026, 8, 6), new DateOnly(2026, 9, 3), new DateOnly(2026, 10, 1),
            1m - host.Services.GetRequiredService<IOptions<AgentOptions>>().Value.Outcomes.DropPct / 100m);
        foreach (var layer in positional.Where(a => int.TryParse(a, out _)).Select(int.Parse))
        {
            var sw = Stopwatch.StartNew();
            var trace = new List<(int Lever, PricingTool.Agent.Backtest.BacktestLine Line, decimal Gain)>();
            var r = await backtester.RunAsync(layer, from, to, trace, layer == 1 ? learning : null);
            if (layer == 1)
            {
                Console.WriteLine($"Lever 6 verdicts on approval 12 (GP/day 28 days after vs before, season-adjusted with 2025; rolled back in Sep 3-30 under x{learning.RollBackBelow:0.00}, the outcome tracker's drop): " +
                                  string.Join(", ", (await backtester.LearningVerdictsAsync(1, learning.LiveOn)).OrderBy(v => v.Key).Select(v => $"{v.Key} {(v.Value is { } x ? x.ToString("0.00") : "n/a")}")));
                var naive = await backtester.RunAsync(layer, from, to, null, learning with { RollBackBelow = 1m });
                Console.WriteLine("  the plan's plain rule (roll back under x1.00) instead: " +
                                  string.Join("; ", naive.Gains.Where(g => g.Lever == 6).Select(g => $"{g.Month} e {g.Elasticity}: {g.Gain:N0} ({g.Lines} lines)")));
            }
            Console.WriteLine($"Layer {layer}, {from:yyyy-MM-dd}..{to.AddDays(-1):yyyy-MM-dd}, in {sw.Elapsed.TotalSeconds:0.0} s (booked COGS, EUR VAT excl.; estimated unless said)");
            Console.WriteLine("lever | month | e -0.5 | e -1.0 | e -2.0 | lines | units");
            foreach (var g in r.Gains.GroupBy(g => (g.Lever, g.Month)))
                Console.WriteLine($"{g.Key.Lever} {names[g.Key.Lever]} | {g.Key.Month} | " +
                                  string.Join(" | ", PricingTool.Agent.Backtest.LeverBacktest.Elasticities.Select(e => g.FirstOrDefault(x => x.Elasticity == e)?.Gain.ToString("N0") ?? "0")) +
                                  $" | {g.First().Lines:N0} | {g.First().Units:N0}");
            if (args.Contains("--detail"))
                foreach (var lv in trace.GroupBy(t => t.Lever))
                {
                    Console.WriteLine($"  lever {lv.Key}, top days at e -1.0: " + string.Join("; ", lv.GroupBy(t => t.Line.Day).OrderByDescending(d => d.Sum(t => t.Gain)).Take(6)
                        .Select(d => $"{d.Key:MM-dd} {d.Sum(t => t.Gain):N0} ({d.Count()} lines, {d.Select(t => t.Line.Sku).Distinct().Count()} SKUs)")));
                    Console.WriteLine($"  lever {lv.Key}, top SKUs: " + string.Join("; ", lv.GroupBy(t => t.Line.Sku).OrderByDescending(d => d.Sum(t => t.Gain)).Take(5)
                        .Select(d => $"{d.Key} {d.Sum(t => t.Gain):N0} ({d.Count()} lines, cost {d.First().Line.UnitCogs:0.00} at {d.First().Line.UnitNet:0.00} net)")));
                }
            Console.WriteLine("month | cross-dock loss on normal days (measured) | lines | caught by the known-cost rule | campaign-day cross-dock loss (lever 7)");
            foreach (var c in r.Context)
                Console.WriteLine($"{c.Month} | {c.CrossDockLoss:N0} | {c.CrossDockLossLines:N0} | {c.CaughtLoss:N0} ({(c.CrossDockLoss > 0 ? 100m * c.CaughtLoss / c.CrossDockLoss : 0):0}%) | {c.CampaignDayCrossDockLoss:N0}");
        }
        return 0;
    }

    case "reproduce-per-ty":
    {
        if (!testRun)
        {
            Console.Error.WriteLine("Run it with --test: its brief, plans and thread go to the test database.");
            return 1;
        }
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.MigrateAgentDbAsync();
        var sp = scope.ServiceProvider;
        var dataTo = PricingTool.Agent.Backtest.PerTyReproduction.DataTo;
        var clock = new AsOfClock(new DateTimeOffset(dataTo.ToDateTime(new TimeOnly(7, 0)), TimeZoneInfo.Local.GetUtcOffset(dataTo.ToDateTime(new TimeOnly(7, 0)))));
        var options = sp.GetRequiredService<IOptions<AgentOptions>>();
        var db = sp.GetRequiredService<AgentDbContext>();
        var reader = sp.GetRequiredService<PricingDataReader>();
        var candidates = new PricingTool.Agent.Backtest.AsOfCampaignCandidateSource(reader);
        var planning = new PricingTool.Agent.Campaigns.CampaignPlanning(db, sp.GetRequiredService<PricingToolDbContext>(), candidates,
            new AgentDataTools(reader, options, new AgentToolLog(db, clock), db, clock), sp.GetRequiredService<PricingTool.Data.Services.BandConfigProvider>(),
            sp.GetRequiredService<ClaudeCodeClient>(), sp.GetRequiredService<IKillSwitch>(), options,
            sp.GetRequiredService<ILogger<PricingTool.Agent.Campaigns.CampaignPlanning>>(), clock);
        var brief = PricingTool.Agent.Backtest.PerTyReproduction.Brief(1, DateTime.UtcNow);
        db.CampaignBriefs.Add(brief);
        await db.SaveChangesAsync();
        var sw = Stopwatch.StartNew();
        var r = await planning.PlanAsync(brief.Id, publish: false);
        Console.WriteLine($"Brief #{brief.Id} (test DB): {r.Status} in {sw.Elapsed.TotalSeconds:0} s {r.Error}");
        foreach (var m in await db.CampaignMessages.AsNoTracking().Where(m => m.BriefId == brief.Id).OrderBy(m => m.Id).ToListAsync())
            Console.WriteLine($"  [{m.Round}] {m.From}: {m.Text}");
        if (r.Final is { } plan)
        {
            var holdouts = options.Value.Limits.Holdouts.TryGetValue("1", out var h) ? h : new List<string>();
            var all = await candidates.LoadAsync(1, dataTo.AddDays(-42), dataTo.AddDays(-14));
            var inScope = all.Where(c => !c.FollowsGj50 && !holdouts.Contains(c.Category, StringComparer.OrdinalIgnoreCase)).ToList();
            var score = PricingTool.Agent.Backtest.PerTyReproduction.Score(plan.ExpectedMarginPct, PricingTool.Agent.Backtest.PerTyReproduction.NormalMarginPct(inScope));
            Console.WriteLine($"Plan #{plan.Id} ({plan.Strategy}): {score.Detail}. Verdict: {(score.Passed ? "PASS" : "FAIL")}.");
            decimal Revenue(IEnumerable<PricingTool.Agent.Campaigns.CampaignCandidate> cs) => cs.Sum(c => c.UnitsPerDay * c.CurrentPrice);
            Console.WriteLine($"Gjirafa50-listed share of Mall KS normal-day revenue before the campaign: {100m * Revenue(all.Where(c => c.FollowsGj50)) / Revenue(all):0}% " +
                              $"at {PricingTool.Agent.Backtest.PerTyReproduction.NormalMarginPct(all.Where(c => c.FollowsGj50)):0.0}% normal margin, vs " +
                              $"{PricingTool.Agent.Backtest.PerTyReproduction.NormalMarginPct(all.Where(c => !c.FollowsGj50)):0.0}% for Mall's own (measured; the plan leaves them out, as the hand plan did).");
            var actual = await reader.GetWindowResultAsync(1, PricingTool.Agent.Backtest.PerTyReproduction.Start, PricingTool.Agent.Backtest.PerTyReproduction.End.AddDays(1));
            string M(IEnumerable<(bool Gj50Listed, decimal Net, decimal Gp)> x) => $"GP {x.Sum(a => a.Gp):N0} on {x.Sum(a => a.Net):N0} net, {(x.Sum(a => a.Net) > 0 ? 100m * x.Sum(a => a.Gp) / x.Sum(a => a.Net) : 0):0.0}% margin";
            Console.WriteLine("Side by side, Mall's own SKUs (estimated unless said):");
            Console.WriteLine($"  hand plan (6 Oct): band targets at 75% of normal, forecast effective margin 15.1% (GM3 10.2%)");
            Console.WriteLine($"  agents' plan: {plan.Strategy}, {plan.DiscountedSkus:N0} of {plan.SkusInScope:N0} SKUs at {plan.AverageDepthPct:0.0}% average depth, expected {plan.ExpectedMarginPct:0.0}% margin, GP {plan.ExpectedGrossProfit:N0}");
            Console.WriteLine($"  what happened (measured, provisional: late cancellations): Mall's own {M(actual.Where(a => !a.Gj50Listed))}; whole Mall {M(actual)}");
        }
        return 0;
    }

    case "approve-plan":
    {
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.MigrateAgentDbAsync();
        var r = await scope.ServiceProvider.GetRequiredService<PricingTool.Agent.Campaigns.CampaignPlanning>()
            .ApproveAsync(long.Parse(positional[0]), Opt("--by") ?? "user", args.Contains("--publish"));
        Console.WriteLine($"Plan #{positional[0]}: {r.Status} {r.Error}");
        return r.Error is null ? 0 : 1;
    }

    case "campaign-goal":
    {
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.MigrateAgentDbAsync();
        var n = await scope.ServiceProvider.GetRequiredService<PricingTool.Agent.Campaigns.CampaignCalendar>()
            .SetGoalAsync(long.Parse(positional[0]), positional[1], decimal.Parse(positional[2], System.Globalization.CultureInfo.InvariantCulture), Opt("--by") ?? "user", Opt("--note"));
        Console.WriteLine($"Goal set on calendar #{positional[0]}: {positional[1]} {positional[2]}; {n} open brief(s) updated.");
        return 0;
    }

    case "campaign-note":
    {
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.MigrateAgentDbAsync();
        var e = await scope.ServiceProvider.GetRequiredService<PricingTool.Agent.Campaigns.CampaignCalendar>()
            .AddContextAsync(long.Parse(positional[0]), positional[1], Opt("--by") ?? "user", args.Contains("--not-representative"));
        Console.WriteLine($"#{e.Id} {e.Name} (layer {e.LayerId}): representative = {e.Representative}; context: {e.Context}");
        return 0;
    }

    case "plan-campaign":
    {
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.MigrateAgentDbAsync();
        var sw = Stopwatch.StartNew();
        var briefId = long.Parse(positional[0]);
        var r = await scope.ServiceProvider.GetRequiredService<PricingTool.Agent.Campaigns.CampaignPlanning>().PlanAsync(briefId, args.Contains("--publish"));
        Console.WriteLine($"Brief #{briefId}: {r.Status} in {sw.Elapsed.TotalSeconds:0} s {r.Error}");
        foreach (var p in r.Plans)
            Console.WriteLine($"  plan #{p.Id} round {p.Round} {p.Strategy} [{p.Status}]: {p.DiscountedSkus:N0}/{p.SkusInScope:N0} SKUs at {p.AverageDepthPct:0.#}%, " +
                              $"units {p.ExpectedUnits:N0}, GP {p.ExpectedGrossProfit:N0} (no discounts {p.GrossProfitWithoutDiscounts:N0}), margin {p.ExpectedMarginPct:0.0}%. {p.Feasibility}");
        foreach (var m in await scope.ServiceProvider.GetRequiredService<AgentDbContext>().CampaignMessages.AsNoTracking().Where(m => m.BriefId == briefId).OrderBy(m => m.Id).ToListAsync())
            Console.WriteLine($"  [{m.Round}] {m.From}: {m.Text}");
        return r.Status is "Failed" or "Refused" ? 1 : 0;
    }

    case "cost-watch":
    {
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.MigrateAgentDbAsync();
        var sp = scope.ServiceProvider;
        if (args.Contains("--poll"))
        {
            var f = await sp.GetRequiredService<PricingTool.NopIntegration.CostWatch.ReplicaCostFeed>().PollAsync();
            Console.WriteLine($"Replica: {f.Refreshed} of {f.Countries} countries refreshed, {f.SkusRead} costs read, {f.Observations} moved, {f.ReplicaMs} ms {f.Note}");
        }
        if (Array.IndexOf(args, "--inject") is var at and >= 0)
        {
            var db = sp.GetRequiredService<AgentDbContext>();
            db.CostObservations.Add(new CostObservation
            {
                LayerId = int.Parse(args[at + 1]), Sku = args[at + 2], Pptcv = decimal.Parse(args[at + 3], System.Globalization.CultureInfo.InvariantCulture),
                ObservedUtc = DateTime.UtcNow, Source = "[INJECTED-TEST] cost-watch --inject",
            });
            await db.SaveChangesAsync();
            Console.WriteLine($"Injected [INJECTED-TEST] cost {args[at + 3]} for {args[at + 2]} on layer {args[at + 1]}.");
        }
        var r = await sp.GetRequiredService<PricingTool.NopIntegration.CostWatch.CostWatcher>().WatchAsync();
        Console.WriteLine($"Cost watch: {r.Observed} observations, {r.Raised} raised, {r.Held} held, {r.Ended} ended, {r.NoAction} no action {r.Skipped}");
        foreach (var o in await sp.GetRequiredService<AgentDbContext>().CostObservations.AsNoTracking().OrderByDescending(o => o.Id).Take(5).ToListAsync())
        {
            var d = o.DecisionId is { } id ? await sp.GetRequiredService<AgentDbContext>().AgentDecisions.AsNoTracking().SingleAsync(x => x.Id == id) : null;
            Console.WriteLine($"  #{o.Id} {o.Sku} cost {o.Pptcv:0.00} ({o.Source}): {o.Outcome} batch {o.PublishBatchId} {d?.Reason}");
        }
        return 0;
    }

    case "nova-pass":
    {
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.MigrateAgentDbAsync();
        if (args.Contains("--preview"))
        {
            Console.WriteLine(await scope.ServiceProvider.GetRequiredService<PricingTool.Agent.Campaigns.CampaignAgent>().PreviewPromptAsync(int.Parse(positional[0])));
            return 0;
        }
        var sw = Stopwatch.StartNew();
        var r = await scope.ServiceProvider.GetRequiredService<PricingTool.Agent.Campaigns.CampaignAgent>().RunPassAsync(int.Parse(positional[0]), args.Contains("--test"));
        Console.WriteLine($"Nova pass #{r.PassId}: {r.Status} in {sw.Elapsed.TotalSeconds:0} s, {r.Briefs.Count} briefs, {r.ResultsWritten} results {r.Error}");
        foreach (var b in r.Briefs)
            Console.WriteLine($"  [{b.Status}{(b.Limit is null ? "" : " " + b.Limit)}] {b.Kind} '{b.Name}' {b.ScopeKind} {b.ScopeValue} {b.StartDate:yyyy-MM-dd}..{b.EndDate:yyyy-MM-dd} " +
                              $"goal {b.GoalType} {b.GoalValue:0.##}, min margin {b.MinMarginPct:0.#}%, max depth {b.MaxDepthPct:0.#}%\n      {b.Rationale}" +
                              (b.LimitDetail is null ? "" : $"\n      code: {b.LimitDetail}"));
        return r.Status is "Succeeded" or "Skipped" ? 0 : 1;
    }

    case "campaign-data":
    {
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.MigrateAgentDbAsync();
        var (added, moved) = await scope.ServiceProvider.GetRequiredService<PricingTool.Agent.Campaigns.CampaignCalendar>().SyncAsync();
        Console.WriteLine($"Campaign calendar: {added} added, {moved} statuses moved.");
        var tools = scope.ServiceProvider.GetRequiredService<AgentDataTools>();
        var ctx = scope.ServiceProvider.GetRequiredService<AgentToolContext>();
        var layer = int.Parse(positional[0]);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        var sw = Stopwatch.StartNew();
        Console.WriteLine("== get_campaign_history");
        Console.WriteLine(JsonSerializer.Serialize(await tools.GetCampaignHistoryAsync(ctx, layer, Opt("--category")), json));
        Console.WriteLine($"-- {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        Console.WriteLine("== get_sales_trends");
        Console.WriteLine(JsonSerializer.Serialize(await tools.GetSalesTrendsAsync(ctx, layer, Opt("--category"), int.Parse(Opt("--weeks") ?? "8")), json));
        Console.WriteLine($"-- {sw.ElapsedMilliseconds} ms");
        if (Opt("--results") is { } id)
        {
            Console.WriteLine("== get_campaign_results");
            Console.WriteLine(JsonSerializer.Serialize(await tools.GetCampaignResultsAsync(ctx, long.Parse(id)), json));
        }
        return 0;
    }

    case "outcomes":
    {
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.MigrateAgentDbAsync();
        if (Opt("--probe") is { } probeId)
        {
            var day = DateOnly.Parse(args[Array.IndexOf(args, "--probe") + 2], System.Globalization.CultureInfo.InvariantCulture);
            var sw = Stopwatch.StartNew();
            var (f, basis) = await scope.ServiceProvider.GetRequiredService<OutcomeTracker>().ProbeAsync(long.Parse(probeId), day);
            Console.WriteLine($"Probe (nothing written), {sw.ElapsedMilliseconds} ms: {basis}");
            Console.WriteLine($"  GP/day {f.GpPerDayBefore:0} -> {f.GpPerDayAfter:0} (expected {f.ExpectedGpPerDayAfter:0}, {f.GpVsExpectedPct:0.0}%), " +
                              $"units/day {f.UnitsPerDayBefore:0.0} -> {f.UnitsPerDayAfter:0.0} ({f.UnitsVsExpectedPct:0.0}%): {f.Verdict} {f.Why}");
            return 0;
        }
        var r = await scope.ServiceProvider.GetRequiredService<OutcomeTracker>().UpdateAsync();
        Console.WriteLine($"{r.Decisions} applied changes: {r.NotLive} not live yet, {r.Waiting} waiting, {r.Measured} measured, {r.RolledBack} rolled back.");
        foreach (var x in await scope.ServiceProvider.GetRequiredService<AgentDbContext>().AgentOutcomes.AsNoTracking().OrderBy(x => x.DecisionId).ToListAsync())
            Console.WriteLine($"  decision #{x.DecisionId} L{x.LayerId} band {x.BandId} {x.Category}: {x.Status} {x.Verdict} {x.Action}  {x.Evidence}");
        return 0;
    }

    case "mark-pushed":
    {
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.MigrateAgentDbAsync();
        var batchId = long.Parse(positional[0]);
        var by = Opt("--by") ?? host.Services.GetRequiredService<IOptions<AgentOptions>>().Value.Cycle.PushedBy;
        try
        {
            var approvalId = await scope.ServiceProvider.GetRequiredService<CheckedExport>().MarkPushedAsync(batchId, by);
            Console.WriteLine($"Export batch #{batchId} recorded as pushed by {by}: approval #{approvalId}. The ledger measures it at 7 and 30 days.");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    case "ledger":
    {
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.MigrateAgentDbAsync();
        var ledger = scope.ServiceProvider.GetRequiredService<MoneyLedger>();
        var (added, measured) = await ledger.UpdateAsync();
        var sum = await ledger.SummaryAsync();
        Console.WriteLine($"{added} new entries, {measured} windows measured. Measured 30-day total {sum.Measured30Eur:0} EUR over {sum.Entries} entries " +
                          $"({sum.Measured30} measured at 30 days; unmeasured count as 0). Bar: " + (sum.TargetEur is { } t ? $"{t:0} EUR (3x the raise)" : "raise not set"));
        foreach (var e in await scope.ServiceProvider.GetRequiredService<AgentDbContext>().MoneyLedger.AsNoTracking().OrderBy(e => e.Id).ToListAsync())
            Console.WriteLine($"  #{e.Id} {e.Source} {e.RefId} layer {e.LayerId} applied {e.AppliedUtc:yyyy-MM-dd}, {e.Skus} SKUs: expected {e.ExpectedEur:0} (estimated), " +
                              $"7d {e.Measured7Eur?.ToString("0") ?? "-"}, 30d {e.Measured30Eur?.ToString("0") ?? "-"}  {e.Evidence}");
        return 0;
    }

    case "timesheet":
    {
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var r = await scope.ServiceProvider.GetRequiredService<TimeSheetExporter>().ExportAsync();
        Console.WriteLine($"{r.Exported} rows -> {r.XlsxPath} (+ .csv, .json); {r.WithoutMinutesBefore} rows wait for their task type's minutes before.");
        return 0;
    }

    case "cycle-status":
    {
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var day = positional.Count > 0 ? DateOnly.Parse(positional[0], System.Globalization.CultureInfo.InvariantCulture)
                                       : scope.ServiceProvider.GetRequiredService<DailyCycle>().Today();
        var steps = await scope.ServiceProvider.GetRequiredService<AgentDbContext>().DailyCycleSteps.AsNoTracking()
            .Where(s => s.CycleDate == day).OrderBy(s => s.Id).ToListAsync();
        Console.WriteLine(steps.Count == 0 ? $"No cycle steps on {day:yyyy-MM-dd}." : $"Daily cycle {day:yyyy-MM-dd}:");
        foreach (var x in steps)
            Console.WriteLine($"  {x.StartedUtc.ToLocalTime():HH:mm} {x.Step,-14} {x.Status,-9} {(x.FinishedUtc - x.StartedUtc)?.TotalSeconds,6:0}s  {x.Detail ?? x.Error}");
        return 0;
    }

    case "test-db":
    {
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.MigrateAgentDbAsync();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        Console.WriteLine($"{db.Database.GetDbConnection().Database}: test records only (no mock data in the live database).");
        foreach (var p in await db.AgentPasses.AsNoTracking().OrderBy(p => p.Id).ToListAsync())
            Console.WriteLine($"  pass #{p.Id} layer {p.LayerId} {p.StartedUtc:yyyy-MM-dd HH:mm} {p.Status}, test={p.IsTest}: {p.Summary?[..Math.Min(p.Summary.Length, 90)]}");
        Console.WriteLine($"  {await db.AgentDecisions.CountAsync()} decisions, {await db.AgentRunChecks.CountAsync()} run checks, " +
                          $"{await db.AiCallLog.CountAsync()} AI calls (${await db.AiCallLog.SumAsync(c => c.CostUsd ?? 0):0.00}), " +
                          $"{await db.AgentQueryLog.CountAsync()} logged queries, {await db.TimeSheetEntries.CountAsync()} time-sheet rows");
        return 0;
    }

    case "publish-rollback":
    {
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var batchId = long.Parse(positional[0]);
        var restored = await scope.ServiceProvider.GetRequiredService<NopDiscountWriter>().RollbackAsync(batchId);
        Console.WriteLine($"Rolled back batch #{batchId}: {restored} rows restored from ai.PublishAudit");
        return 0;
    }

    default:
        Console.Error.WriteLine("Usage: PricingTool.AgentTools mcp|scorecard|query|ask|alpha-pass|sandbox|publish-canary|publish-rollback|check-run|export-run|mark-pushed|ledger|outcomes|campaign-data|nova-pass|plan-campaign|campaign-note|campaign-goal|approve-plan|backtest-campaigns|backtest-levers|reproduce-per-ty|lesson|lessons|lesson-retire|cost-watch|timesheet|cycle-status|test-db ... (see Program.cs)");
        return 2;
}

// The discount rows a publish batch created or updated, with the product flags, read back from the sandbox schema
// it wrote to (the sandbox lives in the tool DB; a live batch's rows are in the shop DB, so only its audit is shown).
static async Task PrintWrittenAsync(string toolConnection, PublishBatch batch, string title)
{
    Console.WriteLine($"{title} (batch #{batch.Id}, {batch.ShopSchema}):");
    var sandbox = batch.Target == PublisherOptions.SandboxTarget;
    await using var c = new SqlConnection(toolConnection);
    await c.OpenAsync();
    await using var cmd = new SqlCommand(sandbox ? $"""
        SELECT a.Sku, d.Id, d.Name, d.DiscountAmount, d.StartDateUtc, d.EndDateUtc, d.StoreId, d.DiscountApplyTypeId, d.DiscountPlatformTypeId,
               p.HasDiscountsApplied, p.HasDiscountsAppliedStoreIds, a.Change
        FROM ai.PublishAudit a
        JOIN [{batch.ShopSchema}].[Discount] d ON d.Id = a.DiscountId
        LEFT JOIN [{batch.ShopSchema}].[Product] p ON p.Id = a.ProductId
        WHERE a.BatchId = @B AND a.Change IN ('DiscountCreated', 'DiscountUpdated') AND a.Applied = 1
        ORDER BY a.Sku
        """ : "SELECT Sku, DiscountId, DiscountName, NewDiscountAmount, NewStartDateUtc, NewEndDateUtc, 0, 0, 0, NULL, NewStoreIds, Change FROM ai.PublishAudit WHERE BatchId = @B AND Applied = 1 ORDER BY Sku", c);
    cmd.Parameters.AddWithValue("@B", batch.Id);
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
        Console.WriteLine($"  {r.GetString(0),-22} #{r.GetValue(1)} {r.GetValue(2)} amount {r.GetValue(3):0.00} " +
                          $"{r.GetValue(4):yyyy-MM-dd HH:mm:ss} -> {r.GetValue(5):yyyy-MM-dd HH:mm:ss} store {r.GetValue(6)} apply {r.GetValue(7)} platform {r.GetValue(8)} " +
                          $"| product flags {r.GetValue(9)} '{r.GetValue(10)}' ({r.GetString(11)})");
}
