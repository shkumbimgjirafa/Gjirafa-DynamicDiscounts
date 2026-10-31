using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Storage;
using PricingTool.Core.Domain;
using PricingTool.Core.Services;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;

namespace PricingTool.NopIntegration.CostWatch;

/// <summary>Section "CostWatcher".</summary>
public class CostWatcherOptions
{
    public const string SectionName = "CostWatcher";

    /// <summary>How often the engine host runs a watch (plan: every 15 minutes).</summary>
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>A cost rise above this many percent looks like a data error: held, alerted, nothing published.</summary>
    public decimal DataErrorRisePct { get; set; } = 50m;

    /// <summary>A rise smaller than this many percent is noise and not acted on.</summary>
    public decimal MinRisePct { get; set; } = 0.5m;

    /// <summary>Poll the replica's ProductPricing for refreshed costs (off: only injected test rows are watched).</summary>
    public bool PollReplica { get; set; } = true;

    /// <summary>The replica feed watches SKUs whose current margin is within this many points of their floor.</summary>
    public decimal WatchHeadroomPp { get; set; } = 15m;

    /// <summary>A cheaper team discount (one product, not ours) that keeps a price under its floor after a cost rise is ended so
    /// the floor price applies, unless a chat rule "protect-discount" protects it (the user's decision, 31 Oct). Off: held for a person.</summary>
    public bool EndTeamDeals { get; set; } = true;

    /// <summary>An observation whose source starts with this was re-queued after Alpha confirmed the cost jump is real: it is
    /// not held as a data error again.</summary>
    public const string ConfirmedSource = "alpha-confirmed";
}

/// <summary>What publishing does for the watcher; the writer in production, a fake in tests.</summary>
public interface ICostPublisher
{
    /// <summary>Puts the shopper's price at <paramref name="price"/>, as the daily publish does: our ai-price discount when it is under
    /// the store's regular price, the regular price itself when it is at or above it (a discount can't raise a price). With what the
    /// engine run saw and the ceiling max(anchor, current). Returns the batch, its status, and why the price didn't stand, if it
    /// didn't (the writer's exception kind: a cheaper team deal, a price changed since the run, a round-trip mismatch).</summary>
    Task<(long BatchId, string Status, string? LeftOut)> RaiseAsync(int layerId, string sku, decimal price, decimal ceiling, decimal runCurrentPrice,
        decimal runOldPrice, CancellationToken ct);

    /// <summary>Ends our ai-price discount on the SKU, if it has one: the batch, or null when there was nothing of ours.</summary>
    Task<long?> EndOurDiscountAsync(int layerId, string sku, CancellationToken ct);

    /// <summary>Ends our live ai-campaign discount on the SKU before the campaign ends: the batch, or null when nothing was live.</summary>
    Task<long?> EndCampaignDiscountAsync(int layerId, long campaignKey, string sku, CancellationToken ct);

    /// <summary>False for a test run's publisher: the watcher then records what it would do and calls nothing above.</summary>
    bool Publishes => true;
}

/// <summary>
/// A test run's (<c>--test</c>) publisher: it writes nothing. A test run's records go to the test database, but the writer's
/// sandbox is the tool database's: a raise published from a test run would change the shelf the app reports, with its audit
/// where the app's rollback can't find it. Every call throws, so nothing can slip through.
/// </summary>
public sealed class TestRunCostPublisher : ICostPublisher
{
    public bool Publishes => false;

    public Task<(long BatchId, string Status, string? LeftOut)> RaiseAsync(int layerId, string sku, decimal price, decimal ceiling, decimal runCurrentPrice,
        decimal runOldPrice, CancellationToken ct) => throw NotInATestRun();

    public Task<long?> EndOurDiscountAsync(int layerId, string sku, CancellationToken ct) => throw NotInATestRun();

    public Task<long?> EndCampaignDiscountAsync(int layerId, long campaignKey, string sku, CancellationToken ct) => throw NotInATestRun();

