using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PricingTool.Agent;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.ClaudeCode;
using PricingTool.Agent.Storage;
using PricingTool.Agent.Tools;
using PricingTool.AgentTools;

// PricingTool.AgentTools: the agents' data tools as an MCP server, plus commands to check them by hand.
//   mcp --agent <Alpha|Nova|RunChecker|Chat> [--call-tag <tag>]   stdio MCP server (Claude Code starts it)
//   scorecard <layer> [<layer> ...]                                 print Alpha's scorecard and how long it took
//   query [--agent <role>] [--timeout <s>] "<sql>"                  run query_data with its guardrails
//   ask --agent <role> [--schema <file>] "<question>"               one real Claude Code call with the data tools
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

builder.Services.AddPricingAgent(builder.Configuration);
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

    default:
        Console.Error.WriteLine("Usage: PricingTool.AgentTools mcp|scorecard|query|ask ... (see Program.cs)");
        return 2;
}
