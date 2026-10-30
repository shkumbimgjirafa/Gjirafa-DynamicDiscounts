using System.Text.Json;
using PricingTool.Agent.Tools;

namespace PricingTool.Agent.Backtest;

/// <summary>
/// Runs <see cref="LeverBacktest"/> on a layer's booked sales lines: the hand-set floors as they stood each day, rebuilt from
/// the band edits in the audit log (the floor before an edit is its old value, after it the new one; today's floor where
/// a band was never edited), and the pricing agent's per-category floors as they stand now.
/// </summary>
public sealed class LeverBacktester
{
    private readonly PricingDataReader _reader;
    public LeverBacktester(PricingDataReader reader) => _reader = reader;

    public async Task<LeverBacktestResult> RunAsync(int layerId, DateOnly from, DateOnly to, ICollection<(int Lever, BacktestLine Line, decimal Gain)>? trace = null,
        CancellationToken ct = default)
    {
        var lines = await _reader.GetBacktestLinesAsync(layerId, from.AddDays(-LeverBacktest.CeilingDays), to, ct);
        var edits = (await _reader.GetBandFloorEditsAsync(layerId, ct))
            .Select(e => (e.Utc, e.BandId, Old: Floor(e.OldJson), New: Floor(e.NewJson))).Where(e => e.New is not null).OrderBy(e => e.Utc).ToList();
        var today = (await _reader.GetBandConfigAsync(layerId, ct)).ToDictionary(b => b.BandId, b => b.MarginFloorPct);
        var rules = (await _reader.GetCategoryRulesAsync(layerId, ct)).Where(r => r.MarginFloorPct is not null)
            .ToDictionary(r => (r.BandId, r.Category), r => r.MarginFloorPct!.Value);

        decimal? Hand(int band, DateOnly day)
        {
            var dayStart = day.ToDateTime(TimeOnly.MinValue);
            var last = edits.LastOrDefault(e => e.BandId == band && e.Utc < dayStart);
            if (last.New is not null) return last.New;
            var next = edits.FirstOrDefault(e => e.BandId == band && e.Utc >= dayStart);
            return next.Old ?? (today.TryGetValue(band, out var f) ? f : null);
        }

        return LeverBacktest.Run(layerId, lines, from, to, Hand, (band, category) => rules.TryGetValue((band, category), out var f) ? f : null, trace);
    }

    private static decimal? Floor(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith('{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("MarginFloorPct", out var f) && f.TryGetDecimal(out var v) ? v : null;
        }
        catch (JsonException) { return null; }
    }
}
