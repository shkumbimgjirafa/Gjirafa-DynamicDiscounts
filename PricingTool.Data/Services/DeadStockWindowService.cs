using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PricingTool.Data.Entities;

namespace PricingTool.Data.Services;

/// <summary>
/// Seals a dead-stock clearance window into a frozen <see cref="DeadStockWindowLog"/> when the NEXT batch
/// is approved. Rule: per layer, the newest <see cref="ApprovedBatch"/> (by PushedUtc) is the OPEN window;
/// any older batch that has no log yet is sealed — its final open/close/cleared figures (read off its
/// already-refreshed <see cref="ApprovedBatchItem"/>s, the same aggregates the dashboard shows) plus the
/// per-SKU detail of what moved or cleared. Runs on the hourly worker tick right after the sales refresh,
/// so a newly-approved batch is detected — and the prior window sealed with fresh figures — within an hour.
///
/// Idempotent: a batch is sealed at most once (guarded by the existence check here and a unique index on
/// <see cref="DeadStockWindowLog.ApprovedBatchId"/>). The newest batch is never sealed (it is still open).
/// </summary>
public class DeadStockWindowService
{
    private readonly PricingToolDbContext _db;
    private readonly ILogger<DeadStockWindowService> _logger;

    public DeadStockWindowService(PricingToolDbContext db, ILogger<DeadStockWindowService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Seals every superseded-but-unsealed clearance window across all layers. A failure on one
    /// batch is logged and never stops the rest. Returns the number of windows sealed.</summary>
    public async Task<int> SealSupersededWindowsAsync(CancellationToken ct = default)
    {
        if (_db.Database.IsRelational()) _db.Database.SetCommandTimeout(600);

        var batches = await _db.ApprovedBatches.AsNoTracking().ToListAsync(ct);
        if (batches.Count == 0) return 0;

        var alreadySealed = (await _db.DeadStockWindowLogs.AsNoTracking()
            .Select(l => l.ApprovedBatchId).ToListAsync(ct)).ToHashSet();

        var sealed_ = 0;
        foreach (var layerGroup in batches.GroupBy(b => b.LayerId))
        {
            // Newest by push is the open window; everything older is a candidate to seal.
            var ordered = layerGroup.OrderByDescending(b => b.PushedUtc).ThenByDescending(b => b.Id).ToList();
            if (ordered.Count < 2) continue; // only the open window exists — nothing to seal
            var newest = ordered[0];

            foreach (var older in ordered.Skip(1))
            {
                if (alreadySealed.Contains(older.Id)) continue;
                try
                {
                    await SealWindowAsync(older, newest, ct);
                    alreadySealed.Add(older.Id);
                    sealed_++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to seal dead-stock window for batch {Id} ({Label}).", older.Id, older.Label);
                }
            }
        }
        return sealed_;
    }

    /// <summary>Seals one window: aggregates the batch's items and writes the log + moved/cleared detail.</summary>
    private async Task SealWindowAsync(ApprovedBatch older, ApprovedBatch newest, CancellationToken ct)
    {
        var items = await _db.ApprovedBatchItems.AsNoTracking()
            .Where(i => i.ApprovedBatchId == older.Id).ToListAsync(ct);

        // A SKU has CLEARED when it had local stock at push and now has none. Matches the dashboard's
        // sold-out predicate (HomeController) exactly; CurrentStock is null until the first refresh.
        static bool IsCleared(ApprovedBatchItem i) => i.StockAtPush > 0 && i.CurrentStock == 0;

        var now = DateTime.UtcNow;
        var log = new DeadStockWindowLog
        {
            LayerId = older.LayerId,
            ApprovedBatchId = older.Id,
            BatchLabel = older.Label,
            WindowOpenedUtc = older.PushedUtc,
            WindowClosedUtc = newest.PushedUtc,
            WindowDays = Math.Max(0, (int)(newest.PushedUtc.Date - older.PushedUtc.Date).TotalDays),
            SupersededByBatchId = newest.Id,

            // Open — the batch as pushed (matches the "Approved batch" card).
            OpenSkuCount = items.Count,
            OpenStockUnits = items.Sum(i => (long)i.StockAtPush),
            OpenStockCost = items.Sum(i => (i.Pptcv ?? 0m) * i.StockAtPush),

            // Close — sold since push as of now (matches the "Sold since push" card).
            MovedSkuCount = items.Count(i => i.SoldUnits > 0),
            SoldUnits = items.Sum(i => (long)i.SoldUnits),
            SoldNet = items.Sum(i => i.SoldNet),
            ClearedSkuCount = items.Count(IsCleared),
            ClearedStockUnits = items.Where(IsCleared).Sum(i => (long)i.StockAtPush),
            ClearedStockCost = items.Where(IsCleared).Sum(i => (i.Pptcv ?? 0m) * i.StockAtPush),

            CreatedUtc = now,
            Notes = $"Auto-sealed when batch {newest.Id} was approved. Sold-since-push figures are cumulative "
                  + $"from {older.PushedUtc:yyyy-MM-dd} as of {now:yyyy-MM-dd HH:mm}Z.",
        };

        // Detail = the SKUs that moved or fully cleared during the window (the "cleared SKUs + value" list).
        log.Items = items
            .Where(i => i.SoldUnits > 0 || IsCleared(i))
            .OrderByDescending(i => i.SoldNet).ThenBy(i => i.Sku)
            .Select(i => new DeadStockWindowClearedItem
            {
                Sku = i.Sku,
                ProductName = i.ProductName,
                Brand = i.Brand,
                Band = i.Band,
                PushedPrice = i.PushedPrice,
                Pptcv = i.Pptcv,
                StockAtPush = i.StockAtPush,
                SoldUnits = i.SoldUnits,
                SoldNet = i.SoldNet,
                IsClearedOut = IsCleared(i),
            })
            .ToList();

        _db.DeadStockWindowLogs.Add(log);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Sealed dead-stock window for batch {Id} ({Label}): {Open} SKUs / {Units} units open, "
            + "{Moved} moved, {Cleared} cleared, {Net} net sold.",
            older.Id, older.Label, log.OpenSkuCount, log.OpenStockUnits, log.MovedSkuCount,
            log.ClearedSkuCount, log.SoldNet);
    }
}
