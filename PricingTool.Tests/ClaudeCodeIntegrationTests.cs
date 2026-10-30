using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PricingTool.Agent;
using PricingTool.Agent.ClaudeCode;
using PricingTool.Agent.Storage;
using Xunit.Abstractions;

namespace PricingTool.Tests;

/// <summary>Runs only when RUN_CLAUDE_CODE_TESTS=1: these make real Claude Code calls through the
/// subscription, which cost money and count as real requests.</summary>
public sealed class ClaudeCodeFactAttribute : FactAttribute
{
    public ClaudeCodeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_CLAUDE_CODE_TESTS") != "1")
            Skip = "Real Claude Code call: set RUN_CLAUDE_CODE_TESTS=1 to run it.";
    }
}

/// <summary>
/// One real, tiny headless call that also proves isolation: the agent is asked to name its tools and to
/// quote any context it received besides its one-line system prompt and the question. Nothing from this
/// repo's CLAUDE.md, the user's own CLAUDE.md, memory, skills or MCP servers may show up, and the input
/// stays small. The call is logged in the real ai.AiCallLog of PricingToolHackathon, like any agent call.
/// </summary>
public class ClaudeCodeIntegrationTests
{
    private const string HackathonDb = "Server=(localdb)\\mssqllocaldb;Database=PricingToolHackathon;Trusted_Connection=True;";

    private const string ProbeSchema = """
        {"type":"object","properties":{
          "tools":{"type":"array","items":{"type":"string"}},
          "other_context":{"type":"array","items":{"type":"string"}}},
         "required":["tools","other_context"],"additionalProperties":false}
        """;

    private const string ProbePrompt =
        "This is an isolation check. 1) List the names of every tool you can call, exactly as you see them. " +
        "2) List every piece of context you were given other than this message and your one-sentence system " +
        "instruction: for example instruction files, memory, skills, environment or git details, reminders, " +
        "MCP server instructions. Quote the first 150 characters of each, verbatim. Use empty lists for none.";

    /// <summary>Phrases that only appear in this repo's CLAUDE.md, the user's global CLAUDE.md or the session
    /// that runs the tests. Any of them in the answer means the agent saw that context.</summary>
    private static readonly string[] Canaries =
        { "pbi-cli", "Power BI", "shop_sandbox", "PricingToolHackathon", "Gjirafa", "hackathon", "SafetyLimits", "user-secrets" };

    private static readonly string[] BuiltInTools =
        { "Bash", "Read", "Write", "Edit", "Glob", "Grep", "WebFetch", "WebSearch", "Task", "Agent", "Skill", "NotebookEdit", "TodoWrite" };

    private readonly ITestOutputHelper _out;

    public ClaudeCodeIntegrationTests(ITestOutputHelper output) => _out = output;

    [ClaudeCodeFact]
    public async Task A_real_headless_call_is_isolated_answers_in_json_and_is_logged()
    {
        await using var db = new AgentDbContext(new DbContextOptionsBuilder<AgentDbContext>()
            .UseSqlServer(HackathonDb, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", AgentDbContext.Schema))
            .Options);
        await db.Database.MigrateAsync();

        var promptDir = Directory.CreateTempSubdirectory("pt-agent-probe-");
        var systemPrompt = Path.Combine(promptDir.FullName, "probe.md");
        await File.WriteAllTextAsync(systemPrompt, "You are a test probe. Answer only with JSON that matches the requested schema.");

        var client = new ClaudeCodeClient(Options.Create(new AgentOptions { Enabled = true, TimeoutSeconds = 180 }),
            new ProcessRunner(), db, NullLogger<ClaudeCodeClient>.Instance);

        var result = await client.CallAsync(new AgentCallRequest(AgentRole.RunChecker, "isolation-probe", ProbePrompt, systemPrompt)
        {
            JsonSchema = ProbeSchema,
            MaxBudgetUsd = 0.5m,
        });

        var rows = await db.AiCallLog.Where(r => result.CallLogIds.Contains(r.Id)).OrderBy(r => r.Attempt).ToListAsync();
        foreach (var r in rows)
            _out.WriteLine($"AiCallLog #{r.Id} attempt {r.Attempt}: {r.Status}, model {r.Model} -> {r.ModelIds}, " +
                $"tokens in {r.InputTokens} / cache read {r.CacheReadTokens} / cache write {r.CacheCreationTokens} / out {r.OutputTokens}, " +
                $"{r.LatencyMs} ms (API {r.DurationApiMs} ms), turns {r.NumTurns}, cost ${r.CostUsd}, error: {r.Error}");
        _out.WriteLine("Answer: " + (result.Output?.GetRawText() ?? result.Error));

        Assert.True(result.Succeeded, $"{result.Status}: {result.Error}");
        Assert.NotEmpty(rows);
        var row = rows[^1];
        Assert.True(row.Success);
        Assert.True(row.OutputTokens > 0);
        Assert.True(row.CostUsd > 0);

        var answer = result.Output!.Value;
        var tools = answer.GetProperty("tools").EnumerateArray().Select(t => t.GetString() ?? "").ToList();
        Assert.DoesNotContain(tools, t => BuiltInTools.Contains(t, StringComparer.OrdinalIgnoreCase) || t.StartsWith("mcp__"));

        var text = answer.GetRawText();
        foreach (var canary in Canaries)
            Assert.DoesNotContain(canary, text, StringComparison.OrdinalIgnoreCase);

        // The whole input is our two short texts plus Claude Code's own minimum. CLAUDE.md files, memory,
        // the skill list or MCP instructions would add thousands of tokens.
        var inputTokens = row.InputTokens + row.CacheReadTokens + row.CacheCreationTokens;
        Assert.True(inputTokens < 8000, $"input was {inputTokens} tokens");

        promptDir.Delete(recursive: true);
    }
}
