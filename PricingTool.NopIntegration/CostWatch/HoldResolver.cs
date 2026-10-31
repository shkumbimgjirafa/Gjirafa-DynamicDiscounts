using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Operations;
using PricingTool.Agent.Storage;
using PricingTool.Data;
using PricingTool.Data.Entities;

namespace PricingTool.NopIntegration.CostWatch;

/// <summary>
/// Settles the cost watcher's holds that code can settle, so only the rest reaches a person (the user's decision, 31 Oct).
/// For every held SKU it reads what a shopper pays now: back at or above the hold's floor price (the deal ended, the price
/// moved), the hold is closed. Still under it because of a cheaper team discount, it republishes the current run's price for
/// the SKU (the engine's price, on the new cost, charm-rounded, at or above the floor) through the cost watcher's publisher,
/// which ends that discount unless a chat rule protects it; audited, rolled back with its batch. A held cost jump Alpha confirmed
/// as real is republished the same way. Nothing else is touched: a cost above the shelf, or a protected deal, stays held.
/// </summary>
public sealed class HoldResolver : IHoldResolver
{
    private const string Source = "CostWatcher";
    private readonly AgentDbContext _db;
    private readonly PricingToolDbContext _tool;
    private readonly NopDiscountWriter _writer;
    private readonly ICostPublisher _publisher;
    private readonly CostWatcherOptions _options;
    private readonly ILogger<HoldResolver> _logger;
    private readonly TimeProvider _clock;

