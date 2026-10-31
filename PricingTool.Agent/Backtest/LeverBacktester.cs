using System.Text.Json;
using PricingTool.Agent.Tools;

namespace PricingTool.Agent.Backtest;

/// <summary>
/// Runs <see cref="LeverBacktest"/> on a layer's booked sales lines: the hand-set floors as they stood each day, rebuilt from
/// the band edits in the audit log (the floor before an edit is its old value, after it the new one; today's floor where
/// a band was never edited), and the pricing agent's per-category floors as they stand now. Lever 5 uses the gaps between each
/// run finishing and its approval; lever 6 judges one approval's raises per category, season-adjusted with the same windows a
/// year earlier, and rolls them back over the following month where they didn't pay.
/// </summary>
public sealed class LeverBacktester
{
    private readonly PricingDataReader _reader;
    public LeverBacktester(PricingDataReader reader) => _reader = reader;

    /// <summary>Lever 6's experiment: an approval that went live by hand, judged over <see cref="LearningDays"/> days after it
    /// against the same before, and rolled back from <paramref name="ScoreFrom"/> to <paramref name="ScoreTo"/> in the categories
    /// whose season-adjusted ratio is under <paramref name="RollBackBelow"/>: 1 is the plan's plain rule ("didn't gain"), the
    /// outcome tracker as built needs a drop of its DropPct (0.80).</summary>
    public sealed record LearningExperiment(int ApprovalId, DateOnly LiveOn, DateOnly ScoreFrom, DateOnly ScoreTo, decimal RollBackBelow = 0.80m);

    public const int LearningDays = 28;

    /// <summary>Per category: GP per day after vs before the approval, divided by the same ratio a year earlier (null where
    /// either year's before is empty); a category under 1 didn't gain.</summary>
    public async Task<Dictionary<string, decimal?>> LearningVerdictsAsync(int layerId, DateOnly liveOn, CancellationToken ct = default)
    {
        async Task<Dictionary<string, decimal>> Gp(DateOnly from, DateOnly to) =>
            (await _reader.GetCategoryNormalDayGpAsync(layerId, from, to, ct)).ToDictionary(x => x.Category, x => x.Gp);
        var windows = await _reader.GetCampaignWindowsAsync(layerId, new DateOnly(2000, 1, 1), liveOn.AddDays(LearningDays + 1), ct);
        int Normal(DateOnly from, DateOnly to) => Enumerable.Range(0, to.DayNumber - from.DayNumber).Select(from.AddDays)
            .Count(d => !windows.Any(w => d >= w.StartDate && d <= w.EndDate));
        var (b0, a0) = (liveOn.AddDays(-LearningDays), liveOn.AddDays(LearningDays));
        var (b1, l1, a1) = (b0.AddDays(-364), liveOn.AddDays(-364), a0.AddDays(-364));
        var (before, after, beforeLy, afterLy) = (await Gp(b0, liveOn), await Gp(liveOn, a0), await Gp(b1, l1), await Gp(l1, a1));
        var (nb, na, nbl, nal) = (Normal(b0, liveOn), Normal(liveOn, a0), Normal(b1, l1), Normal(l1, a1));
        return before.Keys.Union(after.Keys).ToDictionary(c => c, c =>
        {
            decimal Per(Dictionary<string, decimal> d, string k, int days) => days > 0 && d.TryGetValue(k, out var v) ? v / days : 0m;
            var (pb, pa, pbl, pal) = (Per(before, c, nb), Per(after, c, na), Per(beforeLy, c, nbl), Per(afterLy, c, nal));
            return pb > 0 && pbl > 0 && pal > 0 ? Math.Round(pa / pb / (pal / pbl), 3) : (decimal?)null;
        });
    }

    public async Task<LeverBacktestResult> RunAsync(int layerId, DateOnly from, DateOnly to, ICollection<(int Lever, BacktestLine Line, decimal Gain)>? trace = null,
        LearningExperiment? learning = null, CancellationToken ct = default)
    {
        var pending = (await _reader.GetPendingPriceChangesAsync(layerId, ct)).ToDictionary(p => (p.Sku, p.Day), p => (p.Old, p.New));
        Rollback? rollback = null;
        if (learning is not null)
        {
            var verdicts = await LearningVerdictsAsync(layerId, learning.LiveOn, ct);
            var losing = verdicts.Where(v => v.Value is { } x && x < learning.RollBackBelow).Select(v => v.Key).ToHashSet(StringComparer.Ordinal);
            var raises = (await _reader.GetApprovalRaisesAsync(learning.ApprovalId, ct)).ToDictionary(r => r.Sku, r => (r.Old, r.New));
            rollback = (sku, category, day) => day >= learning.ScoreFrom && day < learning.ScoreTo && losing.Contains(category)
                                               && raises.TryGetValue(sku, out var r) ? r : null;
        }
        var lines = await _reader.GetBacktestLinesAsync(layerId, from.AddDays(-LeverBacktest.CeilingDays), to, ct);
        var hand = await HandFloorsAsync(layerId, ct);
        var rules = (await _reader.GetCategoryRulesAsync(layerId, ct)).Where(r => r.MarginFloorPct is not null)
            .ToDictionary(r => (r.BandId, r.Category), r => r.MarginFloorPct!.Value);

        return LeverBacktest.Run(layerId, lines, from, to, hand, (band, category) => rules.TryGetValue((band, category), out var f) ? f : null, trace,
            (sku, day) => pending.TryGetValue((sku, day), out var p) ? p : null, rollback);
    }

    /// <summary>E21 on a layer: GP forgone below the hand-set floor of each day, per month (<see cref="LeverBacktest.BelowFloor"/>).</summary>
    public async Task<IReadOnlyList<BelowFloorMonth>> BelowFloorAsync(int layerId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        LeverBacktest.BelowFloor(await _reader.GetBacktestLinesAsync(layerId, from, to, ct), from, to, await HandFloorsAsync(layerId, ct));

    /// <summary>The hand-set floor of a band on a day, rebuilt from the band edits in the audit log.</summary>
    private async Task<Func<int, DateOnly, decimal?>> HandFloorsAsync(int layerId, CancellationToken ct)
    {
        var edits = (await _reader.GetBandFloorEditsAsync(layerId, ct))
            .Select(e => (e.Utc, e.BandId, Old: Floor(e.OldJson), New: Floor(e.NewJson))).Where(e => e.New is not null).OrderBy(e => e.Utc).ToList();
        var today = (await _reader.GetBandConfigAsync(layerId, ct)).ToDictionary(b => b.BandId, b => b.MarginFloorPct);
        return (band, day) =>
        {
            var dayStart = day.ToDateTime(TimeOnly.MinValue);
            var last = edits.LastOrDefault(e => e.BandId == band && e.Utc < dayStart);
            if (last.New is not null) return last.New;
            var next = edits.FirstOrDefault(e => e.BandId == band && e.Utc >= dayStart);
            return next.Old ?? (today.TryGetValue(band, out var f) ? f : null);
        };
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
