using Microsoft.Extensions.Options;
using PricingTool.Agent.Tools;

namespace PricingTool.Agent.Alpha;

/// <summary>Where a pass gets its scorecard (a seam for tests).</summary>
public interface IAlphaInputSource
{
    Task<AlphaScorecard> BuildAsync(int layerId, CancellationToken ct = default);
}

/// <summary>
/// Builds Alpha's scorecard for one layer (no AI): the last <see cref="ScorecardOptions.WindowDays"/> days
/// ending <see cref="ScorecardOptions.LagDays"/> days ago, against the same weeks of last year, per band ×
/// category. The SQL aggregates, <see cref="AlphaScorecard.Build"/> does the rest of the arithmetic.
/// </summary>
public sealed class AlphaInputBuilder : IAlphaInputSource
{
    private readonly PricingDataReader _reader;
    private readonly ScorecardOptions _options;
    private readonly TimeProvider _clock;

    public AlphaInputBuilder(PricingDataReader reader, IOptions<AgentOptions> options, TimeProvider? clock = null)
    {
        _reader = reader;
        _options = options.Value.Scorecard;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<AlphaScorecard> BuildAsync(int layerId, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
        var window = DateWindow.Ending(today, _options.WindowDays, _options.LagDays);
        var lastYear = window.SameWeeksLastYear();

        var layer = await _reader.GetLayerAsync(layerId, ct);
        var bands = await _reader.GetBandConfigAsync(layerId, ct);
        var sales = await _reader.GetSalesAsync(layerId, window, lastYear, _options.FloorTolerancePp, _options.AtFloorBandPp, ct: ct);
        var run = await _reader.GetLatestRunAsync(layerId, ct);
        var stock = run is null ? new List<StockAggregate>() : await _reader.GetStockAsync(layerId, run, ct: ct);
        var costs = await _reader.GetCostChangesAsync(layerId, ct: ct);
        var campaigns = (await _reader.GetCampaignWindowsAsync(layerId, lastYear.From, lastYear.To, ct))
            .Concat(await _reader.GetCampaignWindowsAsync(layerId, window.From, window.To, ct))
            .ToList();

        return AlphaScorecard.Build(layer, window, lastYear, _options, bands, sales, stock, costs, campaigns, run);
    }
}
