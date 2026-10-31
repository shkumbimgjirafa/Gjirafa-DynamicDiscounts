using Microsoft.EntityFrameworkCore;
using PricingTool.Agent.Checker;
using PricingTool.Agent.Operations;
using PricingTool.Agent.Storage;
using PricingTool.Data;

namespace PricingTool.NopIntegration;

/// <summary>
/// The outcome tracker's rollback of raises (prompt 07): the regular prices this publisher raised since a decision was
/// applied, on the layer's SKUs of the decision's band × category (the band from the proposal the price came from, the
/// category from the product map, as for the holdout), go back to their Price and OldPrice from the publish audit
/// (<see cref="NopDiscountWriter.RestoreRegularPricesAsync"/>). A row someone changed since our raise is left alone. Rows we
/// inserted, and the ai-price discounts a raise ended, are left for a person's batch rollback.
/// </summary>
public sealed class NopRaiseRestorer : IRaiseRestorer
{
    private readonly AgentDbContext _db;
    private readonly PricingToolDbContext _tool;
    private readonly IHoldoutLookup _categories;
    private readonly NopDiscountWriter _writer;

    public NopRaiseRestorer(AgentDbContext db, PricingToolDbContext tool, IHoldoutLookup categories, NopDiscountWriter writer)
    {
        _db = db;
        _tool = tool;
        _categories = categories;
        _writer = writer;
    }

    public async Task<string> RestoreRaisesAsync(int layerId, int bandId, string category, DateTime sinceUtc, string reason, CancellationToken ct = default)
    {
        var raises = await _db.PublishAudits.AsNoTracking()
            .Where(a => a.Change == "TierPriceUpdated" && a.Applied && a.RolledBackUtc == null && a.CreatedUtc >= sinceUtc && a.ProposalId != null
                        && _db.PublishBatches.Any(b => b.Id == a.BatchId && b.LayerId == layerId && b.Kind == PublishKinds.Regular))
            .Select(a => new { a.Id, ProposalId = a.ProposalId!.Value }).ToListAsync(ct);
        if (raises.Count == 0) return $"No regular price was raised there since {sinceUtc:yyyy-MM-dd}.";

        var inBand = new List<(long Id, long RunId)>();
        foreach (var ids in raises.Select(r => r.ProposalId).Distinct().Chunk(2000))
            inBand.AddRange((await _tool.ProposedPrices.AsNoTracking().Where(p => ids.Contains(p.Id) && p.PriceBandId == bandId)
                .Select(p => new { p.Id, p.PricingRunId }).ToListAsync(ct)).Select(p => (p.Id, p.PricingRunId)));
        var inScope = new HashSet<long>();
        foreach (var run in inBand.GroupBy(p => p.RunId))
        {
            var inCategory = await _categories.HoldoutProposalIdsAsync(run.Key, layerId, new[] { category }, ct);
            inScope.UnionWith(run.Select(p => p.Id).Where(inCategory.Contains));
        }
        var selected = raises.Where(r => inScope.Contains(r.ProposalId)).Select(r => r.Id).ToList();
        if (selected.Count == 0) return $"No regular price was raised in band {bandId} × {category} since {sinceUtc:yyyy-MM-dd}.";

        var (restored, left, batch) = await _writer.RestoreRegularPricesAsync(layerId, selected, reason, ct);
        return $"Regular prices raised in band {bandId} × {category} since {sinceUtc:yyyy-MM-dd}: {restored} restored from the publish audit" +
               (left > 0 ? $", {left} left alone (changed since our raise)" : "") + $" (batch #{batch}).";
    }
}