    private static InvalidOperationException NotInATestRun() => new("A test run (--test) publishes nothing.");
}

public sealed class WriterCostPublisher : ICostPublisher
{
    private readonly NopDiscountWriter _writer;
    private readonly AgentDbContext _db;
    private readonly CostWatcherOptions _options;
    private readonly TimeProvider _clock;

    public WriterCostPublisher(NopDiscountWriter writer, AgentDbContext db, IOptions<CostWatcherOptions> options, TimeProvider? clock = null)
    {
        _writer = writer;
        _db = db;
        _options = options.Value;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<(long BatchId, string Status, string? LeftOut)> RaiseAsync(int layerId, string sku, decimal price, decimal ceiling, decimal runCurrentPrice,
        decimal runOldPrice, CancellationToken ct)
    {
        var item = new PublishItem(sku, price, runOldPrice) { RunCurrentPrice = runCurrentPrice, RunOldPrice = runOldPrice, Ceiling = ceiling };
        var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
        var protectedPatterns = _options.EndTeamDeals
            ? await _db.ChatRules.AsNoTracking()
                .Where(r => r.Kind == ChatRuleKinds.ProtectDiscount && r.RemovedUtc == null && (r.LayerId == null || r.LayerId == layerId) && r.FromDate <= today && r.ToDate >= today)
                .Select(r => r.Value).ToListAsync(ct)
            : new List<string>();
        var o = await _writer.PublishAsync(new PublishRequest(layerId, new[] { item })
        {
            RaiseRegularPrices = true, EndTeamDeals = _options.EndTeamDeals, ProtectedDealPatterns = protectedPatterns,
        }, ct);
        // app-deal-cheaper is information (our price went out on the web), not a reason the price didn't stand.
        return (o.BatchId, o.Status, o.Exceptions.FirstOrDefault(e => e.Sku == sku && e.Kind != "app-deal-cheaper")?.Kind);
    }

    public async Task<long?> EndOurDiscountAsync(int layerId, string sku, CancellationToken ct)
    {
        var id = await _writer.ExpireAsync(layerId, new[] { sku }, ct);
        return await _db.PublishBatches.AsNoTracking().Where(b => b.Id == id).Select(b => b.Updated).SingleAsync(ct) > 0 ? id : null;
    }

    public async Task<long?> EndCampaignDiscountAsync(int layerId, long campaignKey, string sku, CancellationToken ct)
    {
        var id = await _writer.ExpireCampaignAsync(layerId, campaignKey.ToString(CultureInfo.InvariantCulture), new[] { sku }, ct);
        return await _db.PublishBatches.AsNoTracking().Where(b => b.Id == id).Select(b => b.Updated).SingleAsync(ct) > 0 ? id : null;
    }
}

public sealed record CostWatchResult(int Observed, int Raised, int Held, int Ended, int NoAction, string? Skipped);

/// <summary>
/// The SKUs the cost watcher leaves to the daily run, because another firm rule owns their price: the dead-stock lane
/// (the only one allowed under the floor, at its cost-based tiers, and the clearance lock that pins it), a GjirafaMall
/// price that follows Gjirafa50 verbatim (the Gjirafa50 layer's own watch covers the shared SKU), and the holdout
/// categories (the control group stays exactly as the pre-agent tool prices it).
/// </summary>
public static class CostWatchLanes
{
    public static string? LaneOf(int deadStockStage, string codes, bool isMall, bool isHoldout) =>
        deadStockStage != 0 || codes.Contains("DEAD_STOCK", StringComparison.Ordinal) || codes.Contains(Core.Domain.GuardrailFlags.ClearanceLockHeld, StringComparison.Ordinal)
            ? "dead-stock-lane"
            : isMall && codes.Contains(Core.Domain.GuardrailFlags.SyncedToGj50, StringComparison.Ordinal) ? "synced-to-gj50"
            : isHoldout ? "holdout"
            : null;

