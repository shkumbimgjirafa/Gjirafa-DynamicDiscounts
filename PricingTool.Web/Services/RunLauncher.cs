using PricingTool.Data.Services;

namespace PricingTool.Web.Services;

/// <summary>
/// Fires an on-demand pricing run in the background ("Run now"). One at a time per process;
/// the orchestrator's DB guard additionally protects against overlap with the Engine scheduler.
/// </summary>
public class RunLauncher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RunLauncher> _logger;
    private readonly object _gate = new();
    private int _running;
    private CancellationTokenSource? _cts;

    public RunLauncher(IServiceScopeFactory scopeFactory, ILogger<RunLauncher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>
    /// Starts an on-demand run for one layer in the background. Returns false when a run is already
    /// executing in this process (runs are serialized; the orchestrator also guards globally in DB).
    /// </summary>
    public bool TryStartRun(string triggeredBy, int layerId)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;

        var cts = new CancellationTokenSource();
        lock (_gate) _cts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<PricingRunOrchestrator>();
                await orchestrator.ExecuteRunAsync(triggeredBy, isOnDemand: true, layerId, cts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("On-demand pricing run was stopped by request.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "On-demand pricing run failed.");
            }
            finally
            {
                lock (_gate) { if (_cts == cts) _cts = null; }
                cts.Dispose();
                Volatile.Write(ref _running, 0);
            }
        });
        return true;
    }

    /// <summary>
    /// Requests cooperative cancellation of the on-demand run executing in THIS process. The run stops
    /// at the next SKU boundary and lands as Failed. Returns false when this process isn't running one
    /// (e.g. a scheduled run in the Engine process, which can't be cancelled from here — its DB row must
    /// be force-failed instead to release the global run guard).
    /// </summary>
    public bool RequestStop()
    {
        lock (_gate)
        {
            if (_cts is null) return false;
            _cts.Cancel();
            return true;
        }
    }
}
