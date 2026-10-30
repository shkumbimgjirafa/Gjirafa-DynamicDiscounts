using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PricingTool.Core.Domain;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;

namespace PricingTool.Tests;

/// <summary>
/// The hourly approved-batch refresh: it folds live-source sales-since-push + current stock onto the
/// batch's items (and stamps SalesRefreshedUtc), only for ACTIVE batches, spanning both platforms of the
/// country — the data behind the "sold since push" tiles and the sold-out list.
/// </summary>
public class ApprovedBatchRefreshTests
{
    private static PricingToolDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PricingToolDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Layer SeedLayer(PricingToolDbContext db, string brand = "GjirafaMall", string opDb = "GjirafaMall",
        int storeId = 2, int sortOrder = 0)
    {
        var layer = new Layer
        {
            Brand = brand, CountryCode = "KS", DisplayName = $"{brand} KS",
            OperationalDatabase = opDb, StoreId = storeId, TranslationCountryId = 1, WarehouseStoreId = 2,
            WmsWarehouseId = 1, Currency = "EUR", RunTimeUtc = "03:00", CadenceHours = 24,
            IsActive = true, SortOrder = sortOrder,
        };
        db.Layers.Add(layer);
        db.SaveChanges();
        return layer;
    }

    private static ApprovedBatchRefreshService Service(PricingToolDbContext db, FakeReader reader) =>
        new(db, reader, NullLogger<ApprovedBatchRefreshService>.Instance);

    [Fact]
    public async Task Refresh_RecentBatch_WritesSoldAndCurrentStock_RegardlessOfIsActive()
    {
        using var db = NewDb();
        var layer = SeedLayer(db);
        // IsActive = false (the clearance lock is off) — refresh is driven by the tracking window, not the
        // lock flag, so a recently-pushed dashboard batch still refreshes.
        var batch = new ApprovedBatch { LayerId = layer.Id, Label = "Dead-stock clearance", IsActive = false, PushedUtc = DateTime.UtcNow.Date.AddDays(-3) };
        batch.Items.Add(new ApprovedBatchItem { Sku = "A", StockAtPush = 5, PushedPrice = 12.99m }); // clears (now 0)
        batch.Items.Add(new ApprovedBatchItem { Sku = "B", StockAtPush = 3, PushedPrice = 40.00m }); // still has stock
        db.ApprovedBatches.Add(batch);
        db.SaveChanges();

        var reader = new FakeReader(Array.Empty<SnapshotRow>());
        // ActiveDiscountIds is what the source returns AFTER the pushed-price match: A still carries its promo(s)
        // at 12.99; B has no live discount at its pushed price.
        reader.BatchSales.Add(new BatchSaleRow("A", 5, 100m, CompletedSoldUnits: 4, CurrentStock: 0, ActiveDiscountIds: "201 362"));
        reader.BatchSales.Add(new BatchSaleRow("B", 1, 20m, CompletedSoldUnits: 1, CurrentStock: 2, ActiveDiscountIds: null));

        var refreshed = await Service(db, reader).RefreshTrackedBatchesAsync();
        Assert.Equal(1, refreshed);

        // Each SKU's pushed price is forwarded to the reader — that's what the source uses to keep only
        // discounts priced at the pushed price.
        Assert.Equal(12.99m, reader.LastSkuPushedPrices!["A"]);
        Assert.Equal(40.00m, reader.LastSkuPushedPrices!["B"]);

        var items = await db.ApprovedBatchItems.OrderBy(i => i.Sku).ToListAsync();
        Assert.Equal(5, items[0].SoldUnits);
        Assert.Equal(4, items[0].CompletedSoldUnits);        // status-30 subset of the 5 sold
        Assert.Equal(100m, items[0].SoldNet);
        Assert.Equal(0, items[0].CurrentStock);              // A cleared
        Assert.Equal("201 362", items[0].ActiveDiscountIds); // pushed-price discount ids folded onto the item
        Assert.Equal(1, items[1].SoldUnits);
        Assert.Equal(1, items[1].CompletedSoldUnits);
        Assert.Equal(2, items[1].CurrentStock);              // B still stocked
        Assert.Null(items[1].ActiveDiscountIds);             // no live discount at the pushed price

        // Sold-out derivation (what the dashboard/CSV query on): only A qualifies.
        var soldOut = items.Where(i => i.StockAtPush > 0 && i.CurrentStock == 0).Select(i => i.Sku).ToList();
        Assert.Equal(new[] { "A" }, soldOut);

        Assert.NotNull((await db.ApprovedBatches.SingleAsync()).SalesRefreshedUtc);
    }

    [Fact]
    public async Task Refresh_OldBatchOutsideWindow_IsSkipped()
    {
        using var db = NewDb();
        var layer = SeedLayer(db);
        // Pushed well beyond the tracking window (even though IsActive = true) — its sales window has long
        // since matured, so it is left static.
        var batch = new ApprovedBatch { LayerId = layer.Id, Label = "old batch", IsActive = true, PushedUtc = DateTime.UtcNow.Date.AddDays(-(ApprovedBatchRefreshService.RefreshWindowDays + 15)) };
        batch.Items.Add(new ApprovedBatchItem { Sku = "A", StockAtPush = 5 });
        db.ApprovedBatches.Add(batch);
        db.SaveChanges();

        var reader = new FakeReader(Array.Empty<SnapshotRow>());
        reader.BatchSales.Add(new BatchSaleRow("A", 5, 100m, CompletedSoldUnits: 5, CurrentStock: 0, ActiveDiscountIds: "999"));

        var refreshed = await Service(db, reader).RefreshTrackedBatchesAsync();
        Assert.Equal(0, refreshed);

        var item = await db.ApprovedBatchItems.SingleAsync();
        Assert.Equal(0, item.SoldUnits);
        Assert.Equal(0, item.CompletedSoldUnits);            // never refreshed → stays at default
        Assert.Null(item.CurrentStock);
        Assert.Null((await db.ApprovedBatches.SingleAsync()).SalesRefreshedUtc);
    }

    [Fact]
    public async Task Refresh_WithSibling_PassesBothPlatformContext()
    {
        using var db = NewDb();
        var mall = SeedLayer(db, "GjirafaMall", "GjirafaMall", storeId: 2, sortOrder: 0);
        SeedLayer(db, "Gjirafa50", "GjirafaEcommerce", storeId: 7, sortOrder: 1); // same country → sibling
        var batch = new ApprovedBatch { LayerId = mall.Id, Label = "b", IsActive = false, PushedUtc = DateTime.UtcNow.Date.AddDays(-3) };
        batch.Items.Add(new ApprovedBatchItem { Sku = "A", StockAtPush = 5 });
        db.ApprovedBatches.Add(batch);
        db.SaveChanges();

        var reader = new FakeReader(Array.Empty<SnapshotRow>());
        reader.BatchSales.Add(new BatchSaleRow("A", 2, 40m, CompletedSoldUnits: 2, CurrentStock: 1, ActiveDiscountIds: "555"));
        await Service(db, reader).RefreshTrackedBatchesAsync();

        // The refresh must span both platforms of the country: sibling DB + store id wired into the query.
        Assert.Equal("GjirafaEcommerce", reader.LastContext!.SiblingOperationalDatabase);
        Assert.Equal(7, reader.LastContext!.SiblingStoreId);
    }
}
