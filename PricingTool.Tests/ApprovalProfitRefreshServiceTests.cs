using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PricingTool.Core.Abstractions;
using PricingTool.Core.Domain;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;

namespace PricingTool.Tests;

/// <summary>
/// The hourly approval-profit refresh: for each recent approval it folds the sales-report before/after
/// realized profit (7/30/90d) onto the Approval row and stamps ProfitRefreshedUtc, scoped to the layer's
/// (PlatformId, CompanyId) and the approval's own SKU set. Approvals past the tracking window are left static.
/// </summary>
public class ApprovalProfitRefreshServiceTests
{
    private static PricingToolDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PricingToolDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Layer SeedLayer(PricingToolDbContext db, int plat = 2, int comp = 5)
    {
        var layer = new Layer
        {
            Brand = "GjirafaMall", CountryCode = "KS", DisplayName = "GjirafaMall KS",
            OperationalDatabase = "GjirafaMall", StoreId = 2, TranslationCountryId = 1, WarehouseStoreId = 2,
            WmsWarehouseId = 1, Currency = "EUR", RunTimeUtc = "03:00", CadenceHours = 24,
            IsActive = true, SortOrder = 0, SrPlatformId = plat, SrCompanyId = comp,
        };
        db.Layers.Add(layer);
        db.SaveChanges();
        return layer;
    }

    private sealed class FakeProfitReader : ISalesReportProfitReader
    {
        public ApprovalProfitWindows Windows { get; set; } = new(0, 0, 0, 0, 0, 0);
        public (int Plat, int Comp, string OpDb, List<string> Skus, DateTime T)? LastCall;

        public Task<IReadOnlyList<WeeklyProfitPoint>> GetWeeklyLayerProfitAsync(
            int p, int c, string opDb, DateTime since, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WeeklyProfitPoint>>(Array.Empty<WeeklyProfitPoint>());

        public Task<ApprovalProfitWindows> GetApprovalProfitWindowsAsync(
            int p, int c, string opDb, IReadOnlyCollection<string> skus, DateTime t,
            CancellationToken ct = default)
        {
            LastCall = (p, c, opDb, skus.ToList(), t);
            return Task.FromResult(Windows);
        }
    }

    private static ApprovalProfitRefreshService Service(PricingToolDbContext db, ISalesReportProfitReader reader) =>
        new(db, reader, NullLogger<ApprovalProfitRefreshService>.Instance);

    [Fact]
    public async Task Refresh_RecentApproval_StoresBeforeAfterWindows_ScopedToLayerAndItsSkus()
    {
        using var db = NewDb();
        var layer = SeedLayer(db, plat: 2, comp: 5);
        var t = DateTime.UtcNow.Date.AddDays(-3);
        var approval = new Approval
        {
            LayerId = layer.Id, ApprovedUtc = t, SourceRunId = 10, ApprovedBy = "mgr",
            SkuCount = 2, IncreaseCount = 1, DecreaseCount = 1, CreatedUtc = t,
        };
        approval.Items.Add(new ApprovalItem { Sku = "A", OldPrice = 10m, NewPrice = 12m });
        approval.Items.Add(new ApprovalItem { Sku = "B", OldPrice = 20m, NewPrice = 18m });
        db.Approvals.Add(approval);
        db.SaveChanges();

        var reader = new FakeProfitReader { Windows = new(100m, 130m, 400m, 450m, 1200m, 1300m) };
        var refreshed = await Service(db, reader).RefreshTrackedApprovalsAsync();

        Assert.Equal(1, refreshed);
        Assert.Equal(2, reader.LastCall!.Value.Plat);          // the layer's SR scope, not defaults
        Assert.Equal(5, reader.LastCall.Value.Comp);
        Assert.Equal("GjirafaMall", reader.LastCall.Value.OpDb); // the layer's own shop DB, for the
                                                                 // authoritative cancelled-order check
        Assert.Equal(t, reader.LastCall.Value.T);              // anchored on the approval date
        Assert.Equal(new[] { "A", "B" }, reader.LastCall.Value.Skus.OrderBy(x => x).ToArray());

        var saved = await db.Approvals.AsNoTracking().SingleAsync();
        Assert.Equal(100m, saved.Profit7Before);
        Assert.Equal(130m, saved.Profit7After);
        Assert.Equal(400m, saved.Profit30Before);
        Assert.Equal(450m, saved.Profit30After);
        Assert.Equal(1200m, saved.Profit90Before);
        Assert.Equal(1300m, saved.Profit90After);
        Assert.NotNull(saved.ProfitRefreshedUtc);
    }

    [Fact]
    public async Task Refresh_ApprovalPastTrackingWindow_IsLeftStatic()
    {
        using var db = NewDb();
        var layer = SeedLayer(db);
        var old = new Approval
        {
            LayerId = layer.Id,
            ApprovedUtc = DateTime.UtcNow.Date.AddDays(-(ApprovalProfitRefreshService.RefreshWindowDays + 5)),
            SourceRunId = 1, ApprovedBy = "mgr", SkuCount = 1, CreatedUtc = DateTime.UtcNow,
        };
        old.Items.Add(new ApprovalItem { Sku = "A", OldPrice = 10m, NewPrice = 9m });
        db.Approvals.Add(old);
        db.SaveChanges();

        var reader = new FakeProfitReader { Windows = new(1, 1, 1, 1, 1, 1) };
        var refreshed = await Service(db, reader).RefreshTrackedApprovalsAsync();

        Assert.Equal(0, refreshed);
        Assert.Null(reader.LastCall);                          // reader never called for a matured approval
        var saved = await db.Approvals.AsNoTracking().SingleAsync();
        Assert.Null(saved.Profit7Before);                      // untouched
    }
}
