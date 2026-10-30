using Microsoft.Extensions.Options;
using PricingTool.Agent;
using PricingTool.Agent.Operations;

namespace PricingTool.Engine;

/// <summary>
/// The laptop's host mode (<c>--hackathon-host</c>, prompt 05). Runs the daily cycle as soon as the host starts, once
/// the daily start time has passed (a start before it waits for it; a second start the same day only resumes what
/// isn't done), then every 15 minutes checks whether the next
/// day's cycle is due (from <see cref="CycleOptions.DailyStartLocal"/>) or a failed step of today's cycle may be
/// tried again (after <see cref="CycleOptions.RetryFailedAfterMinutes"/>; the cycle caps it at three attempts a
/// day). The cost watcher is meant to share this 15-minute loop; it is not built yet, and the cycle logs it as
/// NotBuilt.
/// </summary>
public class DailyCycleWorker : BackgroundService
{
    internal static readonly TimeSpan Tick = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CycleOptions _options;
    private readonly ILogger<DailyCycleWorker> _logger;
    private readonly TimeProvider _clock;

    public DailyCycleWorker(IServiceScopeFactory scopeFactory, IOptions<AgentOptions> options, ILogger<DailyCycleWorker> logger, TimeProvider? clock = null)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value.Cycle;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateOnly? lastDay = null;
        var lastRun = DateTimeOffset.MinValue;
        var retryable = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _clock.GetLocalNow();
            if (IsDue(lastDay, retryable, lastRun, now, _options))
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var cycle = scope.ServiceProvider.GetRequiredService<DailyCycle>();
                    var day = cycle.Today();
                    _logger.LogInformation("Daily cycle {Day}: starting.", day);
                    var steps = await cycle.RunAsync(day, stoppingToken);
                    lastDay = day;
                    retryable = DailyCycle.HasRetryableFailure(steps);
                    _logger.LogInformation("Daily cycle {Day}: {Summary}.", day,
                        string.Join(", ", steps.GroupBy(s => s.Status).Select(g => $"{g.Count()} {g.Key}")));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    // The cycle's bookkeeping itself failed (e.g. the database is down): try again on the retry interval.
                    lastDay ??= DateOnly.FromDateTime(now.DateTime);
                    retryable = true;
                    _logger.LogError(ex, "Daily cycle could not run; trying again in {Minutes} minutes.", _options.RetryFailedAfterMinutes);
                }
                lastRun = _clock.GetLocalNow();
            }

            try { await Task.Delay(Tick, _clock, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Whether to run the cycle now: at the host's start or on a new day, once the daily start time has passed
    /// (a restart at 00:35 on 31 Oct ran that day's cycle six hours early); or today again when a failed step has
    /// attempts left and the retry interval has passed.</summary>
    internal static bool IsDue(DateOnly? lastDay, bool retryableFailure, DateTimeOffset lastRun, DateTimeOffset nowLocal, CycleOptions options)
    {
        if (lastDay is null) return TimeOnly.FromDateTime(nowLocal.DateTime) >= options.DailyStart();
        if (DateOnly.FromDateTime(nowLocal.DateTime) != lastDay)
            return TimeOnly.FromDateTime(nowLocal.DateTime) >= options.DailyStart();
        return retryableFailure && nowLocal - lastRun >= TimeSpan.FromMinutes(options.RetryFailedAfterMinutes);
    }
}
