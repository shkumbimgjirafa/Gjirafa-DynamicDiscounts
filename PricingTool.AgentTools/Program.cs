using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PricingTool.Agent;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Checker;
using PricingTool.Agent.ClaudeCode;
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
//   sandbox [check|create [--recreate]|script]                      the shop sandbox: contract check, build + seed, DDL
//   publish-canary <layer> --run <id> --category <c> [--max n] [--rollback]   publish a run's sample, show the rows
//   publish-rollback <batch id>                                     restore what a publish batch changed
//   check-run <run id> [--test]                                     the pre-publish run check (one model call)
//   export-run <run id> [--out <dir>]                               the checked export for the push (refused when blocked)
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
                JOIN hk.Products p ON p.LayerId = pp.LayerId AND p.ProductId = pp.ProductId
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

        var type = await db.TimeSheetTaskTypes.SingleAsync(t => t.Id == AgentDbContext.TaskTypes.CreateAndPushDiscounts);
        db.TimeSheetEntries.Add(new TimeSheetEntry
        {
            Date = DateOnly.FromDateTime(DateTime.Now), UserEmail = builder.Configuration["Agent:TimeSheetOwnerEmail"] ?? "",
            TaskTypeId = type.Id, MinutesBefore = type.MinutesBefore, MinutesAfter = Math.Round((decimal)sw.Elapsed.TotalMinutes, 2),
            What = $"Layer {layer} canary, engine run #{run}, '{category}': {outcome.Created + outcome.Updated} discounts written to the {batch.Target.ToLowerInvariant()} " +
                   $"({batch.ShopSchema}), {outcome.Skipped} skipped",
            Proof = $"ai.PublishBatch #{batch.Id}", Comment = "Canary publish; minutes after = the publish's wall clock.",
        });
        await db.SaveChangesAsync();

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
        Console.Error.WriteLine("Usage: PricingTool.AgentTools mcp|scorecard|query|ask|alpha-pass|sandbox|publish-canary|publish-rollback|check-run|export-run ... (see Program.cs)");
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
