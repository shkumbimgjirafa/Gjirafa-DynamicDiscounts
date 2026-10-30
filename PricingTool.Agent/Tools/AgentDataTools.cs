using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Storage;

namespace PricingTool.Agent.Tools;

/// <summary>
/// The six fixed read-only tools of plan section 5. Each returns a compact object (serialized to JSON for
/// the agent) built from the same queries and arithmetic as Alpha's scorecard, and each call is logged in
/// <c>ai.AgentQueryLog</c>. <paramref>layer</paramref> is 1 (GjirafaMall KS) or 4 (Gjirafa50 KS); band and
/// category narrow the answer when given.
/// </summary>
public sealed partial class AgentDataTools
{
    private const int MaxCostChangeRows = 25;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PricingDataReader _reader;
    private const int MaxPastDecisions = 60;

    private readonly ScorecardOptions _scorecard;
    private readonly Campaigns.CampaignLimitOptions _campaigns;
    private readonly AgentToolLog _log;
    private readonly AgentDbContext _db;
    private readonly TimeProvider _clock;

    public AgentDataTools(PricingDataReader reader, IOptions<AgentOptions> options, AgentToolLog log, AgentDbContext db,
        TimeProvider? clock = null)
    {
        _reader = reader;
        _scorecard = options.Value.Scorecard;
        _campaigns = options.Value.Campaigns;
        _log = log;
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Units, revenue, gross profit, margin, share sold below / at the floor, cross-dock share, per
    /// band × category, over <paramref name="days"/> normal days ending <see cref="ScorecardOptions.LagDays"/> ago.</summary>
    public Task<object> GetBandPerformanceAsync(AgentToolContext ctx, int layer, int? band, string? category, int days,
        CancellationToken ct = default) =>
        LoggedAsync(ctx, AgentToolNames.GetBandPerformance, new { layer, band, category, days }, async () =>
        {
            days = Math.Clamp(days, 7, 365);
            var info = await _reader.GetLayerAsync(layer, ct);
            var window = DateWindow.Ending(Today(), days, _scorecard.LagDays);
            var bands = await _reader.GetBandConfigAsync(layer, ct);
            var sales = await _reader.GetSalesAsync(layer, window, DateWindow.Empty, _scorecard.FloorTolerancePp,
                _scorecard.AtFloorBandPp, band, category, ct);
            var campaigns = await _reader.GetCampaignWindowsAsync(layer, window.From, window.To, ct);
            var card = AlphaScorecard.Build(info, window, DateWindow.Empty, _scorecard, bands, sales,
                Array.Empty<StockAggregate>(), Array.Empty<CostChangeRow>(), campaigns, null);
            object Row(ScorecardRow r) => new
            {
                r.BandId, band = r.BandName, r.Category, floorPct = r.FloorPct, units = r.Units, netRevenue = R0(r.NetRevenue),
                grossProfit = R0(r.GrossProfit), marginPct = R1(r.MarginPct), belowFloorPct = R1(r.BelowFloorPct),
                atFloorPct = R1(r.AtFloorPct), crossDockPct = R1(r.CrossDockPct), campaignDayNetRevenue = R0(r.CampaignNetRevenue),
                skus = r.SkusSold,
            };
            return (new
            {
                layer = info.LayerName, categoryLevel = info.CategoryLevel, window = window.ToString(), lagDays = _scorecard.LagDays,
                note = "Normal days only (campaign days excluded, their revenue in campaignDayNetRevenue). EUR, VAT excl., booked cost.",
                campaignsExcluded = campaigns.Select(c => $"{c.Name} {c.StartDate:yyyy-MM-dd}..{c.EndDate:yyyy-MM-dd}"),
                rows = card.Rows.Select(Row),
                total = Row(card.Total),
            }, card.Rows.Count);
        }, ct);

    /// <summary>SKUs whose booked unit cost moved more than <paramref name="minChangePct"/> % (last 30 days vs
    /// the 60 before), the largest moves first.</summary>
    public Task<object> GetCostChangesAsync(AgentToolContext ctx, int layer, int? band, string? category, decimal minChangePct,
        CancellationToken ct = default) =>
        LoggedAsync(ctx, AgentToolNames.GetCostChanges, new { layer, band, category, minChangePct }, async () =>
        {
            var threshold = Math.Max(0m, minChangePct) / 100m;
            var all = await _reader.GetCostChangesAsync(layer, band, category, ct);
            var moved = all.Where(c => Math.Abs(c.ChangePct) > threshold).OrderByDescending(c => Math.Abs(c.ChangePct)).ToList();
            return (new
            {
                basis = "Booked unit cost (VAT excl.), last 30 days vs the 60 before; only SKUs that sold in both windows. " +
                        "Band = the band the recent cost incl. VAT falls in.",
                skusWithCostData = all.Count,
                skusUp = moved.Count(c => c.ChangePct > 0),
                skusDown = moved.Count(c => c.ChangePct < 0),
                largest = moved.Take(MaxCostChangeRows).Select(c => new
                {
                    c.Sku, c.BandId, c.Category, c.UnitCostPrior60, c.UnitCostLast30, changePct = R1(100m * c.ChangePct),
                }),
                shownOf = $"{Math.Min(moved.Count, MaxCostChangeRows)} of {moved.Count}",
            }, Math.Min(moved.Count, MaxCostChangeRows));
        }, ct);

    /// <summary>Local stock value at PPTCV, the dead-stock share and the longest-dead SKU, from the latest engine run.</summary>
    public Task<object> GetStockHealthAsync(AgentToolContext ctx, int layer, int? band, string? category, CancellationToken ct = default) =>
        LoggedAsync(ctx, AgentToolNames.GetStockHealth, new { layer, band, category }, async () =>
        {
            var run = await _reader.GetLatestRunAsync(layer, ct);
            if (run is null) return (new { note = "No engine run with a snapshot yet for this layer." }, 0);
            var stock = await _reader.GetStockAsync(layer, run, band, category, ct);
            var bands = (await _reader.GetBandConfigAsync(layer, ct)).ToDictionary(b => b.BandId, b => b.BandName);
            decimal total = stock.Sum(s => s.StockValue), dead = stock.Sum(s => s.DeadStockValue);
            return (new
            {
                engineRun = run.RunId, snapshotDate = run.SnapshotDate.ToString("yyyy-MM-dd"),
                basis = "Local-warehouse units x PPTCV (cost incl. VAT). Dead = what the engine stages as dead stock. " +
                        "maxDeadObservedDays stands in for the oldest-unit age (WMS age is not in the snapshot). " +
                        "GjirafaMall and Gjirafa50 KS share one warehouse: never add their stock.",
                rows = stock.OrderByDescending(s => s.StockValue).Select(s => new
                {
                    s.BandId, band = s.BandId is { } id ? bands.GetValueOrDefault(id) : null, s.Category, s.SkusInStock,
                    stockValue = R0(s.StockValue), deadStockPct = R1(Pct(s.DeadStockValue, s.StockValue)), s.MaxDeadObservedDays,
                }),
                total = new { stockValue = R0(total), deadStockPct = R1(Pct(dead, total)), skusInStock = stock.Sum(s => s.SkusInStock) },
            }, stock.Count);
        }, ct);

    /// <summary>Earlier agent decisions on real (non-test) passes, newest first: what was decided, what code
    /// let through, why, and the outcome tracker's measurement of each applied change.</summary>
    public Task<object> GetPastDecisionsAsync(AgentToolContext ctx, int layer, int? band, string? category, CancellationToken ct = default) =>
        LoggedAsync(ctx, AgentToolNames.GetPastDecisions, new { layer, band, category }, async () =>
        {
            var realPasses = _db.AgentPasses.Where(p => p.LayerId == layer && !p.IsTest && p.Status == "Succeeded").Select(p => p.Id);
            var q = _db.AgentDecisions.AsNoTracking().Where(d => d.LayerId == layer && realPasses.Contains(d.PassId));
            if (band is not null) q = q.Where(d => d.BandId == band);
            if (category is not null) q = q.Where(d => d.Category == category);
            var rows = await q.OrderByDescending(d => d.CreatedUtc).ThenBy(d => d.BandId)
                .Where(d => d.Action != "keep" || d.Status != "Applied")              // changes and anything held or refused
                .Take(MaxPastDecisions).ToListAsync(ct);
            var ids = rows.Select(d => d.Id).ToList();
            var outcomes = await _db.AgentOutcomes.AsNoTracking().Where(x => ids.Contains(x.DecisionId)).ToDictionaryAsync(x => x.DecisionId, ct);
            var paused = await _db.CategoryPauses.AsNoTracking().Where(p => p.LayerId == layer && p.ReleasedUtc == null)
                .Select(p => new { p.BandId, p.Category, p.Reason }).ToListAsync(ct);
            object Outcome(AgentDecision d) => outcomes.TryGetValue(d.Id, out var x)
                ? new
                {
                    x.Status, x.Verdict, gpVsExpectedPct = x.GpVsExpectedPct, unitsVsExpectedPct = x.UnitsVsExpectedPct,
                    gpPerDay = x.GpPerDayBefore is null ? null : $"{x.GpPerDayBefore:0} -> {x.GpPerDayAfter:0}", x.Action, x.Evidence,
                }
                : d.Status == "Applied" ? new { Status = "not measured yet" } : null!;
            return (new
            {
                note = rows.Count == 0
                    ? "No earlier changes or holds on this layer (keeps are not listed)."
                    : "Changes and holds from earlier real passes, newest first (keeps are not listed). outcome = the outcome tracker's " +
                      "measurement: GP/day and units/day after the change went live vs before, against the holdout's season (NotLive: no " +
                      "pushed export priced with it yet; Waiting: its window isn't complete).",
                paused = paused.Select(p => new { p.BandId, p.Category, p.Reason }),
                decisions = rows.Select(d => new
                {
                    decisionId = d.Id, passId = d.PassId, date = d.CreatedUtc.ToString("yyyy-MM-dd"), d.BandId, d.Category, d.Action,
                    floor = $"{d.CurrentFloorPct:0.##} -> {d.ProposedFloorPct:0.##}", target = $"{d.CurrentTargetPct:0.##} -> {d.ProposedTargetPct:0.##}",
                    d.Status, d.Limit, d.Reason, outcome = Outcome(d),
                }),
            }, rows.Count);
        }, ct);

    /// <summary>Fitted price elasticities per band × category for SKUs the latest engine run priced.</summary>
    public Task<object> GetElasticitySummaryAsync(AgentToolContext ctx, int layer, int? band, string? category, CancellationToken ct = default) =>
        LoggedAsync(ctx, AgentToolNames.GetElasticitySummary, new { layer, band, category }, async () =>
        {
            var run = await _reader.GetLatestRunAsync(layer, ct);
            if (run is null) return (new { note = "No engine run yet for this layer." }, 0);
            var fits = await _reader.GetElasticityFitsAsync(layer, run, band, category, ct);
            var rows = fits.GroupBy(f => (f.BandId, f.Category)).Select(g =>
            {
                var usable = g.Where(f => f.IsUsable).Select(f => f.Coefficient).OrderBy(c => c).ToList();
                return new
                {
                    g.Key.BandId, g.Key.Category, skusFitted = g.Count(), skusUsable = usable.Count,
                    medianUsable = usable.Count == 0 ? (decimal?)null : Math.Round(Median(usable), 2),
                    usableElasticPct = usable.Count == 0 ? (decimal?)null : R1(100m * usable.Count(c => c < -1m) / usable.Count),
                };
            }).OrderByDescending(r => r.skusUsable).ToList();
            return (new
            {
                engineRun = run.RunId,
                basis = "Per-SKU log-log fits (the tool's SkuElasticity). usableElasticPct = share of usable fits steeper than -1. " +
                        "Where no usable fit exists, the pooled within-SKU elasticity is about -1.0 to -1.1 (estimated).",
                rows,
                total = new { skusFitted = fits.Count, skusUsable = fits.Count(f => f.IsUsable) },
            }, rows.Count);
        }, ct);

    /// <summary>Today's bands with their cost limits, floors and targets.</summary>
    public Task<object> GetCurrentConfigAsync(AgentToolContext ctx, int layer, CancellationToken ct = default) =>
        LoggedAsync(ctx, AgentToolNames.GetCurrentConfig, new { layer }, async () =>
        {
            var info = await _reader.GetLayerAsync(layer, ct);
            var bands = await _reader.GetBandConfigAsync(layer, ct);
            var rules = await _reader.GetCategoryRulesAsync(layer, ct);
            var paused = await _db.CategoryPauses.AsNoTracking().Where(p => p.LayerId == layer && p.ReleasedUtc == null)
                .Select(p => new { p.BandId, p.Category }).ToListAsync(ct);
            return (new
            {
                layer = info.LayerName, info.Currency, vatRatePct = info.VatRatePct, categoryLevel = info.CategoryLevel,
                basis = "Bands are by cost incl. VAT (PPTCV): [costFrom, costTo). Floor and target are margins on the VAT-inclusive price.",
                bands = bands.Select(b => new
                {
                    b.BandId, band = b.BandName, costFrom = b.MinCost, costTo = b.MaxCost, floorPct = b.MarginFloorPct, targetPct = b.TargetMarginPct,
                }),
                categoryOverrides = rules.Select(r => new
                {
                    r.BandId, r.Category, floorPct = r.MarginFloorPct, targetPct = r.TargetMarginPct, setBy = r.UpdatedBy,
                    setOn = r.UpdatedUtc.ToString("yyyy-MM-dd"), paused = paused.Any(p => p.BandId == r.BandId && p.Category == r.Category),
                }),
                categoryOverridesNote = "A band x category with an override prices against its floor and target instead of the band's; " +
                                        "null keeps the band's value. paused = rolled back after an outcome drop: changes there wait for a person.",
            }, bands.Count);
        }, ct);

    private async Task<object> LoggedAsync(AgentToolContext ctx, string tool, object args, Func<Task<(object Result, int Rows)>> run,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var (result, rows) = await run();
            await _log.WriteAsync(ctx, tool, JsonSerializer.Serialize(args, Json), accepted: true, rows, false, sw.Elapsed, null);
            return result;
        }
        catch (Exception ex) when (ex is ArgumentException or Microsoft.Data.SqlClient.SqlException)
        {
            await _log.WriteAsync(ctx, tool, JsonSerializer.Serialize(args, Json), accepted: true, null, false, sw.Elapsed, ex.Message);
            return new { error = ex.Message };
        }
    }

    private DateOnly Today() => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
    private static decimal R0(decimal v) => Math.Round(v, 0, MidpointRounding.AwayFromZero);
    private static decimal? R1(decimal? v) => v is null ? null : Math.Round(v.Value, 1, MidpointRounding.AwayFromZero);
    private static decimal? Pct(decimal part, decimal whole) => whole > 0 ? 100m * part / whole : null;

    private static decimal Median(IReadOnlyList<decimal> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2m;
}
