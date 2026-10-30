using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Json.Schema;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PricingTool.Agent.Storage;

namespace PricingTool.Agent.ClaudeCode;

/// <summary>
/// The agents' only way to Claude: runs Claude Code headless (<c>claude -p --output-format json</c>)
/// through the team's subscription, isolated from this repo and from the user's own setup, and logs
/// every attempt to <c>ai.AiCallLog</c>.
///
/// Isolation, all of it applied on every call:
/// <list type="bullet">
/// <item>the process runs in a runtime folder outside the repo, with no CLAUDE.md in it or above it
///   (checked before every call);</item>
/// <item><c>--setting-sources ""</c>: no user, project or local settings, so no plugins, hooks or memory;</item>
/// <item><c>--system-prompt-file</c> replaces Claude Code's own system prompt with the agent's rules;</item>
/// <item><c>--tools ""</c>: no built-in tools (no shell, no file access); <c>--strict-mcp-config</c>: no MCP
///   server except ours; <c>--disable-slash-commands</c>: no skills;</item>
/// <item>the parent's Claude and Anthropic environment variables are dropped, so a call made from inside
///   another Claude Code session (or the desktop app) is not routed through it.</item>
/// </list>
/// <c>--bare</c> would skip the same things but reads only an API key, never the subscription login,
/// so it is not used.
/// </summary>
public sealed class ClaudeCodeClient
{
    /// <summary>The first try plus one retry; then the caller gets a typed failure.</summary>
    internal const int MaxAttempts = 2;

    private const int MaxLoggedOutputChars = 16_000;
    private const int MaxLoggedErrorChars = 2_000;

    /// <summary>Inherited variables dropped from the child's environment (case-insensitive prefixes).</summary>
    private static readonly string[] DroppedEnvironmentPrefixes = { "CLAUDE", "ANTHROPIC_", "MCP_" };

