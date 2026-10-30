using Microsoft.EntityFrameworkCore;
using PricingTool.Agent.Campaigns;
using PricingTool.Agent.Storage;

namespace PricingTool.Agent.Tools;

/// <summary>The campaign tools of plan section 5 ("Campaign agent and campaign planning", data and tools).</summary>
public sealed partial class AgentDataTools
{
    private const int TrendWeeksMax = 52;

    /// <summary>
    /// Every past campaign of the layer, in two views kept apart. What the campaigns did: units per day against the 28 days
    /// before (season-adjusted with the same windows a year earlier where usable), discount depth against normal days,
    /// margin and gross profit per day; event and depth together. What depth buys: the within-campaign fit (campaign
    /// fixed effects, <see cref="CampaignMath.FitWithinCampaign"/>), with each campaign type's event lift, and the engine's
    /// fitted SKU elasticity next to it as a sanity check.
    /// </summary>
    public Task<object> GetCampaignHistoryAsync(AgentToolContext ctx, int layer, string? category, CancellationToken ct = default) =>
        LoggedAsync(ctx, AgentToolNames.GetCampaignHistory, new { layer, category }, async () =>
        {
            var today = Today();
            var (byCampaign, windows) = await CampaignSegmentsAsync(layer, null, today, ct);
            var entries = await _db.CampaignCalendar.AsNoTracking().Where(c => c.LayerId == layer && c.SourceCampaignId != null)
                .ToDictionaryAsync(c => c.SourceCampaignId!.Value, ct);
            var calendar = entries.ToDictionary(e => e.Key, e => e.Value.Id);
            var excluded = entries.Where(e => !e.Value.Representative).Select(e => e.Key).ToHashSet();

            object Row(CampaignWindow w, CampaignEffect e) => new
            {
                calendarId = calendar.TryGetValue(w.CampaignId, out var id) ? id : (long?)null, campaign = w.Name, type = CampaignMath.TypeOf(w.Name),
                representative = !excluded.Contains(w.CampaignId), context = entries.TryGetValue(w.CampaignId, out var en) ? en.Context : w.Notes,
                provisional = Provisional(w, today), dates = $"{w.StartDate:yyyy-MM-dd}..{w.EndDate:yyyy-MM-dd}", w.CampaignType,
                unitsPerDayBefore = R1(e.UnitsPerDayBefore), unitsPerDayCampaign = R1(e.UnitsPerDayCampaign),
                rawUplift = R2(e.RawUplift), seasonFactor = R2(e.SeasonFactor), uplift = R2(e.Uplift),
                depthBeforePct = R1(e.DepthBeforePct), depthCampaignPct = R1(e.DepthCampaignPct), extraDepthPp = R1(e.ExtraDepthPp),
                marginBeforePct = R1(e.MarginBeforePct), marginCampaignPct = R1(e.MarginCampaignPct),
                gpPerDayBefore = R0(e.GpPerDayBefore), gpPerDayCampaign = R0(e.GpPerDayCampaign), thin = e.Thin,
            };

            var rows = byCampaign.Select(c => (c.Window, Effect: CampaignMath.Read(category is null
                ? Total(c.ByCategory.Values)
                : c.ByCategory.TryGetValue(category, out var s) ? s : Empty(c)))).ToList();
            var eventPlusDepth = CampaignMath.Fit(rows.Where(r => !Provisional(r.Window, today) && !excluded.Contains(r.Window.CampaignId)).Select(r => r.Effect),
                category ?? "all categories");
            var depth = DepthEffectFrom(byCampaign, today, excluded);
            var elasticity = await MedianUsableElasticityAsync(layer, category, ct);

            const string basis = "Booked COGS, EUR VAT excl. uplift = units/day in the campaign over the 28 days before, divided by the same " +
                                 "ratio a year earlier (seasonFactor; null when last year's windows sold too little, start before the data " +
                                 "(Oct 2024) or follow a campaign, then uplift = rawUplift). depth = discount given / pre-discount price, both incl. " +
                                 "VAT, lines claiming over 80 % off left out (data errors); extraDepthPp = campaign depth minus normal-day depth. " +
                                 "Depth is null where under 20 % of the lines carry a discount (GjirafaMall from Nov 2025). provisional = ended " +
                                 "in the last 14 days (cancellations still post): listed, not fitted. representative = false: a person said the window " +
                                 "isn't what such a campaign normally does (see context): listed, not fitted, not an anchor for a goal.";
            return (new
            {
                layer, category = category ?? "all", basis,
                whatCampaignsDid = new
                {
                    note = "Event and depth together: the campaign's traffic, banners and newsletter are in these numbers. Not what depth alone buys.",
                    campaigns = rows.Select(r => Row(r.Window, r.Effect)),
                    eventPlusDepthCurve = new
                    {
                        b = eventPlusDepth.B, campaignsUsed = eventPlusDepth.Points,
                        unitsMultiplierAtExtraDepthPct = eventPlusDepth.Grid.ToDictionary(g => g.DepthPct.ToString("0"), g => g.Uplift),
                        eventPlusDepth.Basis,
                    },
                },
                whatDepthBuys = new
                {
                    note = "units multiplier = eventLift(campaign type) x exp(b x extra depth). b comes only from categories that went deeper " +
                           "or shallower than others in the same campaign (campaign fixed effects), all categories of the layer pooled; " +
                           "never read beyond the extra depths seen. impliedElasticity ~ -b, next to the engine's own fitted SKU elasticity.",
                    b = depth.B, campaignsUsed = depth.Campaigns, points = depth.Points,
                    extraDepthSeenPp = depth.B is null ? null : $"{depth.MinDepthPct:0.#} to {depth.MaxDepthPct:0.#}",
                    depthMultiplierAtExtraDepthPct = depth.B is null ? null
                        : CampaignMath.DepthGrid.ToDictionary(d => d.ToString("0"), d => Math.Round(depth.DepthMultiplier(d)!.Value, 2)),
                    impliedElasticity = depth.B is { } b ? -b : (decimal?)null,
                    engineMedianSkuElasticity = elasticity,
                    eventLiftByType = depth.EventLiftByType,
                    depth.Basis,
                },
                upcoming = windows.Where(w => w.EndDate >= today).Select(w => new
                {
                    calendarId = calendar.TryGetValue(w.CampaignId, out var id) ? id : (long?)null, w.Name, type = CampaignMath.TypeOf(w.Name),
                    dates = $"{w.StartDate:yyyy-MM-dd}..{w.EndDate:yyyy-MM-dd}", w.CampaignType,
                    context = entries.TryGetValue(w.CampaignId, out var en) ? en.Context : w.Notes,
                }),
            }, rows.Count);
        }, ct);