    /// <summary>Whether the SKU's category (its own listing first, else its lowest product id) is a holdout category.</summary>
    public static bool IsHoldout(IReadOnlyDictionary<string, IReadOnlyList<SkuListing>>? listings, IReadOnlyCollection<string> holdouts, string sku, int? productId)
    {
        if (listings is null || holdouts.Count == 0 || !listings.TryGetValue(sku, out var l) || l.Count == 0) return false;
        var own = productId is { } id ? l.FirstOrDefault(x => x.ProductId == id) : null;
        return holdouts.Contains((own ?? l.MinBy(x => x.ProductId))!.Category, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The fast lane for cost rises (plan section 5, "CostWatcher"). Holding the floor is a rule, not a judgment, so code
/// does it within one watch: a SKU whose cost rose and whose live price is now under its floor is raised to the floor price
/// (cost ÷ (1 − floor), charm-rounded up), never above max(anchor, current price), the way the daily publish writes a price:
/// our ai-price discount under the store's regular price, the regular price itself at or above it.
/// It doesn't just raise in three cases: a cost jump that looks like a data error is held and alerted, nothing
/// published; a floor price above the shelf price (our discount removed would still be under the floor) ends our
/// discount and alerts that the regular price needs changing; a low price from a discount we didn't create is held for
/// a person (the publisher leaves cheaper team deals alone). A SKU in one of our live campaigns is judged against the campaign
/// plan's minimum margin, not the band floor (the campaign price sits under the floor on purpose): above it nothing changes
/// ("campaign"), under it our campaign discount on that SKU ends early. Every action is an <see cref="AgentDecision"/> (source
/// CostWatcher) and, through the writer, a <c>PublishAudit</c>, so it shows in the feed and can be rolled back.
/// Floors come from the band, or its per-category rule. The kill switch stops it.
/// </summary>
public sealed class CostWatcher
{
    public const string Source = "CostWatcher";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly AgentDbContext _db;
    private readonly PricingToolDbContext _tool;
    private readonly BandConfigProvider _bands;
    private readonly ICostPublisher _publisher;
    private readonly IKillSwitch _killSwitch;
    private readonly CostWatcherOptions _options;
    private readonly ILogger<CostWatcher> _logger;
    private readonly TimeProvider _clock;
    private readonly PricingTool.Agent.AgentOptions _agent;
    private readonly decimal _minDisplayedDiscountPct;
    private readonly ICategoryMapReader? _categoryMap;

    public CostWatcher(AgentDbContext db, PricingToolDbContext tool, BandConfigProvider bands, ICostPublisher publisher, IKillSwitch killSwitch,
        IOptions<CostWatcherOptions> options, IOptions<PricingTool.Agent.AgentOptions> agent, IOptions<Core.Options.PricingEngineOptions> engine,
        ILogger<CostWatcher> logger, TimeProvider? clock = null, ICategoryMapReader? categoryMap = null)
    {
        _agent = agent.Value;
        _minDisplayedDiscountPct = engine.Value.MinDisplayedDiscountPct;
        _categoryMap = categoryMap;
        _db = db;
        _tool = tool;
        _bands = bands;
        _publisher = publisher;
        _killSwitch = killSwitch;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Processes every observation not processed yet. With the kill switch pulled nothing is processed (they wait).</summary>
    public async Task<CostWatchResult> WatchAsync(CancellationToken ct = default)
    {
        var stop = await _killSwitch.GetAsync(ct);
        if (stop.Stopped) return new CostWatchResult(0, 0, 0, 0, 0, $"kill switch: {stop.Reason}");
        var pending = await _db.CostObservations.Where(o => o.ProcessedUtc == null).OrderBy(o => o.Id).ToListAsync(ct);
        if (pending.Count == 0) return new CostWatchResult(0, 0, 0, 0, 0, null);

        var now = _clock.GetUtcNow().UtcDateTime;
        var passes = new Dictionary<int, AgentPass>();
        int raised = 0, held = 0, ended = 0, none = 0;
        var testRun = !_publisher.Publishes;
        foreach (var layerGroup in pending.GroupBy(o => o.LayerId))
        {
            var layer = await _tool.Layers.AsNoTracking().SingleOrDefaultAsync(l => l.Id == layerGroup.Key, ct);
            var run = layer is null ? null : await _tool.PricingRuns.AsNoTracking()
                .Where(r => r.LayerId == layer.Id && (r.Status == RunStatus.Succeeded || r.Status == RunStatus.SucceededWithErrors))
                .OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);
            if (layer is null || run is null)
            {
                foreach (var o in layerGroup) Done(o, "unknown-sku");
                none += layerGroup.Count();
                continue;
            }
            var skus = layerGroup.Select(o => o.Sku).Distinct().ToList();
            var date = run.StartedUtc.Date;
            var snaps = (await _tool.DailySnapshots.AsNoTracking().Where(s => s.LayerId == layer.Id && s.SnapshotDate == date && skus.Contains(s.Sku)).ToListAsync(ct))
                .GroupBy(s => s.Sku, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var bands = await _bands.GetBandsAsync(layer.Id, ct);
            var categoryBands = await _bands.GetCategoryBandsAsync(layer, bands, ct);
            var proposals = (await _tool.ProposedPrices.AsNoTracking().Where(p => p.PricingRunId == run.Id && skus.Contains(p.Sku))
                    .Select(p => new { p.Sku, p.ProductId, p.DeadStockStage, Codes = p.ReasonCodes + "," + p.GuardrailFlags }).ToListAsync(ct))
                .GroupBy(p => p.Sku, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var isMall = layer.Brand == "GjirafaMall";
            var holdouts = _agent.Limits.Holdouts.TryGetValue(layer.Id.ToString(CultureInfo.InvariantCulture), out var h) ? h : new List<string>();
            var holdoutMap = holdouts.Count > 0 && _categoryMap is not null ? await _categoryMap.ReadAsync(layer.Id, isMall, holdouts, ct) : null;
            // Our live campaigns (published, not rolled back, today inside the window): the price on those days is the campaign's.
            var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
            var campaigns = (await (from l in _db.CampaignPlanLines.AsNoTracking()
                                    join p in _db.CampaignPlans.AsNoTracking() on l.PlanId equals p.Id
                                    join b in _db.CampaignBriefs.AsNoTracking() on p.BriefId equals b.Id
                                    join x in _db.PublishBatches.AsNoTracking() on p.PublishBatchId equals x.Id
                                    where b.LayerId == layer.Id && !p.IsTest && p.Status == PlanStatus.Published && x.RolledBackUtc == null
                                          && b.StartDate <= today && b.EndDate >= today && skus.Contains(l.Sku)
                                    orderby p.Id descending
                                    select new { l.Sku, l.CampaignPrice, b.MinMarginPct, Key = b.CalendarId ?? b.Id }).ToListAsync(ct))
                .GroupBy(c => c.Sku, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            // What the watcher itself published since the snapshot: that is the live price now.
            var ours = (await _db.CostObservations.AsNoTracking().Where(o => o.LayerId == layer.Id && o.Outcome == "raised" && skus.Contains(o.Sku) && o.DecisionId != null)
                    .Join(_db.AgentDecisions, o => o.DecisionId, d => d.Id, (o, d) => new { o.Sku, o.ProcessedUtc, d.EvidenceRefs }).ToListAsync(ct))
                .Where(x => x.ProcessedUtc >= run.StartedUtc).GroupBy(x => x.Sku, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.ProcessedUtc).First().EvidenceRefs, StringComparer.OrdinalIgnoreCase);

            foreach (var o in layerGroup)
            {
                if (!snaps.TryGetValue(o.Sku, out var s) || s.Pptcv is not { } oldCost || oldCost <= 0 || s.CurrentPrice is not { } current || current <= 0)
                {
                    Done(o, "unknown-sku");
                    none++;
                    continue;
                }
                proposals.TryGetValue(o.Sku, out var proposal);
                var lane = CostWatchLanes.LaneOf((int)(proposal?.DeadStockStage ?? 0), proposal?.Codes ?? "", isMall,
                    CostWatchLanes.IsHoldout(holdoutMap, holdouts, o.Sku, proposal?.ProductId));
                if (lane is not null)
                {
                    Done(o, lane);                                                   // another firm rule owns this price
                    none++;
                    continue;
                }
                var risePct = 100m * (o.Pptcv - oldCost) / oldCost;
                if (risePct < _options.MinRisePct)
                {
                    Done(o, "not-a-rise");
                    none++;
                    continue;
                }
                var live = ours.TryGetValue(o.Sku, out var evidence) && JsonDocument.Parse(evidence).RootElement.TryGetProperty("publishedPrice", out var pp)
                    ? pp.GetDecimal() : current;
                var band = BandConfigProvider.FindBand(bands, o.Pptcv) ?? BandConfigProvider.FindBand(bands, oldCost);
                if (band is null)
                {
                    Done(o, "unknown-sku");
                    none++;
                    continue;
                }
                band = categoryBands.Resolve(o.Sku, null, band);
                var category = categoryBands.CategoryOf(o.Sku, null) ?? "";
                var floorPct = band.MarginFloorPct;
                var floorPrice = VatMath.MinGrossPriceForMargin(o.Pptcv, floorPct);
                var anchor = s.AnchorPrice is > 0 ? s.AnchorPrice.Value : s.OldPrice ?? current;
                var ceiling = Math.Max(anchor, current);
                // The engine's own charm rounding, never under the floor price (GjmCharm has no static round-up).
                var target = band.RoundingEnabled && band.Rounding != RoundingConvention.None && ceiling > floorPrice
                    ? new RoundingService().Apply(floorPrice, band.Rounding, new PriceBounds(floorPrice, ceiling)).Price
                    : Math.Ceiling(floorPrice * 100m) / 100m;
                var facts = new Dictionary<string, object?>
                {
                    ["sku"] = o.Sku, ["observation"] = o.Id, ["source"] = o.Source, ["oldCost"] = oldCost, ["newCost"] = o.Pptcv,
                    ["risePct"] = Math.Round(risePct, 1), ["livePrice"] = live, ["floorPct"] = floorPct, ["floorPrice"] = Math.Round(floorPrice, 2),
                    ["anchor"] = anchor, ["engineRun"] = run.Id,
                };

                if (risePct > _options.DataErrorRisePct && !o.Source.StartsWith(CostWatcherOptions.ConfirmedSource, StringComparison.Ordinal))
                {
                    var d = Decision(o, band.BandId, category, floorPct, "hold", DecisionStatus.Held, "data-error-cost-jump",
                        $"Cost {oldCost:0.00} -> {o.Pptcv:0.00} (+{risePct:0}%) looks like a data error (over {_options.DataErrorRisePct:0}%): nothing published; check the cost.", facts);
                    Done(o, "held-data-error", d);
                    held++;
                }
                else if (campaigns.TryGetValue(o.Sku, out var campaign))
                {
                    // Judged against the campaign's own minimum margin: under it, our campaign discount on this SKU ends early.
                    var campaignFloor = VatMath.MinGrossPriceForMargin(o.Pptcv, campaign.MinMarginPct);
                    (facts["campaign"], facts["campaignPrice"], facts["campaignMinMarginPct"]) = (campaign.Key, campaign.CampaignPrice, campaign.MinMarginPct);
                    if (campaign.CampaignPrice >= campaignFloor - 0.005m)
                    {
                        Done(o, "campaign");
                        none++;
                    }
                    else if (testRun)
                        TestRun(o, band.BandId, category, campaign.MinMarginPct, "end-campaign-discount",
                            $"Cost {oldCost:0.00} -> {o.Pptcv:0.00}: the campaign price {campaign.CampaignPrice:0.00} is under the campaign's {campaign.MinMarginPct:0.#}% " +
                            $"minimum margin ({campaignFloor:0.00}): would end our campaign discount on it early.", facts);
                    else
                    {
                        var batch = await _publisher.EndCampaignDiscountAsync(o.LayerId, campaign.Key, o.Sku, ct);
                        facts["publishBatch"] = batch;
                        var d = Decision(o, band.BandId, category, campaign.MinMarginPct, "end-campaign-discount", DecisionStatus.Applied, "campaign-under-min-margin",
                            $"Cost {oldCost:0.00} -> {o.Pptcv:0.00}: the campaign price {campaign.CampaignPrice:0.00} is under the campaign's {campaign.MinMarginPct:0.#}% " +
                            $"minimum margin ({campaignFloor:0.00}). " + (batch is null ? "Nothing of ours was live to end." : "Our campaign discount on it was ended early; the regular price applies."), facts);
                        Done(o, batch is null ? "campaign-nothing-live" : "ended-campaign-discount", d, batch);
                        ended++;
                    }
                }
                else if (live >= floorPrice - 0.005m)
                {
                    Done(o, "above-floor");
                    none++;
                }
                else if (target > ceiling + 0.005m)
                {
                    // Even our discount removed, the price stays under the floor: the regular price must change.
                    if (testRun)
                    {
                        TestRun(o, band.BandId, category, floorPct, "end-our-discount",
                            $"Cost rose to {o.Pptcv:0.00}: the floor price {target:0.00} is above the shelf price {ceiling:0.00}: would end our discount, " +
                            "if it has one, and alert that the regular price needs changing.", facts);
                        continue;
                    }
                    var batch = await _publisher.EndOurDiscountAsync(o.LayerId, o.Sku, ct);
                    facts["publishBatch"] = batch;
                    var d = Decision(o, band.BandId, category, floorPct, batch is null ? "hold" : "end-our-discount", DecisionStatus.Held, "cost-above-shelf",
                        $"Cost rose to {o.Pptcv:0.00}: the floor price {target:0.00} is above the shelf price {ceiling:0.00}. " +
                        (batch is null ? "We have no discount on it; " : "Our discount was ended; ") + "the regular price needs changing.", facts);
                    Done(o, batch is null ? "held-cost-above-shelf" : "ended-our-discount", d, batch);
                    if (batch is null) held++; else ended++;
                }
                else
                {
                    var price = Math.Min(target, ceiling);
                    var shelf = s.OldPrice ?? 0m;
                    var underMinDiscount = shelf > 0 && 100m * (shelf - price) / shelf < _minDisplayedDiscountPct;
                    if (testRun)
                    {
                        TestRun(o, band.BandId, category, floorPct, underMinDiscount ? "end-our-discount" : "raise-to-floor",
                            $"Cost {oldCost:0.00} -> {o.Pptcv:0.00} (+{risePct:0.#}%) put the live price {live:0.00} under the {floorPct:0.##}% floor ({floorPrice:0.00}): " +
                            (underMinDiscount ? $"would end the discount, so the regular price {shelf:0.00} applies." : $"would raise it to {price:0.00}."), facts);
                        continue;
                    }
                    if (underMinDiscount)
                    {
                        // The engine never shows a discount under the minimum (it holds such a price at the full shelf price):
                        // end ours instead, so the regular price, above the floor, applies.
                        var endBatch = await _publisher.EndOurDiscountAsync(o.LayerId, o.Sku, ct);
                        facts["publishBatch"] = endBatch;
                        var why = $"Cost {oldCost:0.00} -> {o.Pptcv:0.00}: the floor price {price:0.00} would be under {_minDisplayedDiscountPct:0.#}% off the shelf price {shelf:0.00}, " +
                                  "which the engine holds at the full price.";
                        if (endBatch is not null)
                        {
                            var d = Decision(o, band.BandId, category, floorPct, "end-our-discount", DecisionStatus.Applied, "min-discount", why + " Our discount was ended; the regular price applies.", facts);
                            Done(o, "ended-our-discount", d, endBatch);
                            ended++;
                        }
                        else if (!_options.EndTeamDeals)
                        {
                            var d = Decision(o, band.BandId, category, floorPct, "hold", DecisionStatus.Held, "team-discount",
                                why + " The low price comes from a discount we didn't create: a person decides.", facts);
                            Done(o, "held-team-discount", d);
                            held++;
                        }
                        if (endBatch is not null || !_options.EndTeamDeals) continue;
                        // The low price is a deal we didn't create: publish the regular price itself, which ends that deal.
                        price = Math.Min(shelf, ceiling);
                    }
                    var (batchId, status, leftOut) = await _publisher.RaiseAsync(o.LayerId, o.Sku, price, ceiling, current, s.OldPrice ?? current, ct);
                    facts["publishBatch"] = batchId;
                    facts["publishStatus"] = status;
                    if (leftOut is not null) facts["leftOut"] = leftOut;
                    if (status == "Published" && leftOut is null)
                    {
                        facts["publishedPrice"] = price;
                        var endedDeals = await _db.PublishAudits.AsNoTracking().Where(a => a.BatchId == batchId && a.Change == "DiscountSuperseded")
                            .Select(a => new { a.DiscountId, a.DiscountName, a.OldDiscountAmount }).ToListAsync(ct);
                        if (endedDeals.Count > 0) facts["endedTeamDeals"] = endedDeals.Select(e => $"#{e.DiscountId} {e.DiscountName} at {e.OldDiscountAmount:0.00}").ToList();
                        var dealsText = string.Join(", ", endedDeals.Select(e => $"'{e.DiscountName}' at {e.OldDiscountAmount:0.00}"));
                        var d = Decision(o, band.BandId, category, floorPct, endedDeals.Count > 0 ? "end-team-deal" : "raise-to-floor", DecisionStatus.Applied, null,
                            $"Cost {oldCost:0.00} -> {o.Pptcv:0.00} (+{risePct:0.#}%) put the live price {live:0.00} under the {floorPct:0.##}% floor ({floorPrice:0.00}): " +
                            (endedDeals.Count > 0 ? $"ended the team discount {dealsText} that sold under it; the price is {price:0.00}." : $"raised to {price:0.00}."), facts);
                        Done(o, endedDeals.Count > 0 ? "ended-team-deal" : "raised", d, batchId);
                        raised++;
                    }
                    else if (status == "Stopped")
                    {
                        _logger.LogInformation("Cost watcher: publishing stopped for {Sku}; the observation waits.", o.Sku);
                        continue;                                                    // a switch stopped publishing: try again next watch
                    }
                    else if (leftOut is "own-push" or "raised-under-own-push")
                    {
                        var d = Decision(o, band.BandId, category, floorPct, "hold", DecisionStatus.Held, "own-push",
                            $"Cost {oldCost:0.00} -> {o.Pptcv:0.00} put the live price {live:0.00} under the floor ({floorPrice:0.00}), but the cheaper price is the " +
                            "calculator's own earlier push, imported by the team: it stays until it ends or a person supersedes it.", facts);
                        Done(o, "held-own-push", d, batchId);
                        held++;
                    }
                    else if (leftOut == "protected-deal")
                    {
                        var d = Decision(o, band.BandId, category, floorPct, "hold", DecisionStatus.Held, "protected-deal",
                            $"Cost {oldCost:0.00} -> {o.Pptcv:0.00} put the live price {live:0.00} under the floor ({floorPrice:0.00}); a cheaper team discount wins and " +
                            "a rule given in the chat protects it, so it stays: the shopper pays under the floor until it ends.", facts);
                        Done(o, "held-protected-deal", d, batchId);
                        held++;
                    }
                    else if (leftOut is not ("cheaper-deal" or "raised-under-team-deal"))
                    {
                        var d = Decision(o, band.BandId, category, floorPct, "hold", DecisionStatus.Held, leftOut ?? "not-published",
                            $"Cost {oldCost:0.00} -> {o.Pptcv:0.00} put the live price {live:0.00} under the floor ({floorPrice:0.00}), but {price:0.00} didn't stand " +
                            $"({leftOut ?? status}): a person looks.", facts);
                        Done(o, "held-not-published", d, batchId);
                        held++;
                    }
                    else
                    {
                        var d = Decision(o, band.BandId, category, floorPct, "hold", DecisionStatus.Held, "team-discount",
                            $"Cost {oldCost:0.00} -> {o.Pptcv:0.00} put the live price {live:0.00} under the floor ({floorPrice:0.00}), but a cheaper discount we didn't create wins: a person decides.", facts);
                        Done(o, "held-team-discount", d, batchId);
                        held++;
                    }
                }
            }
        }
        await _db.SaveChangesAsync(CancellationToken.None);
        foreach (var p in passes.Values)
        {
            var decisions = await _db.AgentDecisions.Where(d => d.PassId == p.Id).ToListAsync(ct);
            p.Applied = decisions.Count(d => d.Status == DecisionStatus.Applied);
            p.Held = decisions.Count(d => d.Status == DecisionStatus.Held);
            p.Status = "Succeeded";
            p.FinishedUtc = _clock.GetUtcNow().UtcDateTime;
            p.Summary = $"{p.Applied} raised to the floor, {p.Held} held for a person.";
        }
        await _db.SaveChangesAsync(CancellationToken.None);
        _logger.LogInformation("Cost watcher: {Observed} observations, {Raised} raised, {Held} held, {Ended} discounts ended", pending.Count, raised, held, ended);
        return new CostWatchResult(pending.Count, raised, held, ended, none, null);

        void Done(CostObservation o, string outcome, AgentDecision? d = null, long? batch = null)
        {
            o.ProcessedUtc = _clock.GetUtcNow().UtcDateTime;
            o.Outcome = outcome;
            o.PublishBatchId = batch;
            if (d is not null) o.DecisionId = d.Id;
        }

        // A test run decides as a real watch would, then holds: nothing is published.
        void TestRun(CostObservation o, int bandId, string category, decimal floorPct, string action, string reason, Dictionary<string, object?> facts)
        {
            facts["testRun"] = true;
            var d = Decision(o, bandId, category, floorPct, action, DecisionStatus.Held, "test-run", reason + " A test run (--test) publishes nothing.", facts);
            Done(o, "test-run-not-published", d);
            held++;
        }

        AgentDecision Decision(CostObservation o, int bandId, string category, decimal floorPct, string action, string status, string? limit, string reason,
            Dictionary<string, object?> facts)
        {
            if (!passes.TryGetValue(o.LayerId, out var pass))
            {
                pass = new AgentPass { LayerId = o.LayerId, Source = Source, Mode = "Code", StartedUtc = now, Status = "Running",
                    IsTest = o.Source.StartsWith("[INJECTED-TEST]", StringComparison.Ordinal) };
                _db.AgentPasses.Add(pass);
                _db.SaveChanges();
                passes[o.LayerId] = pass;
            }
            var d = new AgentDecision
            {
                PassId = pass.Id, LayerId = o.LayerId, BandId = bandId, Category = category, Source = Source, Action = action,
                CurrentFloorPct = floorPct, ProposedFloorPct = floorPct, Status = status, Limit = limit, Reason = reason,
                EvidenceRefs = JsonSerializer.Serialize(facts, Json), CreatedUtc = now, AppliedUtc = status == DecisionStatus.Applied ? now : null,
            };
            _db.AgentDecisions.Add(d);
            _db.SaveChanges();
            return d;
        }
    }
}
