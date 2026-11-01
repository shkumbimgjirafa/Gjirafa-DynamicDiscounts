using System.Text.Json;
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

namespace PricingTool.Tests;

/// <summary>
/// The Claude Code client against a fake process: the command line it builds (isolation flags, model per
/// role, prompt on stdin), the child environment, schema validation with one retry, typed failures, the
/// kill switch, and the AiCallLog row written for every attempt.
/// </summary>
public class ClaudeCodeClientTests : IDisposable
{
    private const string Schema = """
        {"type":"object","properties":{"action":{"enum":["raise","lower","keep"]},"confidence":{"type":"number","minimum":0,"maximum":1}},
         "required":["action","confidence"],"additionalProperties":false}
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pt-agent-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _systemPrompt;
    private readonly AgentDbContext _db;

    public ClaudeCodeClientTests()
    {
        Directory.CreateDirectory(_dir);
        _systemPrompt = Path.Combine(_dir, "rules.md");
        File.WriteAllText(_systemPrompt, "You are a test agent.");
        _db = new AgentDbContext(new DbContextOptionsBuilder<AgentDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeRunner : IProcessRunner
    {
        private readonly Queue<Func<ProcessSpec, ProcessResult>> _results = new();
        public List<ProcessSpec> Calls { get; } = new();
        public FakeRunner Returns(ProcessResult r) { _results.Enqueue(_ => r); return this; }
        public FakeRunner Throws(Exception ex) { _results.Enqueue(_ => throw ex); return this; }
        public FakeRunner Then(Func<ProcessSpec, ProcessResult> f) { _results.Enqueue(f); return this; }

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default)
        {
            Calls.Add(spec);
            return Task.FromResult(_results.Dequeue()(spec));
        }
    }

    private static ProcessResult Ok(string envelope) => new(0, envelope, "", false, TimeSpan.FromMilliseconds(1234));

    /// <summary>The shape <c>claude -p --output-format json</c> prints.</summary>
    private static string Envelope(string? result = null, string? structured = null, bool isError = false, string subtype = "success")
    {
        var o = new JsonObject
        {
            ["type"] = "result", ["subtype"] = subtype, ["is_error"] = isError,
            ["duration_ms"] = 1100, ["duration_api_ms"] = 900, ["num_turns"] = 1,
            ["result"] = result ?? "", ["session_id"] = "s", ["total_cost_usd"] = 0.0123m,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = 120, ["cache_creation_input_tokens"] = 300,
                ["cache_read_input_tokens"] = 4000, ["output_tokens"] = 45,
            },
            ["modelUsage"] = new JsonObject { ["claude-sonnet-5-5"] = new JsonObject { ["costUSD"] = 0.0123m } },
        };
        if (structured is not null) o["structured_output"] = JsonNode.Parse(structured);
        return o.ToJsonString();
    }

    private ClaudeCodeClient Client(FakeRunner runner, bool enabled = true) =>
        new(Options.Create(new AgentOptions
        {
            Enabled = enabled,
            RuntimeDirectory = Path.Combine(_dir, "runtime"),
            TimeoutSeconds = 60,
            MaxBudgetUsdPerCall = 1.5m,
        }), runner, _db, NullLogger<ClaudeCodeClient>.Instance);

    private AgentCallRequest Request(AgentRole role = AgentRole.RunChecker, string? schema = Schema) =>
        new(role, "unit-test", "Judge these rows: [secret-free test prompt]", _systemPrompt) { JsonSchema = schema };

    private sealed class Meter : IUsageMeter
    {
        public List<UsageEvent> Events { get; } = new();
        public void Record(UsageEvent e) => Events.Add(e);
    }

    [Fact]
    public async Task Each_attempt_that_reached_the_model_is_one_task_for_the_meter_and_an_injected_test_is_not()
    {
        var meter = new Meter();
        var runner = new FakeRunner()
            .Returns(Ok(Envelope(structured: """{"action":"keep","confidence":0.8}""")))
            .Returns(Ok(Envelope(structured: """{"action":"keep","confidence":0.8}""")));
        var client = new ClaudeCodeClient(Options.Create(new AgentOptions { Enabled = true, RuntimeDirectory = Path.Combine(_dir, "runtime"), TimeoutSeconds = 60 }),
            runner, _db, NullLogger<ClaudeCodeClient>.Instance, meter: meter);

        await client.CallAsync(Request(AgentRole.Alpha) with { Purpose = "campaign-plan brief 10 r1" });
        await client.CallAsync(Request() with { Purpose = "[INJECTED-TEST] run check 7" });

        var e = Assert.Single(meter.Events);                                                // the labelled test isn't counted
        Assert.Equal(("alpha campaign-plan brief", 1, 4420L, 45L, true), (e.Task, e.AiRequests, e.TokensIn, e.TokensOut, e.Ok));   // input + cache, output
        Assert.True(e.EndedUtc >= e.StartedUtc);
    }

    [Fact]
    public async Task A_core_task_run_counts_its_calls_in_one_event_and_a_call_outside_it_is_its_own()
    {
        var meter = new Meter();
        var runner = new FakeRunner();
        for (var i = 0; i < 3; i++) runner.Returns(Ok(Envelope(structured: """{"action":"keep","confidence":0.8}""")));
        var client = new ClaudeCodeClient(Options.Create(new AgentOptions { Enabled = true, RuntimeDirectory = Path.Combine(_dir, "runtime"), TimeoutSeconds = 60 }),
            runner, _db, NullLogger<ClaudeCodeClient>.Instance, meter: meter);

        using (var run = UsageRun.Start(meter, CoreTasks.CampaignPlanning, new { brief = 10 }))
        {
            await client.CallAsync(Request(AgentRole.Alpha) with { Purpose = "campaign-plan brief 10 r1" });
            await client.CallAsync(Request(AgentRole.Nova) with { Purpose = "campaign-review brief 10 r1" });
            run.Source = new UsageSource(CoreTasks.ShopSql, CoreTasks.ShopCopy, new[] { "agent.SalesLines" }, 420);
            (run.Units, run.Ok) = (1, true);
            Assert.Empty(meter.Events);                                                     // nothing sent while the run is open
        }
        Assert.Null(UsageRun.Current);
        await client.CallAsync(Request(AgentRole.Nova) with { Purpose = "chat 4" });

        Assert.Equal(2, meter.Events.Count);
        var e = meter.Events[0];
        Assert.Equal((CoreTasks.CampaignPlanning, 2, 8840L, 90L, true, (int?)1, 420L),
            (e.Task, e.AiRequests, e.TokensIn, e.TokensOut, e.Ok, e.Units, e.Source!.RowsRead));   // both calls: input + cache, output
        Assert.Equal("nova chat", meter.Events[1].Task);
        Assert.Null(meter.Events[1].Units);
    }

    [Fact]
    public void A_run_not_marked_done_is_not_ok_and_a_test_run_records_nothing()
    {
        var meter = new Meter();
        using (UsageRun.Start(meter, CoreTasks.RunCheck, new { run = 7 })) { }
        using (var test = UsageRun.Start(null, CoreTasks.RunCheck, new { run = 8 })) (test.Units, test.Ok) = (1, true);

        var e = Assert.Single(meter.Events);
        Assert.Equal((false, 0), (e.Ok, e.Units));
    }

    [Fact]
    public void The_meter_sends_units_source_and_input_only_for_a_run()
    {
        var at = new DateTime(2026, 11, 1, 16, 0, 0, DateTimeKind.Utc);
        var call = HacktrackMeter.Json(new UsageEvent("nova chat", at, at, 1, 10, 2, true));
        foreach (var field in new[] { "units", "source", "input" }) Assert.DoesNotContain($"\"{field}\"", call);

        var run = JsonNode.Parse(HacktrackMeter.Json(new UsageEvent(CoreTasks.RunCheck, at, at, 1, 10, 2, true)
        {
            Units = 1, Source = new UsageSource(CoreTasks.ShopSql, CoreTasks.ShopCopy, new[] { "PricingTool.ProposedPrices" }, 46258), Input = new { run = 11112 },
        }))!;
        Assert.Equal(1, (int)run["units"]!);
        Assert.Equal(("shop-sql", 46258L, "PricingTool.ProposedPrices"),
            ((string)run["source"]!["system"]!, (long)run["source"]!["rows_read"]!, (string)run["source"]!["tables"]![0]!));
        Assert.Equal(11112, (int)run["input"]!["run"]!);                                     // hook.js hashes it; never sent as is
    }

    [Fact]
    public void The_meter_names_a_task_without_its_ids_and_sends_what_hook_js_takes()
    {
        Assert.Equal("nova chat", HacktrackMeter.TaskOf(AgentRole.Nova, "chat 4"));
        Assert.Equal("alpha problems", HacktrackMeter.TaskOf(AgentRole.Alpha, "problems"));
        var json = HacktrackMeter.Json(new UsageEvent("nova chat", new DateTime(2026, 10, 31, 16, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 31, 16, 0, 15, DateTimeKind.Utc), 2, 1275, 300, true));
        foreach (var part in new[] { "\"kind\":\"run\"", "\"task\":\"nova chat\"", "\"ai_requests\":2", "\"tokens_in\":1275", "\"tokens_out\":300", "\"ok\":true",
                     "\"started_at\":\"2026-10-31T16:00:00.0000000Z\"" })
            Assert.Contains(part, json);
    }

    [Fact]
    public async Task The_meter_hands_node_the_event_intact()
    {
        // A stand-in for the organisers' hook.js that only writes down what it was given: nothing is sent anywhere.
        var hook = Path.Combine(_dir, "fake-hook.js");
        var got = Path.Combine(_dir, "got.json");
        File.WriteAllText(hook, $"require('fs').writeFileSync({JsonSerializer.Serialize(got)}, process.argv[3]);");
        var (oldHook, oldToken) = (Environment.GetEnvironmentVariable("HACKTRACK_HOOK"), Environment.GetEnvironmentVariable("HACKTRACK_TOKEN"));
        try
        {
            Environment.SetEnvironmentVariable("HACKTRACK_HOOK", hook);
            Environment.SetEnvironmentVariable("HACKTRACK_TOKEN", "test-token");
            var e = new UsageEvent("nova chat \"quoted\" task", DateTime.UtcNow, DateTime.UtcNow, 1, 10, 2, true);
            new HacktrackMeter(NullLogger<HacktrackMeter>.Instance).Record(e);
            for (var i = 0; i < 100 && !File.Exists(got); i++) await Task.Delay(100);
            if (!File.Exists(got)) return;                                              // no node on this machine: nothing to check
            Assert.Equal(HacktrackMeter.Json(e), File.ReadAllText(got));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HACKTRACK_HOOK", oldHook);
            Environment.SetEnvironmentVariable("HACKTRACK_TOKEN", oldToken);
        }
    }

    [Fact]
    public async Task Builds_an_isolated_headless_command_with_the_prompt_on_stdin()
    {
        var runner = new FakeRunner().Returns(Ok(Envelope(structured: """{"action":"keep","confidence":0.8}""")));

        await Client(runner).CallAsync(Request(AgentRole.Alpha));

        var spec = Assert.Single(runner.Calls);
        var args = spec.Arguments.ToList();
        Assert.Equal("claude", spec.FileName);
        Assert.Equal(Path.Combine(_dir, "runtime"), spec.WorkingDirectory);
        Assert.Contains("-p", args);
        Assert.Equal("json", args[args.IndexOf("--output-format") + 1]);
        Assert.Equal("claude-opus-5-5", args[args.IndexOf("--model") + 1]);   // Alpha reasons on Opus 5.5, pinned
        Assert.Equal(_systemPrompt, args[args.IndexOf("--system-prompt-file") + 1]);
        Assert.Equal("", args[args.IndexOf("--setting-sources") + 1]);       // no user/project/local settings
        Assert.Equal("", args[args.IndexOf("--tools") + 1]);                 // no built-in tools
        Assert.Contains("--strict-mcp-config", args);
        Assert.Contains("--disable-slash-commands", args);
        Assert.Contains("--no-session-persistence", args);
        Assert.Equal("1.5", args[args.IndexOf("--max-budget-usd") + 1]);
        Assert.Equal(Schema, args[args.IndexOf("--json-schema") + 1]);
        Assert.DoesNotContain("--bare", args);                               // --bare can't use the subscription login
        Assert.DoesNotContain("--mcp-config", args);
        Assert.Equal("Judge these rows: [secret-free test prompt]", spec.StandardInput);
        Assert.DoesNotContain(args, a => a.Contains("Judge these rows"));
        Assert.Equal(TimeSpan.FromSeconds(60), spec.Timeout);
    }

    [Fact]
    public void The_run_checker_uses_sonnet_and_our_mcp_tools_are_passed_only_when_given()
    {
        var client = Client(new FakeRunner());
        var spec = client.BuildProcessSpec(Request() with
        {
            McpConfigFile = Path.Combine(_dir, "mcp.json"),
            AllowedTools = new[] { "mcp__pricing__query_data", "mcp__pricing__get_current_config" },
            MaxBudgetUsd = 0.25m,
        }, _dir);

        var args = spec.Arguments.ToList();
        Assert.Equal("claude-sonnet-5-5", args[args.IndexOf("--model") + 1]);
        Assert.Equal(Path.Combine(_dir, "mcp.json"), args[args.IndexOf("--mcp-config") + 1]);
        Assert.Equal("mcp__pricing__query_data,mcp__pricing__get_current_config", args[args.IndexOf("--allowedTools") + 1]);
        Assert.Equal("0.25", args[args.IndexOf("--max-budget-usd") + 1]);
    }

    [Fact]
    public async Task Data_tools_come_from_a_per_call_mcp_config_tagged_with_the_agent_and_the_call()
    {
        string? config = null;
        var runner = new FakeRunner().Then(spec =>
        {
            config = File.ReadAllText(spec.Arguments[spec.Arguments.ToList().IndexOf("--mcp-config") + 1]);
            return Ok(Envelope(structured: """{"action":"keep","confidence":0.5}"""));
        });

        var result = await Client(runner).CallAsync(Request(AgentRole.Alpha) with { UseDataTools = true });

        var args = runner.Calls.Single().Arguments.ToList();
        var configPath = args[args.IndexOf("--mcp-config") + 1];
        Assert.Equal(Path.Combine(_dir, "runtime"), Path.GetDirectoryName(configPath));
        Assert.False(File.Exists(configPath));                                 // removed after the call
        Assert.Equal(string.Join(",", AgentToolNames.AllowedToolIds), args[args.IndexOf("--allowedTools") + 1]);
        Assert.Equal(11, AgentToolNames.AllowedToolIds.Count);                  // 7 pricing tools + 3 campaign tools + what_if_floor
        Assert.Contains("mcp__pricing__query_data", AgentToolNames.AllowedToolIds);

        var server = JsonNode.Parse(config!)!["mcpServers"]!["pricing"]!;
        Assert.EndsWith("PricingTool.AgentTools.exe", server["command"]!.GetValue<string>());
        Assert.Equal(new[] { "mcp", "--agent", "Alpha", "--call-tag", result.CallTag! },
            server["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal(result.CallTag, (await _db.AiCallLog.SingleAsync()).CallTag);
    }

    [Fact]
    public async Task Appended_prompt_files_are_joined_into_one_stable_system_prompt()
    {
        var dictionary = Path.Combine(_dir, "dictionary.md");
        File.WriteAllText(dictionary, "## Views\nagent.SalesLines ...");
        var runner = new FakeRunner()
            .Returns(Ok(Envelope(structured: """{"action":"keep","confidence":0.5}""")))
            .Returns(Ok(Envelope(structured: """{"action":"keep","confidence":0.5}""")));
        var request = Request() with { AppendSystemPromptFiles = new[] { dictionary } };

        await Client(runner).CallAsync(request);
        await Client(runner).CallAsync(request);

        var paths = runner.Calls.Select(c => c.Arguments[c.Arguments.ToList().IndexOf("--system-prompt-file") + 1]).ToList();
        Assert.Equal(paths[0], paths[1]);                                      // same text, same file: cache-friendly
        Assert.Equal("You are a test agent.\n\n## Views\nagent.SalesLines ...\n", File.ReadAllText(paths[0]));
    }

    [Fact]
    public void The_child_environment_drops_the_parent_sessions_claude_and_anthropic_variables()
    {
        var parent = new System.Collections.Hashtable
        {
            ["PATH"] = "C:\\bin",
            ["USERPROFILE"] = "C:\\Users\\x",
            ["CLAUDECODE"] = "1",
            ["CLAUDE_CODE_ENTRYPOINT"] = "desktop",
            ["CLAUDE_CODE_SIMPLE"] = "1",
            ["ANTHROPIC_API_KEY"] = "must-not-pass",
            ["ANTHROPIC_BASE_URL"] = "http://localhost:1234",
            ["MCP_CONNECTION_NONBLOCKING"] = "1",
            ["CLAUDE_CODE_OAUTH_TOKEN"] = "server-login",
            ["CLAUDE_CODE_GIT_BASH_PATH"] = "C:\\Git\\bin\\bash.exe",
        };

        var env = ClaudeCodeClient.ChildEnvironment(parent);

        Assert.Equal("C:\\bin", env["PATH"]);
        Assert.Equal("C:\\Users\\x", env["USERPROFILE"]);
        Assert.Equal("server-login", env["CLAUDE_CODE_OAUTH_TOKEN"]);
        Assert.Equal("C:\\Git\\bin\\bash.exe", env["CLAUDE_CODE_GIT_BASH_PATH"]);
        Assert.Equal("false", env["ENABLE_CLAUDEAI_MCP_SERVERS"]);
        Assert.Equal("false", env["MCP_CONNECTION_NONBLOCKING"]);             // set by us, not inherited
        foreach (var dropped in new[] { "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SIMPLE", "ANTHROPIC_API_KEY", "ANTHROPIC_BASE_URL" })
            Assert.False(env.ContainsKey(dropped), dropped);
    }

    [Fact]
    public async Task A_valid_structured_answer_succeeds_and_is_logged_with_its_cost()
    {
        var runner = new FakeRunner().Returns(Ok(Envelope(structured: """{"action":"raise","confidence":0.9}""")));

        var result = await Client(runner).CallAsync(Request());

        Assert.Equal(AgentCallStatus.Succeeded, result.Status);
        Assert.Equal("raise", result.Output!.Value.GetProperty("action").GetString());
        var row = Assert.Single(await _db.AiCallLog.ToListAsync());
        Assert.Equal(result.CallLogIds.Single(), row.Id);
        Assert.Equal("RunChecker", row.Agent);
        Assert.Equal("unit-test", row.Purpose);
        Assert.Equal(1, row.Attempt);
        Assert.Equal("claude-sonnet-5-5", row.Model);
        Assert.Equal("claude-sonnet-5-5", row.ModelIds);
        Assert.Equal((120, 45, 4000, 300), (row.InputTokens, row.OutputTokens, row.CacheReadTokens, row.CacheCreationTokens));
        Assert.Equal(0.0123m, row.CostUsd);
        Assert.Equal((1100, 900, 1), (row.DurationMs!.Value, row.DurationApiMs!.Value, row.NumTurns!.Value));
        Assert.Equal(1234, row.LatencyMs);
        Assert.True(row.Success);
        Assert.Equal("Succeeded", row.Status);
        Assert.Null(row.Error);
        Assert.Equal(Request().Prompt.Length, row.PromptChars);
    }

    [Fact]
    public async Task A_json_answer_in_the_result_text_is_accepted_when_there_is_no_structured_output()
    {
        var runner = new FakeRunner().Returns(Ok(Envelope(result: "```json\n{\"action\":\"keep\",\"confidence\":0.5}\n```")));

        var result = await Client(runner).CallAsync(Request());

        Assert.True(result.Succeeded);
        Assert.Equal(0.5, result.Output!.Value.GetProperty("confidence").GetDouble());
    }

    [Fact]
    public async Task An_answer_that_breaks_the_schema_is_retried_once_and_then_succeeds()
    {
        var runner = new FakeRunner()
            .Returns(Ok(Envelope(structured: """{"action":"double it","confidence":3}""")))
            .Returns(Ok(Envelope(structured: """{"action":"lower","confidence":0.6}""")));

        var result = await Client(runner).CallAsync(Request());

        Assert.True(result.Succeeded);
        Assert.Equal(2, runner.Calls.Count);
        var rows = await _db.AiCallLog.OrderBy(r => r.Attempt).ToListAsync();
        Assert.Equal(new[] { "InvalidOutput", "Succeeded" }, rows.Select(r => r.Status));
        Assert.Contains("Schema check failed", rows[0].Error);
        Assert.Contains("double it", rows[0].Output);                         // what the model said, for the audit
        Assert.Equal(result.CallLogIds, rows.Select(r => r.Id));
    }

    [Fact]
    public async Task Two_invalid_answers_end_in_a_typed_failure_with_no_output()
    {
        var runner = new FakeRunner()
            .Returns(Ok(Envelope(result: "I think you should raise it.")))
            .Returns(Ok(Envelope(structured: """{"action":"raise"}""")));

        var result = await Client(runner).CallAsync(Request());

        Assert.Equal(AgentCallStatus.InvalidOutput, result.Status);
        Assert.Null(result.Output);
        Assert.Null(result.Deserialize<JsonElement?>());
        Assert.NotNull(result.Error);
        Assert.Equal(2, await _db.AiCallLog.CountAsync(r => !r.Success));
    }

    [Fact]
    public async Task A_claude_code_error_is_retried_then_reported_as_cli_error()
    {
        var runner = new FakeRunner()
            .Returns(new ProcessResult(1, Envelope(result: "API Error: 529 overloaded", isError: true, subtype: "error_during_execution"), "", false, TimeSpan.FromSeconds(2)))
            .Returns(new ProcessResult(1, "", "Error: something broke before the answer", false, TimeSpan.FromSeconds(1)));

        var result = await Client(runner).CallAsync(Request());

        Assert.Equal(AgentCallStatus.CliError, result.Status);
        Assert.Contains("something broke", result.Error);
        var rows = await _db.AiCallLog.OrderBy(r => r.Attempt).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains("overloaded", rows[0].Error);
        Assert.Equal(0.0123m, rows[0].CostUsd);                               // a failed call still has a cost
        Assert.Null(rows[1].CostUsd);
    }

    [Fact]
    public async Task Not_logged_in_is_reported_plainly_and_not_retried()
    {
        // What Claude Code 2.1.205 prints when the machine has no subscription login (seen 2026-10-30).
        var notLoggedIn = new JsonObject
        {
            ["type"] = "result", ["subtype"] = "success", ["is_error"] = true, ["duration_ms"] = 47, ["duration_api_ms"] = 0,
            ["num_turns"] = 1, ["result"] = "Not logged in · Please run /login", ["total_cost_usd"] = 0,
        }.ToJsonString();
        var runner = new FakeRunner().Returns(new ProcessResult(1, notLoggedIn, "", false, TimeSpan.FromMilliseconds(1600)));

        var result = await Client(runner).CallAsync(Request());

        Assert.Equal(AgentCallStatus.CliError, result.Status);
        Assert.Contains("claude auth login", result.Error);
        Assert.Single(runner.Calls);
        var row = Assert.Single(await _db.AiCallLog.ToListAsync());
        Assert.Equal(0, row.InputTokens + row.OutputTokens);
    }

    [Fact]
    public async Task The_subscription_usage_limit_is_reported_and_not_retried()
    {
        // What Claude Code 2.1.205 printed mid-pass on 2026-10-30 (AiCallLog #6): the work so far was billed.
        var limited = new JsonObject
        {
            ["type"] = "result", ["subtype"] = "success", ["is_error"] = true, ["duration_api_ms"] = 125289, ["num_turns"] = 9,
            ["result"] = "You've hit your session limit · resets 7:10pm (Europe/Budapest)", ["total_cost_usd"] = 0.370873m,
            ["usage"] = new JsonObject { ["input_tokens"] = 20, ["output_tokens"] = 10037 },
        }.ToJsonString();
        var runner = new FakeRunner().Returns(new ProcessResult(1, limited, "", false, TimeSpan.FromSeconds(128)));

        var result = await Client(runner).CallAsync(Request(AgentRole.Alpha));

        Assert.Equal(AgentCallStatus.CliError, result.Status);
        Assert.Contains("usage limit is reached", result.Error);
        Assert.Contains("resets 7:10pm", result.Error);
        Assert.Single(runner.Calls);
        Assert.Equal(0.370873m, (await _db.AiCallLog.SingleAsync()).CostUsd);
    }

    [Theory]
    [InlineData("API Error: 400 Claude Code 2.1.205 does not support this model; version 2.1.280 or newer is required.", 1)]
    [InlineData("API Error: 429 rate_limit_error", 2)]
    [InlineData("API Error: 529 overloaded_error", 2)]
    public async Task Client_errors_are_not_retried_but_rate_limits_and_server_errors_are(string message, int expectedCalls)
    {
        var failed = new JsonObject
        {
            ["type"] = "result", ["subtype"] = "success", ["is_error"] = true, ["duration_api_ms"] = 0, ["result"] = message,
        }.ToJsonString();
        var runner = new FakeRunner()
            .Returns(new ProcessResult(1, failed, "", false, TimeSpan.FromSeconds(2)))
            .Returns(new ProcessResult(1, failed, "", false, TimeSpan.FromSeconds(2)));

        var result = await Client(runner).CallAsync(Request(AgentRole.Alpha));

        Assert.Equal(AgentCallStatus.CliError, result.Status);
        Assert.Equal(expectedCalls, runner.Calls.Count);
    }

    [Fact]
    public async Task A_timeout_is_a_typed_failure_and_is_logged()
    {
        var killed = new ProcessResult(-1, "", "", true, TimeSpan.FromSeconds(60));
        var runner = new FakeRunner().Returns(killed).Returns(killed);

        var result = await Client(runner).CallAsync(Request());

        Assert.Equal(AgentCallStatus.TimedOut, result.Status);
        Assert.Contains("60 s", result.Error);
        Assert.All(await _db.AiCallLog.ToListAsync(), r => Assert.Equal("TimedOut", r.Status));
    }

    [Fact]
    public async Task A_missing_executable_is_not_retried()
    {
        var runner = new FakeRunner().Throws(new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified."));

        var result = await Client(runner).CallAsync(Request());

        Assert.Equal(AgentCallStatus.CliError, result.Status);
        Assert.Contains("could not start", result.Error);
        Assert.Single(runner.Calls);
        Assert.Single(await _db.AiCallLog.ToListAsync());
    }

    [Fact]
    public async Task The_kill_switch_makes_no_call_and_writes_no_row()
    {
        var runner = new FakeRunner();

        var result = await Client(runner, enabled: false).CallAsync(Request());

        Assert.Equal(AgentCallStatus.Disabled, result.Status);
        Assert.Empty(runner.Calls);
        Assert.Empty(await _db.AiCallLog.ToListAsync());
    }

    [Fact]
    public async Task A_kill_switch_pulled_at_runtime_makes_no_call_either()
    {
        var runner = new FakeRunner();
        var options = Options.Create(new AgentOptions { Enabled = true, RuntimeDirectory = Path.Combine(_dir, "runtime"), TimeoutSeconds = 60 });
        await using var tool = new PricingToolDbContext(new DbContextOptionsBuilder<PricingToolDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var killSwitch = new KillSwitch(tool, options);
        await killSwitch.SetAsync(stop: true, "manager@test");

        var result = await new ClaudeCodeClient(options, runner, _db, NullLogger<ClaudeCodeClient>.Instance, killSwitch: killSwitch)
            .CallAsync(Request());

        Assert.Equal(AgentCallStatus.Disabled, result.Status);
        Assert.Contains("pulled by manager@test", result.Error);
        Assert.Empty(runner.Calls);
        Assert.Empty(await _db.AiCallLog.ToListAsync());
    }

    [Fact]
    public async Task Free_text_calls_return_the_answer_as_a_string()
    {
        var runner = new FakeRunner().Returns(Ok(Envelope(result: "Floors held this week.")));

        var result = await Client(runner).CallAsync(Request(AgentRole.Chat, schema: null));

        Assert.True(result.Succeeded);
        Assert.Equal("Floors held this week.", result.Output!.Value.GetString());
        Assert.DoesNotContain("--json-schema", runner.Calls.Single().Arguments);
    }

    [Fact]
    public async Task A_runtime_folder_under_a_claude_md_is_refused()
    {
        var repo = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "CLAUDE.md"), "repo instructions");
        var client = new ClaudeCodeClient(Options.Create(new AgentOptions
        {
            Enabled = true,
            RuntimeDirectory = Path.Combine(repo, "runtime"),
        }), new FakeRunner(), _db, NullLogger<ClaudeCodeClient>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CallAsync(Request()));
        Assert.Contains("not isolated", ex.Message);
    }

    [Fact]
    public async Task Email_addresses_in_the_answer_are_redacted_from_the_log()
    {
        var runner = new FakeRunner().Returns(Ok(Envelope(result: "Context: The user's email address is someone.x@example.com.")));

        var result = await Client(runner).CallAsync(Request(AgentRole.Chat, schema: null));

        Assert.True(result.Succeeded);
        var row = Assert.Single(await _db.AiCallLog.ToListAsync());
        Assert.Equal("Context: The user's email address is [email].", row.Output);
    }

    [Fact]
    public void The_envelope_parser_ignores_noise_before_the_result_object()
    {
        var parsed = ClaudeCodeEnvelope.TryParse("warning: something\n" + Envelope(result: "x"));

        Assert.NotNull(parsed);
        Assert.Equal("x", parsed!.Result);
        Assert.Null(ClaudeCodeEnvelope.TryParse("not json at all"));
        Assert.Null(ClaudeCodeEnvelope.TryParse("{\"type\":\"system\"}"));
    }
}