    /// <summary>The layer's within-campaign depth effect, for the campaign planner (code, not a logged tool call).</summary>
    public async Task<DepthEffect> GetDepthEffectAsync(int layer, CancellationToken ct = default)
    {
        var today = Today();
        var (byCampaign, _) = await CampaignSegmentsAsync(layer, null, today, ct);
        var excluded = (await _db.CampaignCalendar.AsNoTracking().Where(c => c.LayerId == layer && !c.Representative && c.SourceCampaignId != null)
            .Select(c => c.SourceCampaignId!.Value).ToListAsync(ct)).ToHashSet();
        return DepthEffectFrom(byCampaign, today, excluded);
    }

    /// <summary>
    /// Every finished campaign of the layer replayed with the planner's arithmetic (<see cref="CampaignReplay"/>), leave-one-out
    /// (event lift and depth fit refitted without the campaign scored) and in-sample beside it, with the calibration factors from
    /// the usable ones: how far planned gross profit lands from what campaigns really made. The planner uses the leave-one-out one.
    /// </summary>
    public async Task<(List<CampaignReplayResult> Replays, CampaignCalibration? Calibration, List<CampaignReplayResult> InSample, CampaignCalibration? InSampleCalibration)>
        ReplayCampaignsAsync(int layer, CancellationToken ct = default)
    {
        var today = Today();
        var (byCampaign, _) = await CampaignSegmentsAsync(layer, null, today, ct);
        var windows = await _reader.GetCampaignWindowsAsync(layer, new DateOnly(2000, 1, 1), today, ct);
        var rows = (await _reader.GetCampaignReplayRowsAsync(layer, today, ct)).ToLookup(r => r.CampaignId);
        var notRepresentative = (await _db.CampaignCalendar.AsNoTracking().Where(c => c.LayerId == layer && !c.Representative && c.SourceCampaignId != null)
            .Select(c => c.SourceCampaignId!.Value).ToListAsync(ct)).ToHashSet();
        var inSampleDepth = DepthEffectFrom(byCampaign, today, notRepresentative);
        List<CampaignReplayResult> loo = new(), inSample = new();
        foreach (var w in windows.Where(w => w.EndDate < today && !Provisional(w, today) && rows.Contains(w.CampaignId)))
        {
            var rate = CampaignReplay.RateWindow(w.StartDate);
            var normalDays = Enumerable.Range(0, rate.Days).Select(rate.From.AddDays).Count(d => !windows.Any(o => d >= o.StartDate && d <= o.EndDate));
            var representative = !notRepresentative.Contains(w.CampaignId);
            var without = DepthEffectFrom(byCampaign, today, new HashSet<int>(notRepresentative) { w.CampaignId });
            loo.Add(CampaignReplay.Replay(w, normalDays, rows[w.CampaignId], without, representative));
            inSample.Add(CampaignReplay.Replay(w, normalDays, rows[w.CampaignId], inSampleDepth, representative));
        }
        return (loo, CampaignReplay.Calibrate(loo), inSample, CampaignReplay.Calibrate(inSample, leaveOneOut: false));
    }

