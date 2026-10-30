using Microsoft.EntityFrameworkCore;
using PricingTool.Core.Domain;
using PricingTool.Core.Services;
using PricingTool.Data.Entities;

namespace PricingTool.Data.Services;

/// <summary>The impact view for one layer, plus where its numbers came from.</summary>
public sealed record DiscountImpactReport(
    long RunId,
    DateTime RunStartedUtc,
    DateTime SnapshotDate,
    ImpactUnitsBasis Basis,
    bool SoldOnly,
    decimal VatRatePct,
    string Currency,
    DiscountImpactResult Result);

/// <summary>
/// Feeds the Price Bands tab's discount-optimization impact view: today's live prices from the layer's
/// latest successful run, units from that day's snapshot, run through
/// <see cref="DiscountOptimizationImpact"/>. Read-only — it never writes a proposal or changes a price.
/// With <c>soldOnly</c> it looks only at SKUs that sold in the last 30 days (the snapshot's Qty30, both
/// storefronts of the country — the same set the Target impact column prices), and the layer totals it is
/// compared against shrink to those SKUs too.
/// </summary>
public class DiscountOptimizationImpactService
{
    private readonly PricingToolDbContext _db;

    public DiscountOptimizationImpactService(PricingToolDbContext db) => _db = db;

    /// <summary>Null when the layer has no successful run (or no snapshot for it) to measure against.</summary>
    public async Task<DiscountImpactReport?> ComputeAsync(
        int layerId, IReadOnlyList<DiscountTrimRange> ranges, ImpactUnitsBasis basis, bool soldOnly,
        CancellationToken ct = default)
    {
        var layer = await _db.Layers.AsNoTracking()
            .Where(l => l.Id == layerId).Select(l => new { l.VatRatePct, l.Currency }).FirstOrDefaultAsync(ct);
        if (layer is null) return null;
        var vatRatePct = layer.VatRatePct;

        var run = await _db.PricingRuns.AsNoTracking()
            .Where(r => r.LayerId == layerId && r.Status == RunStatus.Succeeded)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);
        if (run is null) return null;

        var snapshotDate = await _db.DailySnapshots.AsNoTracking()
            .Where(s => s.LayerId == layerId && s.SnapshotDate <= run.StartedUtc)
            .MaxAsync(s => (DateTime?)s.SnapshotDate, ct);
        if (snapshotDate is not DateTime snapDate) return null;

        // These reads cover a whole run (~480k proposals on GjirafaMall KS) and a whole day's snapshot, so
        // each is bounded by its bulk-insert block (see BlockRanges) and given room beyond the 30s default.
        _db.Database.SetCommandTimeout(TimeSpan.FromSeconds(180));
        if (await BlockRanges.ProposalsAsync(_db, run.Id, ct) is not { } runIds) return null;
        if (await BlockRanges.SnapshotAsync(_db, layerId, snapDate, ct) is not { } snapIds) return null;

        var snaps = _db.DailySnapshots.AsNoTracking()
            .Where(s => s.Id >= snapIds.Min && s.Id <= snapIds.Max)
            .Where(s => s.LayerId == layerId && s.SnapshotDate == snapDate);

        // Only rows whose discount reaches the shallowest range that actually trims can move, so only those
        // are read — on GjirafaMall KS that is ~100k of ~480k rows. Ranges with a zero trim are reported as
        // "not trimmed", so their SKUs never need counting.
        var skus = new List<DiscountImpactSku>();
        if (ranges.Where(r => r.TrimPct > 0m).Select(r => (decimal?)r.MinDiscountPct).Min() is decimal shallowest)
        {
            var maxPriceOfOld = 1m - shallowest / 100m;
            var rows = await (
                from p in _db.ProposedPrices.AsNoTracking()
                where p.Id >= runIds.Min && p.Id <= runIds.Max
                      && p.PricingRunId == run.Id && p.Status != ProposalStatus.Skipped
                      && p.OldPrice > 0m && p.CurrentPrice > 0m && p.Pptcv != null
                      && p.CurrentPrice <= p.OldPrice * maxPriceOfOld
                join s in snaps on p.Sku equals s.Sku into sj
                from s in sj.DefaultIfEmpty()
                where !soldOnly || (s != null && s.Qty30 > 0)
                select new
                {
                    p.Sku, p.ProductName, p.OldPrice, p.CurrentPrice, p.AnchorPrice,
                    Cost = p.Pptcv!.Value,
                    p.DeadStockStage, p.GuardrailFlags, p.ReasonCodes, p.ActiveDiscountIds,
                    Units = s == null ? 0
                        : basis == ImpactUnitsBasis.Sold30Days ? s.Qty30
                        : basis == ImpactUnitsBasis.Sold90Days ? s.Qty90
                        : s.LocalWarehouseStock,
                })
                .ToListAsync(ct);

            foreach (var r in rows)
            {
                skus.Add(new DiscountImpactSku(
                    r.Sku, r.ProductName, r.OldPrice, r.CurrentPrice, r.AnchorPrice, r.Cost, r.Units,
                    IsExempt: PriceHolds.IsHeld(r.DeadStockStage, r.GuardrailFlags, r.ReasonCodes),
                    r.ActiveDiscountIds));
            }
        }

        // The layer it sits in, on the same units basis — the denominators for "% affected". Sold-only
        // compares against the SKUs that sold, so its SKU count comes from the snapshot rather than the run.
        var totals = await snaps
            .Where(s => s.CurrentPrice > 0m && s.Pptcv != null)
            .Where(s => !soldOnly || s.Qty30 > 0)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Skus = g.Count(),
                Revenue = g.Sum(s => s.CurrentPrice!.Value * (
                    basis == ImpactUnitsBasis.Sold30Days ? s.Qty30
                    : basis == ImpactUnitsBasis.Sold90Days ? s.Qty90
                    : s.LocalWarehouseStock > 0 ? s.LocalWarehouseStock : 0)),
                Gross = g.Sum(s => (s.CurrentPrice!.Value - s.Pptcv!.Value) * (
                    basis == ImpactUnitsBasis.Sold30Days ? s.Qty30
                    : basis == ImpactUnitsBasis.Sold90Days ? s.Qty90
                    : s.LocalWarehouseStock > 0 ? s.LocalWarehouseStock : 0)),
            })
            .FirstOrDefaultAsync(ct);

        var vat = 1m + vatRatePct / 100m;
        var result = DiscountOptimizationImpact.Compute(
            skus, ranges, vatRatePct,
            layerSkus: soldOnly ? totals?.Skus ?? 0 : run.ProposalCount - run.SkippedCount,
            layerRevenue: totals?.Revenue ?? 0m,
            layerProfit: (totals?.Gross ?? 0m) / vat);

        return new DiscountImpactReport(run.Id, run.StartedUtc, snapDate, basis, soldOnly, vatRatePct, layer.Currency, result);
    }

}