    public HoldResolver(AgentDbContext db, PricingToolDbContext tool, NopDiscountWriter writer, ICostPublisher publisher, IOptions<CostWatcherOptions> options,
        ILogger<HoldResolver> logger, TimeProvider? clock = null)
    {
        _db = db;
        _tool = tool;
        _writer = writer;
        _publisher = publisher;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public Task<HoldReview> ReviewAsync(CancellationToken ct = default) => ReviewCoreAsync(null, ct);

    public Task<HoldReview> ConfirmRealRiseAsync(IReadOnlyCollection<long> decisionIds, CancellationToken ct = default) => ReviewCoreAsync(decisionIds, ct);

    private async Task<HoldReview> ReviewCoreAsync(IReadOnlyCollection<long>? confirmed, CancellationToken ct)
    {
        var held = await _db.AgentDecisions
            .Where(d => d.Source == Source && d.Status == DecisionStatus.Held && _db.AgentPasses.Any(p => p.Id == d.PassId && !p.IsTest))
            .ToListAsync(ct);
        if (confirmed is not null) held = held.Where(d => confirmed.Contains(d.Id)).ToList();
        int resolved = 0, fixedSkus = 0, still = 0;
        var lines = new List<string>();
        var passes = new Dictionary<int, AgentPass>();

        foreach (var layer in held.GroupBy(d => d.LayerId))
        {
            var groups = layer.GroupBy(d => CostHoldGroup.SkuOf(d), StringComparer.OrdinalIgnoreCase).ToList();
            var skus = groups.Select(g => g.Key).ToList();
            var shelf = await _writer.ShelfNowAsync(layer.Key, skus, ct);
            var run = await _tool.PricingRuns.AsNoTracking()
                .Where(r => r.LayerId == layer.Key && (r.Status == RunStatus.Succeeded || r.Status == RunStatus.SucceededWithErrors)
                            && _tool.ProposedPrices.Any(p => p.PricingRunId == r.Id))
                .OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);
            var proposals = run is null ? new Dictionary<string, ProposedPrice>(StringComparer.OrdinalIgnoreCase)
                : (await _tool.ProposedPrices.AsNoTracking().Where(p => p.PricingRunId == run.Id && skus.Contains(p.Sku)).ToListAsync(ct))
                    .GroupBy(p => p.Sku, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var g in groups)
            {
                var latest = g.OrderByDescending(d => d.Id).First();
                var floorPrice = Number(latest.EvidenceRefs, "floorPrice");
                if (floorPrice is { } fp && shelf.TryGetValue(g.Key, out var now) && now.Shelf is { } paid && paid >= fp - 0.005m)
                {
                    Close(g, "Alpha (re-check)", $"Closed by the re-check: a shopper now pays {paid:0.00}, at or above the floor price {fp:0.00}" +
                                                 (now.Deal is null ? " (the regular price)." : $" ('{now.Deal}')."));
                    resolved++;
                    continue;
                }
                var mayFix = confirmed is not null
                    ? latest.Limit is "data-error-cost-jump" or "data-error"
                    : _options.EndTeamDeals && latest.Limit is "team-discount" or "own-push";
                if (!mayFix || floorPrice is null || !proposals.TryGetValue(g.Key, out var p) || p.ProposedPriceValue < floorPrice.Value - 0.005m)
                {
                    still++;
                    continue;
                }
                var ceiling = Math.Max(p.AnchorPrice, p.CurrentPrice);
                var (batchId, status, leftOut) = await _publisher.RaiseAsync(layer.Key, g.Key, p.ProposedPriceValue, ceiling, p.CurrentPrice, p.OldPrice, ct);
                if (status == "Published" && leftOut is null)
                {
                    var ended = await _db.PublishAudits.AsNoTracking().Where(a => a.BatchId == batchId && a.Change == "DiscountSuperseded")
                        .Select(a => $"'{a.DiscountName}' at {a.OldDiscountAmount:0.00}").ToListAsync(ct);
                    var what = (ended.Count > 0 ? $"ended the team discount {string.Join(", ", ended)}; " : "") +
                               $"the run's price {p.ProposedPriceValue:0.00} (floor price {floorPrice:0.00}) is on the shelf (batch #{batchId}).";
                    if (!passes.TryGetValue(layer.Key, out var pass)) passes[layer.Key] = pass = await PassAsync(layer.Key, ct);
                    _db.AgentDecisions.Add(new AgentDecision
                    {
                        PassId = pass.Id, LayerId = layer.Key, BandId = latest.BandId, Category = latest.Category, Source = Source,
                        Action = ended.Count > 0 ? "end-team-deal" : "raise-to-floor", CurrentFloorPct = latest.CurrentFloorPct, ProposedFloorPct = latest.ProposedFloorPct,
                        Status = DecisionStatus.Applied, Reason = Truncate($"{g.Key}: " + (confirmed is null ? "held after a cost rise; " : "Alpha confirmed the cost jump is real; ") + what, 2000)!,
                        EvidenceRefs = JsonSerializer.Serialize(new { sku = g.Key, holds = g.Select(d => d.Id), publishBatch = batchId, endedTeamDeals = ended, run = run!.Id }),
                        CreatedUtc = _clock.GetUtcNow().UtcDateTime, AppliedUtc = _clock.GetUtcNow().UtcDateTime,
                    });
                    Close(g, confirmed is null ? "Alpha (ended team deal)" : "Alpha (confirmed real rise)", "Fixed: " + what);
                    lines.Add($"{g.Key}: {what}");
                    fixedSkus++;
                }
                else
                {
                    if (leftOut == "protected-deal")
                        foreach (var d in g) (d.Limit, d.Reason) = ("protected-deal", Truncate(d.Reason + " A rule given in the chat protects the cheaper team discount, so it stays.", 2000)!);
                    lines.Add($"{g.Key}: still held ({leftOut ?? status}, batch #{batchId})");
                    still++;
                }
            }
        }
        foreach (var pass in passes.Values)
        {
            pass.Applied = _db.AgentDecisions.Local.Count(d => d.PassId == pass.Id);
            (pass.Status, pass.FinishedUtc, pass.Summary) = ("Succeeded", _clock.GetUtcNow().UtcDateTime,
                $"Re-check: {pass.Applied} SKUs fixed on this layer; {resolved} closed and {still} still held across the layers.");
        }
        await _db.SaveChangesAsync(CancellationToken.None);
        if (resolved + fixedSkus > 0)
            _logger.LogInformation("Hold review: {Resolved} closed, {Fixed} fixed, {Still} still held.", resolved, fixedSkus, still);
        return new HoldReview(resolved, fixedSkus, still, lines);
    }

    private void Close(IEnumerable<AgentDecision> holds, string by, string note)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var d in holds)
            (d.Status, d.ReviewedBy, d.ReviewedUtc, d.ReportNote) = (DecisionStatus.Handled, by, now, Truncate(note, 1000));
    }

    private async Task<AgentPass> PassAsync(int layerId, CancellationToken ct)
    {
        var pass = new AgentPass { LayerId = layerId, Source = Source, Mode = "Re-check", StartedUtc = _clock.GetUtcNow().UtcDateTime, Status = "Running" };
        _db.AgentPasses.Add(pass);
        await _db.SaveChangesAsync(ct);
        return pass;
    }

    private static decimal? Number(string? json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? "");
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetDecimal() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