    /// <summary>The within-campaign fit over finished, representative campaigns.</summary>
    private static DepthEffect DepthEffectFrom(List<(CampaignWindow Window, Dictionary<string, CampaignSegments> ByCategory)> byCampaign, DateOnly today,
        IReadOnlySet<int> notRepresentative) =>
        CampaignMath.FitWithinCampaign(byCampaign.Where(c => !Provisional(c.Window, today) && !notRepresentative.Contains(c.Window.CampaignId)).SelectMany(c => c.ByCategory.Select(kv =>
            new CampaignPoint(c.Window.CampaignId.ToString(System.Globalization.CultureInfo.InvariantCulture), CampaignMath.TypeOf(c.Window.Name),
                CampaignMath.Read(kv.Value)))), "all categories");

    private static bool Provisional(CampaignWindow w, DateOnly today) => w.EndDate > today.AddDays(-14);

    /// <summary>The median usable per-SKU elasticity the engine fitted, for the SKUs its latest run priced.</summary>
    private async Task<decimal?> MedianUsableElasticityAsync(int layer, string? category, CancellationToken ct)
    {
        var run = await _reader.GetLatestRunAsync(layer, ct);
        if (run is null) return null;
        var usable = (await _reader.GetElasticityFitsAsync(layer, run, null, category, ct)).Where(f => f.IsUsable).Select(f => f.Coefficient).OrderBy(c => c).ToList();
        return usable.Count == 0 ? null : Math.Round(Median(usable), 2);
    }

