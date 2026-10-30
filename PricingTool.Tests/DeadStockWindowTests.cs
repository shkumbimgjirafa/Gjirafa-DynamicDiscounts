using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;

namespace PricingTool.Tests;

/// <summary>
/// Sealing a dead-stock clearance window into a frozen <see cref="DeadStockWindowLog"/> when the next batch
/// is approved: the newest batch per layer is the OPEN window, any older unsealed batch is sealed once with
/// its final open/close/cleared aggregates (matching the dashboard) plus the moved/cleared per-SKU detail.
/// </summary>
public class DeadStockWindowTests
{
    private static PricingToolDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PricingToolDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Layer SeedLayer(PricingToolDbContext db, string brand = "GjirafaMall")
    {
        var layer = new Layer
        {
            Brand = brand, CountryCode = "KS", DisplayName = $"{brand} KS",
            OperationalDatabase = brand, StoreId = 2, TranslationCountryId = 1, WarehouseStoreId = 2,
            WmsWarehouseId = 1, Currency = "EUR", RunTimeUtc = "03:00", CadenceHours = 24, IsActive = true,
        };
        db.Layers.Add(layer);
        db.SaveChanges();
        return layer;
    }

    private static DeadStockWindowService Service(PricingToolDbContext db) =>
        new(db, NullLogger<DeadStockWindowService>.Instance);

    [Fact]
    public async Task SingleBatch_IsTheOpenWindow_NothingSealed()
    {
        using var db = NewDb();
        var layer = SeedLayer(db);
        var batch = new ApprovedBatch { LayerId = layer.Id, Label = "only batch", PushedUtc = DateTime.UtcNow.Date.AddDays(-20) };
        batch.Items.Add(new ApprovedBatchItem { Sku = "A", StockAtPush = 5, PushedPrice = 10m, SoldUnits = 2, SoldNet = 15m, CurrentStock = 0 });
        db.ApprovedBatches.Add(batch);
        db.SaveChanges();

        var sealed_ = await Service(db).SealSupersededWindowsAsync();

        Assert.Equal(0, sealed_);
        Assert.Empty(await db.DeadStockWindowLogs.ToListAsync());
    }

    [Fact]
    public async Task SupersededBatch_IsSealed_WithDashboardAggregatesAndDetail()
    {
        using var db = NewDb();
        var layer = SeedLayer(db);

        var older = new ApprovedBatch { LayerId = layer.Id, Label = "window 1", PushedUtc = DateTime.UtcNow.Date.AddDays(-15) };
        older.Items.Add(new ApprovedBatchItem { Sku = "A", StockAtPush = 10, Pptcv = 5m, PushedPrice = 12.99m, SoldUnits = 10, SoldNet = 100m, CurrentStock = 0 }); // moved + cleared
        older.Items.Add(new ApprovedBatchItem { Sku = "B", StockAtPush = 4, Pptcv = 20m, PushedPrice = 40m, SoldUnits = 1, SoldNet = 35m, CurrentStock = 3 });  // moved, not cleared
        older.Items.Add(new ApprovedBatchItem { Sku = "C", StockAtPush = 6, Pptcv = 10m, PushedPrice = 25m, SoldUnits = 0, SoldNet = 0m, CurrentStock = 6 });   // neither
        older.Items.Add(new ApprovedBatchItem { Sku = "D", StockAtPush = 2, Pptcv = 15m, PushedPrice = 30m, SoldUnits = 0, SoldNet = 0m, CurrentStock = 0 });   // cleared without a recorded sale
        db.ApprovedBatches.Add(older);

        var newer = new ApprovedBatch { LayerId = layer.Id, Label = "window 2", PushedUtc = DateTime.UtcNow.Date.AddDays(-1) };
        newer.Items.Add(new ApprovedBatchItem { Sku = "E", StockAtPush = 5, PushedPrice = 9m });
        db.ApprovedBatches.Add(newer);
        db.SaveChanges();

        var sealed_ = await Service(db).SealSupersededWindowsAsync();
        Assert.Equal(1, sealed_);

        var log = await db.DeadStockWindowLogs.SingleAsync();
        // Window bounds: opened at the older push, closed when the newer batch was approved.
        Assert.Equal(older.Id, log.ApprovedBatchId);
        Assert.Equal(newer.Id, log.SupersededByBatchId);
        Assert.Equal(older.PushedUtc, log.WindowOpenedUtc);
        Assert.Equal(newer.PushedUtc, log.WindowClosedUtc);
        Assert.Equal(14, log.WindowDays);
        Assert.Equal("window 1", log.BatchLabel);

        // Open (the "Approved batch" card).
        Assert.Equal(4, log.OpenSkuCount);
        Assert.Equal(22L, log.OpenStockUnits);              // 10+4+6+2
        Assert.Equal(220m, log.OpenStockCost);              // 5*10 + 20*4 + 10*6 + 15*2

        // Close (the "Sold since push" card).
        Assert.Equal(2, log.MovedSkuCount);                 // A, B
        Assert.Equal(11L, log.SoldUnits);                   // 10+1
        Assert.Equal(135m, log.SoldNet);                    // 100+35

        // Cleared = had stock at push, now zero (A, D — D even though it recorded no sale).
        Assert.Equal(2, log.ClearedSkuCount);
        Assert.Equal(12L, log.ClearedStockUnits);           // 10+2
        Assert.Equal(80m, log.ClearedStockCost);            // 5*10 + 15*2

        // Detail = moved OR cleared (A, B, D — not C), ordered by net sold desc.
        var items = await db.DeadStockWindowClearedItems
            .Where(i => i.DeadStockWindowLogId == log.Id).OrderByDescending(i => i.SoldNet).ToListAsync();
        Assert.Equal(new[] { "A", "B", "D" }, items.Select(i => i.Sku).ToArray());
        Assert.True(items.Single(i => i.Sku == "A").IsClearedOut);
        Assert.False(items.Single(i => i.Sku == "B").IsClearedOut);
        Assert.True(items.Single(i => i.Sku == "D").IsClearedOut);
        Assert.Equal(100m, items.Single(i => i.Sku == "A").SoldNet);

        // The newest batch stays OPEN — never sealed.
        Assert.False(await db.DeadStockWindowLogs.AnyAsync(l => l.ApprovedBatchId == newer.Id));
    }

