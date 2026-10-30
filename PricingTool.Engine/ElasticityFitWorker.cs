using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PricingTool.Core.Options;
using PricingTool.Data;
using PricingTool.Data.Services;

namespace PricingTool.Engine;

/// <summary>
/// Hosted worker that refreshes per-(layer, SKU) price elasticities every
/// <see cref="PricingEngineOptions.ElasticityFitCadenceDays"/> days (default 15). Ticks hourly; a layer
/// is due when it has never been fitted or its last fit is older than that cadence. Layers run
/// sequentially to bound load on the read-only source DB. It writes only SkuElasticity, so it does
/// not contend with the pricing run's single-flight guard — a slightly stale coefficient is exactly the
/// intent: the fitted coefficient is the elastic lane's "opinion", and holding it between fits is what
/// freezes the elastic vote across the daily runs inside one 15-day slow-lane window.
/// </summary>
public class ElasticityFitWorker : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ElasticityFitWorker> _logger;
    private readonly TimeSpan _cadence;

    public ElasticityFitWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<PricingEngineOptions> options,
        ILogger<ElasticityFitWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _cadence = TimeSpan.FromDays(Math.Max(1, options.Value.ElasticityFitCadenceDays));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _logger.LogError(ex, "Elasticity fit tick failed; will retry next hour."); }

            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var dueBefore = DateTime.UtcNow - _cadence;

        List<int> layerIds;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PricingToolDbContext>();
            layerIds = await db.Layers.AsNoTracking()
                .Where(l => l.IsActive && (l.LastElasticityFitUtc == null || l.LastElasticityFitUtc < dueBefore))
                .OrderBy(l => l.SortOrder)
                .Select(l => l.Id)
                .ToListAsync(ct);
        }

        foreach (var layerId in layerIds)
        {
            if (ct.IsCancellationRequested) return;
            using var scope = _scopeFactory.CreateScope();
            var fit = scope.ServiceProvider.GetRequiredService<ElasticityFitService>();
            try
            {
                await fit.FitLayerAsync(layerId, ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Elasticity fit for layer {Layer} failed.", layerId);
            }
        }
    }
}
