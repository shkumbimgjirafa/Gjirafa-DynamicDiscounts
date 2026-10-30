using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PricingTool.Core.Abstractions;
using PricingTool.Core.Domain;
using PricingTool.Data.Entities;

namespace PricingTool.Data.Services;

/// <summary>
/// Refreshes each recently-pushed approved clearance batch from the live source: units + net sold since
/// the push (both platforms of the country) and each SKU's current local stock, written onto
/// ApprovedBatchItems plus the batch's SalesRefreshedUtc. Runs hourly (see the Engine's
/// ApprovedBatchRefreshWorker) so the dead-stock "is it clearing?" dashboard — and its sold-out list —
/// stay current without an external job. Scoped by push date (the dashboard-tracking window), NOT by
/// <c>ApprovedBatch.IsActive</c>: that flag governs clearance-LOCK enforcement (pinning prices), which is
/// orthogonal to keeping the dashboard's sold-since-push figures fresh.
/// </summary>
public class ApprovedBatchRefreshService
{
    /// <summary>Batches pushed within this many days are still tracked on the dashboard and refreshed;
    /// older ones are treated as closed and left static (their sales window has long since matured).</summary>
    public const int RefreshWindowDays = 45;

    private readonly PricingToolDbContext _db;
    private readonly ISourceDataReader _reader;
    private readonly ILogger<ApprovedBatchRefreshService> _logger;

    public ApprovedBatchRefreshService(PricingToolDbContext db, ISourceDataReader reader, ILogger<ApprovedBatchRefreshService> logger)
    {
        _db = db;
        _reader = reader;
        _logger = logger;
    }

    /// <summary>Refreshes every batch pushed within the tracking window (see <see cref="RefreshWindowDays"/>),
    /// regardless of IsActive; a failure on one batch is logged and never stops the rest. Returns the number
    /// of batches successfully refreshed.</summary>
    public async Task<int> RefreshTrackedBatchesAsync(CancellationToken ct = default)
    {
        if (_db.Database.IsRelational()) _db.Database.SetCommandTimeout(600);

        var cutoff = DateTime.UtcNow.Date.AddDays(-RefreshWindowDays);
        var batches = await _db.ApprovedBatches.Where(b => b.PushedUtc >= cutoff).OrderBy(b => b.Id).ToListAsync(ct);
        var refreshed = 0;
        foreach (var batch in batches)
        {
            try
            {
                await RefreshBatchAsync(batch, ct);
                refreshed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Approved-batch refresh failed for batch {Id} ({Label}).", batch.Id, batch.Label);
            }
        }
        return refreshed;
    }

    /// <summary>Refreshes one batch's items (sold-since-push + current stock) and stamps SalesRefreshedUtc.</summary>
    public async Task RefreshBatchAsync(ApprovedBatch batch, CancellationToken ct = default)
    {
        var layer = await _db.Layers.AsNoTracking().FirstOrDefaultAsync(l => l.Id == batch.LayerId, ct);
        if (layer is null)
        {
            _logger.LogWarning("Approved-batch refresh: layer {LayerId} for batch {Id} not found; skipped.", batch.LayerId, batch.Id);
            return;
        }

        // The sibling platform shares this country's warehouse, so its sales fold into "sold since push".
        var sibling = await _db.Layers.AsNoTracking()
            .FirstOrDefaultAsync(l => l.CountryCode == layer.CountryCode && l.Id != layer.Id, ct);

        var items = await _db.ApprovedBatchItems.Where(i => i.ApprovedBatchId == batch.Id).ToListAsync(ct);
        if (items.Count == 0) return;

        // SKU -> the price we pushed for it. The source query keeps only live discounts priced at this value,
        // so the sold-out export can drop SKUs whose pushed promo is no longer live (nothing of ours to delete).
        // Min on the (rare) intra-batch duplicate SKU mirrors the clearance-lock's price resolution
        // (PricingRunOrchestrator pins at the lowest pushed price), so the match sees the same value.
        var skuPushedPrices = items
            .Where(i => !string.IsNullOrWhiteSpace(i.Sku))
            .GroupBy(i => i.Sku, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Min(i => i.PushedPrice), StringComparer.OrdinalIgnoreCase);

        var results = await _reader.GetApprovedBatchSalesAsync(BuildContext(layer, sibling), batch.PushedUtc, skuPushedPrices, ct);
        var bySku = results.ToDictionary(r => r.Sku, StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            // Every requested SKU is returned by the query; a miss (empty/renamed SKU) reads as cleared.
            if (bySku.TryGetValue(item.Sku, out var r))
            {
                item.SoldUnits = r.SoldUnits;
                item.SoldNet = r.SoldNet;
                item.CompletedSoldUnits = r.CompletedSoldUnits;
                item.CurrentStock = r.CurrentStock;
                item.ActiveDiscountIds = r.ActiveDiscountIds;
            }
            else
            {
                item.SoldUnits = 0;
                item.SoldNet = 0m;
                item.CompletedSoldUnits = 0;
                item.CurrentStock = 0;
                item.ActiveDiscountIds = null;
            }
        }

        batch.SalesRefreshedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Approved-batch {Id} ({Label}) refreshed: {Items} SKUs, {Sold} with sales, {SoldOut} sold out.",
            batch.Id, batch.Label, items.Count, items.Count(i => i.SoldUnits > 0),
            items.Count(i => i.StockAtPush > 0 && i.CurrentStock == 0));
    }

    private static LayerSourceContext BuildContext(Layer layer, Layer? sibling) => new()
    {
        OperationalDatabase = layer.OperationalDatabase,
        StoreId = layer.StoreId,
        SiblingOperationalDatabase = sibling?.OperationalDatabase,
        SiblingStoreId = sibling?.StoreId ?? -1,
        TranslationCountryId = layer.TranslationCountryId,
        WarehouseStoreId = layer.WarehouseStoreId,
        WmsWarehouseId = layer.WmsWarehouseId,
        FilterVendors = layer.FilterVendors,
        ExcludeUnpublished = layer.ExcludeUnpublished,
    };
}
