using System.Globalization;
using System.Text;
using System.Text.Json;
using PricingTool.Agent.Campaigns;
using PricingTool.Agent.Storage;
using PricingTool.Agent.Tools;

namespace PricingTool.Agent.Backtest;

/// <summary>Lever 7 and the planner's calibration for one layer.</summary>
public sealed record CampaignLayerBacktest(int LayerId, List<CampaignReplayResult> Replays, CampaignCalibration? SeenType, CampaignCalibration? UnseenType,
    CampaignCalibration? InSample, List<CampaignDepthGain> DepthGains);

public sealed record CoverageGain(string Case, string Month, decimal Base, decimal Gain);

/// <summary>Every lever of one backtest run, as stored for the results page and its CSV.</summary>
public sealed record BacktestSnapshotData(DateOnly From, DateOnly To, List<LeverBacktestResult> Levers, List<CampaignLayerBacktest> Campaigns,
    List<CoverageMonth> CoverageMonths, List<CoverageEffect> CoverageEffects, List<CoverageGain> Coverage);

/// <summary>
/// Runs the whole backtest (levers 1–3, 5, 6 per layer, lever 7 and the calibration per layer, lever 4 on MK/AL) and stores it as
/// one <see cref="BacktestSnapshotRecord"/>, so the results page shows it at once and offers it as CSV. Estimated throughout,
/// except the measured inputs each section names.
/// </summary>
public sealed class BacktestSnapshotBuilder
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly PricingDataReader _reader;
    private readonly AgentDataTools _tools;
    private readonly AgentDbContext _db;

    public BacktestSnapshotBuilder(PricingDataReader reader, AgentDataTools tools, AgentDbContext db)
    {
        _reader = reader;
        _tools = tools;
        _db = db;
    }

    public async Task<BacktestSnapshotRecord> RunAsync(DateOnly from, DateOnly to, IReadOnlyCollection<int> layers, LeverBacktester.LearningExperiment? learning,
        IReadOnlyList<(string Case, decimal GainPerMonth, string Source)> ksEffects, string by, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        var levers = new List<LeverBacktestResult>();
        var backtester = new LeverBacktester(_reader);
        foreach (var layer in layers)
            levers.Add(await backtester.RunAsync(layer, from, to, null, learning is not null && layer == 1 ? learning : null, ct));
        var campaigns = new List<CampaignLayerBacktest>();
        foreach (var layer in layers)
        {
            var bt = await _tools.ReplayCampaignsAsync(layer, ct);
            campaigns.Add(new CampaignLayerBacktest(layer, bt.Replays, bt.SeenType, bt.UnseenType, bt.InSampleCalibration, bt.DepthGains));
        }
        var months = await _reader.GetCoverageMonthsAsync(CoverageBacktest.PushMinDiscounts, ct);
        var ksGp = await _reader.GetKosovoGrossProfitAsync(new DateOnly(2026, 8, 1), new DateOnly(2026, 10, 1), ct) / 2m;
        var effects = ksEffects.Select(e => new CoverageEffect(e.Case, e.GainPerMonth, ksGp, e.Source)).ToList();
        var coverage = effects.SelectMany(e => CoverageBacktest.Gains(months, e).Select(g => new CoverageGain(e.Case, g.Month, g.Base, g.Gain))).ToList();
        var data = new BacktestSnapshotData(from, to, levers, campaigns, months.ToList(), effects, coverage);
        var record = new BacktestSnapshotRecord
        {
            CreatedUtc = DateTime.UtcNow, CreatedBy = by, Seconds = (int)(DateTime.UtcNow - started).TotalSeconds,
            Json = JsonSerializer.Serialize(data, Json),
        };
        _db.BacktestSnapshots.Add(record);
        await _db.SaveChangesAsync(ct);
        return record;
    }

    public static BacktestSnapshotData? Read(BacktestSnapshotRecord? r) => r is null ? null : JsonSerializer.Deserialize<BacktestSnapshotData>(r.Json, Json);

    /// <summary>One row per figure: section, layer, lever or campaign, month, case, value, lines, units, label.</summary>
    public static string Csv(BacktestSnapshotData d)
    {
        var sb = new StringBuilder("section,layer,item,month,case,value_eur,lines,units,label\n");
        string Q(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        string N(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        foreach (var r in d.Levers)
            foreach (var g in r.Gains)
                sb.AppendLine($"lever,{r.LayerId},{g.Lever},{g.Month},e{N(g.Elasticity)},{N(g.Gain)},{g.Lines},{N(g.Units)},estimated");
        foreach (var r in d.Levers)
            foreach (var c in r.Context)
                sb.AppendLine($"cross-dock loss,{r.LayerId},normal days,{c.Month},measured,{N(c.CrossDockLoss)},{c.CrossDockLossLines},,measured");
        foreach (var c in d.Campaigns)
            foreach (var g in c.DepthGains.Where(g => g.BScale == 1m))
                sb.AppendLine($"lever 7,{c.LayerId},{Q(g.Name)},{g.Days} days,{g.Strategy},{N(g.Gain)},{g.SkusScored},,{(g.Provisional ? "estimated; provisional" : "estimated")}{(g.Representative ? "" : "; not representative")}");
        foreach (var c in d.Campaigns)
            foreach (var r in c.Replays)
                sb.AppendLine($"planner replay,{c.LayerId},{Q(r.Name)},{r.Days} days,actual/planned,{N(r.TotalRatio ?? 0)},{r.SeenSkus},,leave-one-out{(r.SeenType ? "" : "; no event lift")}");
        foreach (var g in d.Coverage)
            sb.AppendLine($"lever 4,MK/AL,coverage,{g.Month},{g.Case},{N(g.Gain)},,,estimated");
        return sb.ToString().Replace("\r\n", "\n");                                            // one line ending, whatever the machine
    }
}
