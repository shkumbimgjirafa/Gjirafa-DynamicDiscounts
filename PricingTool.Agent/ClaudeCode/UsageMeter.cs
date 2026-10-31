using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace PricingTool.Agent.ClaudeCode;

/// <summary>One AI task the solution ran: what it was, when, how many model requests and tokens, and whether it worked.</summary>
public sealed record UsageEvent(string Task, DateTime StartedUtc, DateTime EndedUtc, int AiRequests, long TokensIn, long TokensOut, bool Ok);

/// <summary>Counts the solution's own AI tasks for the organisers' AI Pioneer meter (the 10 % bar's steps 3 and 5).</summary>
public interface IUsageMeter
{
    /// <summary>Never throws and never waits: a meter that is down or not set up changes nothing in the app.</summary>
    void Record(UsageEvent e);
}

/// <summary>
/// The organisers' meter ("hacktrack", installed 31 Oct at Mergca's request): each event goes to their <c>hook.js</c>
/// (<c>node hook.js send &lt;json&gt;</c>), which queues it on disk and sends it to the collector with the team token, retrying
/// later when offline. Configuration comes from the environment, falling back to the user's environment variables, so it
/// works however the engine host or the web app was started: <c>HACKTRACK_URL</c>, <c>HACKTRACK_TOKEN</c>,
/// <c>HACKTRACK_USER</c>, <c>HACKTRACK_SOLUTION</c>, and <c>HACKTRACK_HOOK</c> for hook.js (default
/// <c>%USERPROFILE%\.hacktrack\hook.js</c>). No token or no hook.js: nothing is sent. The token is never in the repo.
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
            var hook = Setting("HACKTRACK_HOOK") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hacktrack", "hook.js");
            if (string.IsNullOrEmpty(env["HACKTRACK_TOKEN"]) || !File.Exists(hook)) return;
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

    /// <summary>The event as hook.js takes it (its <c>track()</c> fields).</summary>
    public static string Json(UsageEvent e) => JsonSerializer.Serialize(new
    {
        kind = "run", task = e.Task, started_at = e.StartedUtc.ToString("o"), ended_at = e.EndedUtc.ToString("o"),
        ai_requests = e.AiRequests, tokens_in = e.TokensIn, tokens_out = e.TokensOut, ok = e.Ok,
    });

    /// <summary>A short task name from a call's purpose, ids and round numbers left out: "nova chat", "alpha campaign-plan brief".</summary>
    public static string TaskOf(AgentRole role, string purpose) =>
        $"{role.ToString().ToLowerInvariant()} {Regex.Replace(Regex.Replace(purpose, @"#?\S*\d\S*", " "), @"\s+", " ").Trim()}".Trim();

    private static string? Setting(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v
        : OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) : null;
}
