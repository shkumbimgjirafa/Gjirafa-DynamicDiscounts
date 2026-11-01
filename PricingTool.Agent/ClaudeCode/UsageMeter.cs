using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace PricingTool.Agent.ClaudeCode;

/// <summary>One AI task the solution ran: what it was, when, how many model requests and tokens, and whether it worked.
/// A core task's run (<see cref="UsageRun"/>) also carries the meter's v3 outcome: the units it handled, the data it read,
/// and what it worked on (<see cref="Input"/>, which hook.js hashes on this machine; the input itself is never sent).</summary>
public sealed record UsageEvent(string Task, DateTime StartedUtc, DateTime EndedUtc, int AiRequests, long TokensIn, long TokensOut, bool Ok)
{
    public int? Units { get; init; }
    public UsageSource? Source { get; init; }
    public object? Input { get; init; }
}

/// <summary>The data a run read, as the meter takes it. <see cref="System"/> is "warehouse", "shop-sql" or "other";
/// <see cref="RowsRead"/> counts the rows the task worked through (scorecard rows, changed prices, candidate SKUs), not the
/// sales lines behind them.</summary>
public sealed record UsageSource(string System, string Name, IReadOnlyList<string> Tables, long RowsRead);

/// <summary>The core tasks: the exact names on the team's task cards (one card each), so the meter's runs match them.
/// One unit is one run (the user's choice, 1 Nov): a layer's review, an engine run's check or push, a campaign.</summary>
public static class CoreTasks
{
    public const string FloorTargetReview = "alpha floor and target review";
    public const string RunCheck = "run check before publish";
    public const string CreateAndPushDiscounts = "create and push discounts";
    public const string CampaignPlanning = "nova campaign planning";

    /// <summary>Where every core task's data comes from: the shop's NOP replica, read-only, copied into the app's database.</summary>
    public const string ShopSql = "shop-sql";
    public const string ShopCopy = "NOP replica, copied to LocalDB PricingToolHackathon";
}

/// <summary>
/// One run of a core task (hook.js's v3 <c>run()</c>, for a .NET app). The AI calls made while it is open count toward it
/// instead of being sent on their own (<see cref="ClaudeCodeClient"/> asks <see cref="Current"/>), and disposing it records
/// one event with the units it handled and the data it read. <see cref="Ok"/> stays false until the task says it finished,
/// so a run that fails or throws is recorded as not ok. With no meter (a test pass) nothing is recorded.
/// </summary>
public sealed class UsageRun : IDisposable
{
    private static readonly AsyncLocal<UsageRun?> Open = new();
    private readonly IUsageMeter? _meter;
    private readonly TimeProvider _clock;
    private readonly UsageRun? _outer;
    private int _requests, _closed;
    private long _tokensIn, _tokensOut;

    private UsageRun(IUsageMeter? meter, string task, object input, TimeProvider clock, DateTime? startedUtc)
    {
        _meter = meter;
        _clock = clock;
        Task = task;
        Input = input;
        StartedUtc = startedUtc ?? clock.GetUtcNow().UtcDateTime;
        _outer = Open.Value;
        Open.Value = this;
    }

    /// <param name="input">What the run worked on (ids only); hashed by hook.js, never sent.</param>
    public static UsageRun Start(IUsageMeter? meter, string task, object input, TimeProvider? clock = null, DateTime? startedUtc = null) =>
        new(meter, task, input, clock ?? TimeProvider.System, startedUtc);

    /// <summary>The run open in this flow, if any.</summary>
    public static UsageRun? Current => Open.Value;

    public string Task { get; }
    public object Input { get; }
    public DateTime StartedUtc { get; }
    public int Units { get; set; }
    public UsageSource? Source { get; set; }
    public bool Ok { get; set; }

    /// <summary>One model call made inside the run.</summary>
    public void AddCall(int requests, long tokensIn, long tokensOut)
    {
        Interlocked.Add(ref _requests, requests);
        Interlocked.Add(ref _tokensIn, tokensIn);
        Interlocked.Add(ref _tokensOut, tokensOut);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        if (Open.Value == this) Open.Value = _outer;
        _meter?.Record(new UsageEvent(Task, StartedUtc, _clock.GetUtcNow().UtcDateTime, _requests, _tokensIn, _tokensOut, Ok)
        {
            Units = Units, Source = Source, Input = Input,
        });
    }
}

