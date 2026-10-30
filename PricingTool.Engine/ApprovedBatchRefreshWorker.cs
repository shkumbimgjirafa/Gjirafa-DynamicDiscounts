using PricingTool.Data.Services;

namespace PricingTool.Engine;

/// <summary>
/// Hosted worker that refreshes every recently-pushed approved clearance batch from the live source once
/// an hour (and once on startup): units + net sold since push and current local stock, written onto the
/// batch so the dead-stock dashboard's "sold since push" and sold-out list stay current without an external
/// job. Read-mostly against the source; the only writes are to the tool's own ApprovedBatch* tables.
/// </summary>
public class ApprovedBatchRefreshWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ApprovedBatchRefreshWorker> _logger;

    public ApprovedBatchRefreshWorker(IServiceScopeFactory scopeFactory, ILogger<ApprovedBatchRefreshWorker> logger)
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
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<ApprovedBatchRefreshService>();
                var count = await service.RefreshTrackedBatchesAsync(stoppingToken);
                if (count > 0)
                    _logger.LogInformation("Approved-batch refresh: {Count} batch(es) refreshed.", count);

                // Same tick: refresh each recent approval's before/after profit windows (the per-approval
                // "did the bet pay off?" figures) from the sales report.
                var approvalProfit = scope.ServiceProvider.GetRequiredService<ApprovalProfitRefreshService>();
                var approvalCount = await approvalProfit.RefreshTrackedApprovalsAsync(stoppingToken);
                if (approvalCount > 0)
                    _logger.LogInformation("Approval profit refresh: {Count} approval(s) refreshed.", approvalCount);

                // Same tick, AFTER the sales refresh so figures are fresh: seal any dead-stock clearance
                // window whose successor batch has been approved — one frozen history row per closed window.
                var windows = scope.ServiceProvider.GetRequiredService<DeadStockWindowService>();
                var sealedCount = await windows.SealSupersededWindowsAsync(stoppingToken);
                if (sealedCount > 0)
                    _logger.LogInformation("Dead-stock window seal: {Count} window(s) sealed.", sealedCount);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Approved-batch refresh tick failed; will retry next hour.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