    /// <summary>Kept even though they match a dropped prefix: the server login token from
    /// <c>claude setup-token</c>, and where Git Bash lives on Windows.</summary>
    private static readonly HashSet<string> KeptEnvironmentVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLAUDE_CODE_OAUTH_TOKEN",
        "CLAUDE_CODE_GIT_BASH_PATH",
    };

    private readonly AgentOptions _options;
    private readonly IProcessRunner _runner;
    private readonly AgentDbContext _db;
    private readonly ILogger<ClaudeCodeClient> _logger;
    private readonly TimeProvider _clock;

    public ClaudeCodeClient(
        IOptions<AgentOptions> options, IProcessRunner runner, AgentDbContext db,
        ILogger<ClaudeCodeClient> logger, TimeProvider? clock = null)
    {
        _options = options.Value;
        _runner = runner;
        _db = db;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<AgentCallResult> CallAsync(AgentCallRequest request, CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return new AgentCallResult(AgentCallStatus.Disabled, null, "Agent:Enabled is off (kill switch); no call made.", Array.Empty<long>());

        if (!File.Exists(request.SystemPromptFile))
            throw new FileNotFoundException("The agent's system prompt file is missing.", request.SystemPromptFile);
        var schema = request.JsonSchema is null ? null : JsonSchema.FromText(request.JsonSchema);
        var spec = BuildProcessSpec(request, PrepareRuntimeDirectory());

        var logIds = new List<long>();
        Outcome outcome = default;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ProcessResult? process = null;
            try
            {
                process = await _runner.RunAsync(spec, ct);
                outcome = Interpret(process, schema);
            }
            catch (Win32Exception ex)
            {
                outcome = new Outcome(AgentCallStatus.CliError, null, null,
                    $"Claude Code could not start ('{_options.ClaudeExecutable}'): {ex.Message}", null, Retry: false);
            }

            logIds.Add(await LogAsync(request, attempt, process, outcome, ct));
            if (outcome.Status == AgentCallStatus.Succeeded)
                return new AgentCallResult(AgentCallStatus.Succeeded, outcome.Output, null, logIds);

            _logger.LogWarning("{Agent} call '{Purpose}' attempt {Attempt}/{Max} failed: {Status} {Error}",
                request.Role, request.Purpose, attempt, MaxAttempts, outcome.Status, outcome.Error);
            if (!outcome.Retry) break;
        }
        return new AgentCallResult(outcome.Status, null, outcome.Error, logIds);
    }

    /// <summary>The exact command line, environment and stdin for one call. Internal for tests.</summary>
    internal ProcessSpec BuildProcessSpec(AgentCallRequest request, string runtimeDirectory)
    {
        var args = new List<string>
        {
            "-p",
            "--output-format", "json",
            "--model", _options.ModelFor(request.Role),
            "--system-prompt-file", Path.GetFullPath(request.SystemPromptFile),
            "--setting-sources", "",
            "--tools", "",
            "--strict-mcp-config",
            "--disable-slash-commands",
            "--no-session-persistence",
            "--max-budget-usd", (request.MaxBudgetUsd ?? _options.MaxBudgetUsdPerCall).ToString(CultureInfo.InvariantCulture),
        };
        if (request.McpConfigFile is not null)
        {
            args.Add("--mcp-config");
            args.Add(Path.GetFullPath(request.McpConfigFile));
        }
        if (request.AllowedTools.Count > 0)
        {
            args.Add("--allowedTools");
            args.Add(string.Join(",", request.AllowedTools));
        }
        if (request.JsonSchema is not null)
        {
            args.Add("--json-schema");
            args.Add(request.JsonSchema);
        }

        return new ProcessSpec(_options.ClaudeExecutable, args, runtimeDirectory, request.Prompt,
            ChildEnvironment(Environment.GetEnvironmentVariables()), TimeSpan.FromSeconds(_options.TimeoutSeconds));
    }

    /// <summary>The parent's environment minus its Claude / Anthropic / MCP variables (see the class
    /// summary), plus the switch that keeps the account's claude.ai connectors out.</summary>
    internal static IReadOnlyDictionary<string, string> ChildEnvironment(System.Collections.IDictionary parent)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in parent)
        {
            var key = (string)e.Key;
            var drop = DroppedEnvironmentPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                && !KeptEnvironmentVariables.Contains(key);
            if (!drop && e.Value is string value) env[key] = value;
        }
        env["ENABLE_CLAUDEAI_MCP_SERVERS"] = "false";
        return env;
    }

    /// <summary>Creates the runtime folder and refuses one that has a CLAUDE.md in it or in any folder
    /// above it: Claude Code would load that file into every agent call.</summary>
    internal string PrepareRuntimeDirectory()
    {
        var dir = Path.GetFullPath(_options.ResolveRuntimeDirectory());
        Directory.CreateDirectory(dir);
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            foreach (var name in new[] { "CLAUDE.md", "CLAUDE.local.md" })
            {
                var path = Path.Combine(d.FullName, name);
                if (File.Exists(path))
                    throw new InvalidOperationException(
                        $"Agent runtime folder '{dir}' is not isolated: '{path}' would be loaded into every agent call. " +
                        "Set Agent:RuntimeDirectory to a folder outside the repo.");
            }
        }
        return dir;
    }

    private readonly record struct Outcome(
        AgentCallStatus Status, JsonElement? Output, ClaudeCodeEnvelope? Envelope, string? Error, string? RawOutput, bool Retry = true);

    private Outcome Interpret(ProcessResult process, JsonSchema? schema)
    {
        if (process.TimedOut)
            return new Outcome(AgentCallStatus.TimedOut, null, null,
                $"No answer within {_options.TimeoutSeconds} s; the process was killed.", null);

        var envelope = ClaudeCodeEnvelope.TryParse(process.StandardOutput);
        if (envelope is null)
            return new Outcome(AgentCallStatus.CliError, null, null,
                $"Exit code {process.ExitCode}, no result object. {FirstLine(process.StandardError)} {FirstLine(process.StandardOutput)}".Trim(), null);
        if (IsNotLoggedIn(envelope))
            return new Outcome(AgentCallStatus.CliError, null, envelope,
                "Claude Code is not logged in on this machine: run `claude auth login` once (or set CLAUDE_CODE_OAUTH_TOKEN " +
                "from `claude setup-token` on a server). No request was made.", envelope.Result, Retry: false);
        if (envelope.IsError || envelope.Subtype != "success")
            return new Outcome(AgentCallStatus.CliError, null, envelope,
                $"Claude Code returned an error ({envelope.Subtype ?? "no subtype"}): {envelope.Result}".Trim(), envelope.Result);

        if (schema is null)
            return new Outcome(AgentCallStatus.Succeeded, JsonSerializer.SerializeToElement(envelope.Result ?? ""), envelope, null, envelope.Result);

        var answer = envelope.StructuredOutput ?? ParseJsonAnswer(envelope.Result);
        var raw = answer?.GetRawText() ?? envelope.Result;
        if (answer is null)
            return new Outcome(AgentCallStatus.InvalidOutput, null, envelope, "The answer is not JSON.", raw);

        var errors = SchemaErrors(schema, answer.Value);
        return errors.Count == 0
            ? new Outcome(AgentCallStatus.Succeeded, answer, envelope, null, raw)
            : new Outcome(AgentCallStatus.InvalidOutput, null, envelope, "Schema check failed: " + string.Join("; ", errors), raw);
    }

    /// <summary>Claude Code answers "Not logged in · Please run /login" without calling the API; retrying
    /// can't fix that, so it is reported as is.</summary>
    private static bool IsNotLoggedIn(ClaudeCodeEnvelope envelope) =>
        envelope.IsError && envelope.DurationApiMs is null or 0
        && envelope.Result?.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The answer text as JSON, allowing a ```json fence around it.</summary>
    internal static JsonElement? ParseJsonAnswer(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (t.StartsWith("```"))
        {
            var firstNewline = t.IndexOf('\n');
            var closing = t.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline < 0 || closing <= firstNewline) return null;
            t = t[(firstNewline + 1)..closing].Trim();
        }
        try
        {
            using var doc = JsonDocument.Parse(t);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<string> SchemaErrors(JsonSchema schema, JsonElement answer)
    {
        var results = schema.Evaluate(answer, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid) return Array.Empty<string>();
        var errors = (results.Details ?? new List<EvaluationResults>())
            .Prepend(results)
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(err => $"{d.InstanceLocation}: {err.Value}"))
            .Distinct()
            .ToList();
        return errors.Count > 0 ? errors : new[] { "the answer does not match the schema" };
    }

    private async Task<long> LogAsync(AgentCallRequest request, int attempt, ProcessResult? process, Outcome outcome, CancellationToken ct)
    {
        var env = outcome.Envelope;
        var entry = new AiCallLogEntry
        {
            CalledAtUtc = _clock.GetUtcNow().UtcDateTime - (process?.Elapsed ?? TimeSpan.Zero),
            Agent = request.Role.ToString(),
            Purpose = Truncate(request.Purpose, 200)!,
            Attempt = attempt,
            Model = _options.ModelFor(request.Role),
            ModelIds = env is { ModelIds.Count: > 0 } ? Truncate(string.Join(",", env.ModelIds), 200) : null,
            InputTokens = env?.InputTokens ?? 0,
            OutputTokens = env?.OutputTokens ?? 0,
            CacheReadTokens = env?.CacheReadTokens ?? 0,
            CacheCreationTokens = env?.CacheCreationTokens ?? 0,
            LatencyMs = (int)(process?.Elapsed.TotalMilliseconds ?? 0),
            DurationMs = env?.DurationMs,
            DurationApiMs = env?.DurationApiMs,
            NumTurns = env?.NumTurns,
            CostUsd = env?.TotalCostUsd,
            Success = outcome.Status == AgentCallStatus.Succeeded,
            Status = outcome.Status.ToString(),
            Error = Truncate(outcome.Error, MaxLoggedErrorChars),
            PromptChars = request.Prompt.Length,
            Output = Truncate(outcome.RawOutput, MaxLoggedOutputChars),
        };
        _db.AiCallLog.Add(entry);
        // The log row must survive the caller's cancellation: the call has already been made and paid for.
        await _db.SaveChangesAsync(CancellationToken.None);
        return entry.Id;
    }

    private static string FirstLine(string s)
    {
        var t = s.Trim();
        var i = t.IndexOf('\n');
        return Truncate(i < 0 ? t : t[..i].Trim(), 500) ?? "";
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
