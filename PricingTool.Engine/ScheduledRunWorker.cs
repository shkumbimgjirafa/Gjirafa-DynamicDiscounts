using Microsoft.EntityFrameworkCore;
using PricingTool.Data;
using PricingTool.Data.Services;

namespace PricingTool.Engine;

/// <summary>
/// Hosted scheduler. Once a minute it loads every ACTIVE layer and runs any whose scheduled slot
/// has come due since its last scheduled run (per-layer RunTimeUtc / CadenceHours). Runs are fired
/// sequentially — the orchestrator's global single-flight guard serializes them with each other and
/// with "Run now" runs from the Web app. Schedule edits in the UI take effect within a minute.
/// </summary>
public class ScheduledRunWorker : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScheduledRunWorker> _logger;

    public ScheduledRunWorker(IServiceScopeFactory scopeFactory, ILogger<ScheduledRunWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduler tick failed; will retry next minute.");
            }

            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        List<PricingTool.Data.Entities.Layer> layers;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PricingToolDbContext>();
            // Fire due layers in scheduled-time order (then SortOrder) so an earlier-scheduled layer runs
            // first when several fall due in the same tick — e.g. after a cold start spanning both slots,
            // Gjirafa50 (02:30) runs before its GjirafaMall sibling (03:00), so Mall can adopt today's Gj50
            // proposals (SYNCED_TO_GJ50) rather than yesterday's. RunTimeUtc is zero-padded "HH:mm", so a
            // string sort is chronological within the day.
            layers = await db.Layers.AsNoTracking()
                .Where(l => l.IsActive).OrderBy(l => l.RunTimeUtc).ThenBy(l => l.SortOrder).ToListAsync(ct);
        }

        foreach (var layer in layers)
        {
            if (ct.IsCancellationRequested) return;

            var info = ScheduleService.ToInfo(layer);
            // The slot this run serves, or null when a full cadence hasn't elapsed since the last served
            // slot (a never-run layer is due immediately). Anchoring on the last SLOT — the old prevSlot
            // math re-based on now.Date and fired ~daily for any cadence above 24h — honours the true
            // cadence, and stamping the slot (below) keeps a post-outage catch-up to exactly one run.
            var slot = ScheduleService.DueSlot(info, now);
            if (slot is null) continue;

            await RunLayerAsync(layer.Id, layer.DisplayName, slot.Value, ct);
        }
    }

    private async Task RunLayerAsync(int layerId, string layerName, DateTime slotUtc, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<PricingRunOrchestrator>();
        var schedule = scope.ServiceProvider.GetRequiredService<ScheduleService>();
        try
        {
            await orchestrator.ExecuteRunAsync("scheduler", isOnDemand: false, layerId, ct);
        }
        catch (RunAlreadyInProgressException ex)
        {
            // Another run is in progress (serialized execution). Do NOT stamp LastScheduledRunUtc —
            // leave this layer due so the next tick picks it up once the current run finishes. Catch this
            // type ONLY: a failed run can throw a plain InvalidOperationException too (SqlBulkCopy raises one
            // for a value its column can't hold), and treating that as "busy" re-ran a failing layer
            // back-to-back instead of consuming its slot.
            _logger.LogWarning("Scheduled run for {Layer} skipped: {Reason}", layerName, ex.Message);
            return;
        }
        catch (Exception ex)
        {
            // The run record already captured the failure; keep the scheduler alive and consume the
            // slot (a hard failure shouldn't loop every minute) by falling through to the stamp below.
            _logger.LogError(ex, "Scheduled pricing run for {Layer} failed.", layerName);
        }

        // Consume this SLOT (not wall-clock now) so LastScheduledRunUtc stays on the cadence grid: a
        // catch-up after an outage serves the current period once, without a second run later the same day.
        await schedule.SetLastScheduledRunAsync(layerId, slotUtc, CancellationToken.None);
    }
}