/// <summary>Counts the solution's own AI tasks for the organisers' AI Pioneer meter (the 10 % bar's steps 3 and 5).</summary>
public interface IUsageMeter
{
    /// <summary>Never throws and never waits: a meter that is down or not set up changes nothing in the app.</summary>
    void Record(UsageEvent e);
}

/// <summary>
/// The organisers' meter ("hacktrack", installed 31 Oct at Mergca's request, v3 on 1 Nov): each event goes to their
/// <c>hook.js</c> (<c>node hook.js send &lt;json&gt;</c>), which queues it on disk and sends it to the collector with the team
/// token, retrying later when offline. Configuration comes from the environment, falling back to the user's environment
/// variables, so it works however the engine host or the web app was started: <c>HACKTRACK_URL</c>, <c>HACKTRACK_TOKEN</c>,
/// <c>HACKTRACK_USER</c>, <c>HACKTRACK_SOLUTION</c>, and <c>HACKTRACK_HOOK</c> for hook.js (default: the copy beside this
/// file, shipped in the build output as <c>ClaudeCode\hook.js</c>, then <c>%USERPROFILE%\.hacktrack\hook.js</c>).
/// <c>HACKTRACK_ECHO=1</c> makes hook.js print each event it queues. No token or no hook.js: nothing is sent. The token is
/// never in the repo.
/// </summary>
public sealed class HacktrackMeter : IUsageMeter
{
    private static readonly string[] Settings = { "HACKTRACK_URL", "HACKTRACK_TOKEN", "HACKTRACK_USER", "HACKTRACK_SOLUTION" };
    private readonly ILogger<HacktrackMeter> _logger;
    private int _warned;

    public HacktrackMeter(ILogger<HacktrackMeter> logger) => _logger = logger;

    public void Record(UsageEvent e)
    {
        try
        {
            var env = Settings.ToDictionary(k => k, Setting);
            var hook = Setting("HACKTRACK_HOOK") ?? new[]
            {
                Path.Combine(AppContext.BaseDirectory, "ClaudeCode", "hook.js"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hacktrack", "hook.js"),
            }.FirstOrDefault(File.Exists);
            if (string.IsNullOrEmpty(env["HACKTRACK_TOKEN"]) || hook is null || !File.Exists(hook)) return;
            var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(hook);
            start.ArgumentList.Add("send");
            start.ArgumentList.Add(Json(e));
            foreach (var (k, v) in env) if (v is not null) start.Environment[k] = v;
            using var p = Process.Start(start);                                // hook.js queues, sends and exits within seconds
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0) _logger.LogWarning(ex, "The AI Pioneer meter couldn't record an event; the app carries on.");
        }
    }

    /// <summary>The event as hook.js takes it (its <c>track()</c> fields; units, source and input only on a core task's run).</summary>
    public static string Json(UsageEvent e) => JsonSerializer.Serialize(new
    {
        kind = "run", task = e.Task, started_at = e.StartedUtc.ToString("o"), ended_at = e.EndedUtc.ToString("o"),
        ai_requests = e.AiRequests, tokens_in = e.TokensIn, tokens_out = e.TokensOut, ok = e.Ok,
        units = e.Units,
        source = e.Source is { } s ? new { system = s.System, name = s.Name, tables = s.Tables, rows_read = s.RowsRead } : null,
        input = e.Input,
    }, OmitNulls);

    private static readonly JsonSerializerOptions OmitNulls = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>A short task name from a call's purpose, ids and round numbers left out: "nova chat", "alpha campaign-plan brief".</summary>
    public static string TaskOf(AgentRole role, string purpose) =>
        $"{role.ToString().ToLowerInvariant()} {Regex.Replace(Regex.Replace(purpose, @"#?\S*\d\S*", " "), @"\s+", " ").Trim()}".Trim();

    private static string? Setting(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v
        : OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) : null;
}