    [Fact]
    public async Task Seal_IsIdempotent_AcrossRepeatedTicks()
    {
        using var db = NewDb();
        var layer = SeedLayer(db);
        var older = new ApprovedBatch { LayerId = layer.Id, Label = "w1", PushedUtc = DateTime.UtcNow.Date.AddDays(-15) };
        older.Items.Add(new ApprovedBatchItem { Sku = "A", StockAtPush = 5, Pptcv = 4m, PushedPrice = 10m, SoldUnits = 5, SoldNet = 50m, CurrentStock = 0 });
        db.ApprovedBatches.Add(older);
        db.ApprovedBatches.Add(new ApprovedBatch { LayerId = layer.Id, Label = "w2", PushedUtc = DateTime.UtcNow.Date });
        db.SaveChanges();

        var svc = Service(db);
        Assert.Equal(1, await svc.SealSupersededWindowsAsync()); // first tick seals
        Assert.Equal(0, await svc.SealSupersededWindowsAsync()); // second tick is a no-op

        Assert.Equal(1, await db.DeadStockWindowLogs.CountAsync());
        Assert.Equal(1, await db.DeadStockWindowClearedItems.CountAsync());
    }

    [Fact]
    public async Task Seal_IsScopedPerLayer()
    {
        using var db = NewDb();
        var mall = SeedLayer(db, "GjirafaMall");
        var g50 = SeedLayer(db, "Gjirafa50");
        // Mall has a superseded window; Gjirafa50 has only its open window.
        var mallOld = new ApprovedBatch { LayerId = mall.Id, Label = "mall w1", PushedUtc = DateTime.UtcNow.Date.AddDays(-15) };
        mallOld.Items.Add(new ApprovedBatchItem { Sku = "A", StockAtPush = 3, PushedPrice = 10m, SoldUnits = 1, SoldNet = 8m, CurrentStock = 2 });
        db.ApprovedBatches.Add(mallOld);
        db.ApprovedBatches.Add(new ApprovedBatch { LayerId = mall.Id, Label = "mall w2", PushedUtc = DateTime.UtcNow.Date });
        db.ApprovedBatches.Add(new ApprovedBatch { LayerId = g50.Id, Label = "g50 only", PushedUtc = DateTime.UtcNow.Date.AddDays(-15) });
        db.SaveChanges();

        Assert.Equal(1, await Service(db).SealSupersededWindowsAsync());
        var log = await db.DeadStockWindowLogs.SingleAsync();
        Assert.Equal(mall.Id, log.LayerId);
        Assert.Equal(mallOld.Id, log.ApprovedBatchId);
    }
}
