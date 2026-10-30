using Microsoft.Extensions.Options;
using PricingTool.NopIntegration.CostWatch;

namespace PricingTool.Engine;

/// <summary>
/// The engine host's fast lane (<c>--hackathon-host</c>, plan section 5 "CostWatcher"): every
/// <see cref="CostWatcherOptions.IntervalMinutes"/> it polls the replica for refreshed costs (when enabled) and lets
/// the cost watcher act on what moved. Independent of the daily cycle's loop, so a long cycle never delays it.
/// </summary>
public class CostWatcherWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CostWatcherOptions _options;
    private readonly ILogger<CostWatcherWorker> _logger;

    public CostWatcherWorker(IServiceScopeFactory scopeFactory, IOptions<CostWatcherOptions> options, ILogger<CostWatcherWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.IntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                if (_options.PollReplica)
                    await scope.ServiceProvider.GetRequiredService<ReplicaCostFeed>().PollAsync(stoppingToken);
                var r = await scope.ServiceProvider.GetRequiredService<CostWatcher>().WatchAsync(stoppingToken);
                if (r.Observed > 0 || r.Skipped is not null)
                    _logger.LogInformation("Cost watch: {Observed} observations, {Raised} raised, {Held} held, {Ended} ended {Skipped}",
                        r.Observed, r.Raised, r.Held, r.Ended, r.Skipped);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cost watch failed; trying again in {Minutes} minutes.", interval.TotalMinutes);
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
