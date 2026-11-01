using System.Globalization;
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
/// On GjirafaMall the rows are its own products only: a product also listed on Gjirafa50 takes Gjirafa50's price,
/// so a Mall floor or target never moves it (the user's point, 1 Nov); what was left out is said above the table.
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
        var mall = layer.Brand == "GjirafaMall";
        var bands = await _reader.GetBandConfigAsync(layerId, ct);
        var sales = await _reader.GetSalesAsync(layerId, window, lastYear, _options.FloorTolerancePp, _options.AtFloorBandPp, ownOnly: mall, ct: ct);
        var run = await _reader.GetLatestRunAsync(layerId, ct);
        var stock = run is null ? new List<StockAggregate>() : await _reader.GetStockAsync(layerId, run, ownOnly: mall, ct: ct);
        var costs = await _reader.GetCostChangesAsync(layerId, ownOnly: mall, ct: ct);
        var campaigns = (await _reader.GetCampaignWindowsAsync(layerId, lastYear.From, lastYear.To, ct))
            .Concat(await _reader.GetCampaignWindowsAsync(layerId, window.From, window.To, ct))
            .ToList();

        var card = AlphaScorecard.Build(layer, window, lastYear, _options, bands, sales, stock, costs, campaigns, run);
        // The margin-target rule's basis (CheckedExport): units sold on normal days in the six weeks before the last two (late cancellations).
        var (from, to) = (today.AddDays(-42), today.AddDays(-14));
        card = card with { ShopMargin = run is null ? null : ShopMarginEstimate.Of(run.RunId, from, to, await _reader.GetShopMarginAsync(layerId, run.RunId, from, to, ct)) };
        return mall ? card with { ScopeNote = OwnOnlyNote(await _reader.GetNormalDaySplitAsync(layerId, window, ct)) } : card;
    }

    /// <summary>The Mall scope line: the rows are its own products; the Gjirafa50-listed part of the window, and the whole layer.</summary>
    internal static string OwnOnlyNote(IReadOnlyList<(bool Gj50Listed, decimal Units, decimal Net, decimal Gp)> split)
    {
        var listed = split.Where(s => s.Gj50Listed).ToList();
        decimal net = split.Sum(s => s.Net), gp = split.Sum(s => s.Gp), lNet = listed.Sum(s => s.Net), lGp = listed.Sum(s => s.Gp);
        string Pct(decimal part, decimal whole) => whole > 0 ? (100m * part / whole).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "-";
        return "the rows are GjirafaMall's own products only. This layer's engine run prices only them: a product also listed on Gjirafa50 " +
               "takes Gjirafa50's price, so a Mall floor or target never moves it, and its floor is Gjirafa50's. Left out of the rows (window, normal days): " +
               $"Gjirafa50-listed products, net {lNet.ToString("N0", CultureInfo.InvariantCulture)} ({Pct(lNet, net)} of the layer's revenue) at {Pct(lGp, lNet)} margin; " +
               $"the whole layer with them: net {net.ToString("N0", CultureInfo.InvariantCulture)} at {Pct(gp, net)} margin.";
    }
}