    /// <summary>Revenue, gross profit, margin, units and discount depth per week against the same weeks a year earlier,
    /// with each week's campaign days, plus today's local stock and dead-stock share from the latest engine run.</summary>
    public Task<object> GetSalesTrendsAsync(AgentToolContext ctx, int layer, string? category, int weeks, CancellationToken ct = default) =>
        LoggedAsync(ctx, AgentToolNames.GetSalesTrends, new { layer, category, weeks }, async () =>
        {
            weeks = Math.Clamp(weeks, 2, TrendWeeksMax);
            var today = Today();
            var from = today.AddDays(-7 * weeks);
            var rows = await _reader.GetWeeklySalesAsync(layer, category, from, weeks, ct);
            var windows = await _reader.GetCampaignWindowsAsync(layer, from.AddDays(-364), today, ct);
            int CampaignDays(DateOnly start) => Enumerable.Range(0, 7).Count(i => windows.Any(w => start.AddDays(i) >= w.StartDate && start.AddDays(i) <= w.EndDate));

            var series = Enumerable.Range(0, weeks).Select(i =>
            {
                var start = from.AddDays(7 * i);
                var now = rows.FirstOrDefault(r => r.Week == i && !r.LastYear);
                var ly = rows.FirstOrDefault(r => r.Week == i && r.LastYear);
                return new
                {
                    week = $"{start:yyyy-MM-dd}", units = R0(now?.Units ?? 0), netRevenue = R0(now?.NetRevenue ?? 0), grossProfit = R0(now?.GrossProfit ?? 0),
                    marginPct = R1(Pct(now?.GrossProfit ?? 0, now?.NetRevenue ?? 0)), depthPct = R1(now?.DepthPct),
                    campaignDays = CampaignDays(start),
                    lastYear = new { units = R0(ly?.Units ?? 0), netRevenue = R0(ly?.NetRevenue ?? 0), grossProfit = R0(ly?.GrossProfit ?? 0), campaignDays = CampaignDays(start.AddDays(-364)) },
                    lateCancellations = start.AddDays(7) > today.AddDays(-14),
                };
            }).ToList();

            var run = await _reader.GetLatestRunAsync(layer, ct);
            var stock = run is null ? new List<StockAggregate>() : await _reader.GetStockAsync(layer, run, null, category, ct);
            decimal stockValue = stock.Sum(s => s.StockValue), dead = stock.Sum(s => s.DeadStockValue);
            return (new
            {
                layer, category = category ?? "all",
                basis = "Booked COGS, EUR VAT excl.; lastYear = the same weeks 364 days earlier. Weeks with lateCancellations = true still lose " +
                        "lines to cancellations (they look better than they will). depth = discount given / pre-discount value. Stock: local " +
                        "warehouse at PPTCV (cost incl. VAT) from the latest engine run; GjirafaMall and Gjirafa50 KS share one warehouse.",
                weeks = series,
                stock = run is null ? null : new { engineRun = run.RunId, stockValue = R0(stockValue), deadStockPct = R1(Pct(dead, stockValue)), skusInStock = stock.Sum(s => s.SkusInStock) },
            }, series.Count);
        }, ct);

    /// <summary>A finished campaign's actual results against its goal (and against the 28 days before), from the sales lines.</summary>
    public Task<object> GetCampaignResultsAsync(AgentToolContext ctx, long campaignId, CancellationToken ct = default) =>
        LoggedAsync(ctx, AgentToolNames.GetCampaignResults, new { campaignId }, async () =>
        {
            var c = await _db.CampaignCalendar.AsNoTracking().SingleOrDefaultAsync(x => x.Id == campaignId, ct);
            if (c is null) return (new { error = $"No campaign {campaignId} in the calendar (ids are in get_campaign_history's calendarId)." }, 0);
            var today = Today();
            if (c.EndDate >= today)
                return (new { campaign = c.Name, c.Status, note = $"Not finished: it ends {c.EndDate:yyyy-MM-dd}." }, 0);
            if (c.SourceCampaignId is null)
                return (new { campaign = c.Name, c.Status, note = "No sales windows recorded for this campaign yet." }, 0);

            var scope = c.ScopeKind == "category" ? c.ScopeValue : null;
            var (byCampaign, _) = await CampaignSegmentsAsync(c.LayerId, scope, today, ct);
            var match = byCampaign.FirstOrDefault(x => x.Window.CampaignId == c.SourceCampaignId);
            if (match.ByCategory is null) return (new { campaign = c.Name, note = "No sales lines in its window." }, 0);
            var seg = Total(match.ByCategory.Values);
            var e = CampaignMath.Read(seg);
            object? goal = c.GoalType switch
            {
                null => null,
                "gross-profit" => new { c.GoalType, target = c.GoalValue, actual = R0(seg.Campaign.GrossProfit), met = seg.Campaign.GrossProfit >= c.GoalValue },
                "units" => new { c.GoalType, target = c.GoalValue, actual = R0(seg.Campaign.Units), met = seg.Campaign.Units >= c.GoalValue },
                "margin" => new { c.GoalType, target = c.GoalValue, actual = R1(seg.Campaign.MarginPct), met = seg.Campaign.MarginPct >= c.GoalValue },
                _ => new { c.GoalType, target = c.GoalValue, actual = (decimal?)null, met = (bool?)null },
            };
            return (new
            {
                campaign = c.Name, dates = $"{c.StartDate:yyyy-MM-dd}..{c.EndDate:yyyy-MM-dd}", scope = scope ?? "all", c.Status,
                goal = goal ?? (object)"No goal was set (a shop campaign planned outside the tool).",
                actual = new
                {
                    units = R0(seg.Campaign.Units), netRevenue = R0(seg.Campaign.NetRevenue), grossProfit = R0(seg.Campaign.GrossProfit),
                    marginPct = R1(seg.Campaign.MarginPct), depthPct = R1(seg.Campaign.DepthPct), gpPerDay = R0(seg.Campaign.GpPerDay),
                },
                vsBefore = new { uplift = R2(e.Uplift), rawUplift = R2(e.RawUplift), gpPerDayBefore = R0(e.GpPerDayBefore), marginBeforePct = R1(e.MarginBeforePct) },
                campaignHoldout = "None: a campaign holdout exists only for campaigns the tool plans (10 % of eligible SKUs kept out).",
                basis = "Booked COGS, EUR VAT excl.; before = the 28 days before the campaign without other campaign days.",
            }, 1);
        }, ct);

    /// <summary>Per past campaign (ended before today): its window and its four segments per category, with normal-day counts.</summary>
    private async Task<(List<(CampaignWindow Window, Dictionary<string, CampaignSegments> ByCategory)> Campaigns, List<CampaignWindow> Windows)>
        CampaignSegmentsAsync(int layer, string? category, DateOnly today, CancellationToken ct)
    {
        var windows = await _reader.GetCampaignWindowsAsync(layer, new DateOnly(2000, 1, 1), today.AddYears(2), ct);
        var rows = await _reader.GetCampaignSegmentsAsync(layer, category, today, ct);
        var dataFrom = await _reader.GetFirstSaleDayAsync(layer, ct) ?? today;
        bool IsCampaignDay(DateOnly d) => windows.Any(w => d >= w.StartDate && d <= w.EndDate);
        // A comparison window that starts before the data does has no usable day count (0 = not usable).
        int NormalDays(DateOnly from, DateOnly to) =>
            from < dataFrom ? 0 : Enumerable.Range(0, to.DayNumber - from.DayNumber).Count(i => !IsCampaignDay(from.AddDays(i)));

        var result = new List<(CampaignWindow, Dictionary<string, CampaignSegments>)>();
        foreach (var w in windows.Where(w => w.EndDate < today).OrderBy(w => w.StartDate))
        {
            // Last year's days right after a campaign are a slump, not the season: no season factor then.
            var lyStart = w.StartDate.AddDays(-364);
            var afterCampaign = windows.Any(o => o.EndDate < lyStart && o.EndDate >= lyStart.AddDays(-14));
            var days = new[]
            {
                w.EndDate.DayNumber - w.StartDate.DayNumber + 1,
                NormalDays(w.StartDate.AddDays(-28), w.StartDate),
                afterCampaign ? 0 : NormalDays(lyStart, w.EndDate.AddDays(-363)),
                afterCampaign ? 0 : NormalDays(w.StartDate.AddDays(-392), lyStart),
            };
            var byCategory = rows.Where(r => r.CampaignId == w.CampaignId).GroupBy(r => r.Category, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g =>
                {
                    SegmentSales S(int seg) => g.Where(r => r.Segment == seg).Select(r => new SegmentSales(r.Units, r.NetRevenue, r.GrossProfit, r.Discount, r.PreDiscountGross, days[seg], r.Lines, r.DiscountedLines))
                        .FirstOrDefault() ?? SegmentSales.Empty with { Days = days[seg] };
                    return new CampaignSegments(S(0), S(1), S(2), S(3));
                }, StringComparer.Ordinal);
            result.Add((w, byCategory));
        }
        return (result, windows);
    }

    private static CampaignSegments Total(IEnumerable<CampaignSegments> parts) =>
        parts.Aggregate(new CampaignSegments(SegmentSales.Empty, SegmentSales.Empty, SegmentSales.Empty, SegmentSales.Empty),
            (a, s) => new CampaignSegments(a.Campaign.Plus(s.Campaign), a.Before.Plus(s.Before), a.CampaignLastYear.Plus(s.CampaignLastYear),
                a.BeforeLastYear.Plus(s.BeforeLastYear)));

    private static CampaignSegments Empty((CampaignWindow Window, Dictionary<string, CampaignSegments> ByCategory) c) =>
        new(SegmentSales.Empty, SegmentSales.Empty, SegmentSales.Empty, SegmentSales.Empty);

    private static decimal? R2(decimal? v) => v is null ? null : Math.Round(v.Value, 2, MidpointRounding.AwayFromZero);
}
