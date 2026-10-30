using Microsoft.EntityFrameworkCore;
using PricingTool.Core.Options;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;

namespace PricingTool.Tests;

/// <summary>
/// "Match to KS" pulls a brand's Kosovo band target margins (plus an uplift) and margin floors onto one of
/// its MK/AL sibling layers on demand. It matches bands by SortOrder (never MinPrice — those are in local
/// currency), copies only the two currency-agnostic numbers, and leaves the band boundaries, rounding and
/// dead-stock ladder untouched. It is the pull counterpart of <see cref="BandFloorMirrorService"/>.
/// </summary>
public class BandMatchTests
{
    private static PricingToolDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PricingToolDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<PricingToolDbContext> SeededDbAsync()
    {
        var db = NewDb();
        await DbSeeder.SeedCoreAsync(db, new PricingEngineOptions());
        return db;
    }

    private static Task<Layer> LayerAsync(PricingToolDbContext db, string brand, string country) =>
        db.Layers.SingleAsync(l => l.Brand == brand && l.CountryCode == country);

    private static Task<PriceBand> BandAsync(PricingToolDbContext db, int layerId, int sortOrder) =>
        db.PriceBands.SingleAsync(b => b.LayerId == layerId && b.SortOrder == sortOrder);

    /// <summary>Give the KS bands distinct, known target margins + floors so a match is observable.</summary>
    private static async Task SetKsAsync(PricingToolDbContext db, string brand)
    {
        var ks = await LayerAsync(db, brand, "KS");
        foreach (var band in await db.PriceBands.Where(b => b.LayerId == ks.Id).ToListAsync())
        {
            band.TargetMarginPct = 20m - band.SortOrder;   // 20,19,…,13
            band.MarginFloorPct = 10m - band.SortOrder;    // 10,9,…,3
        }
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("Gjirafa50", "MK")]
    [InlineData("Gjirafa50", "AL")]
    [InlineData("GjirafaMall", "MK")]
    [InlineData("GjirafaMall", "AL")]
    public async Task Match_CopiesKsTargetPlusUpliftAndFloor_ByPosition(string brand, string country)
    {
        using var db = await SeededDbAsync();
        await SetKsAsync(db, brand);
        var sibling = await LayerAsync(db, brand, country);

        var result = await new BandMatchService(db).MatchToPrimaryAsync(sibling.Id, 1.5m);
        await db.SaveChangesAsync();

        Assert.NotNull(result);
        Assert.Equal(8, result!.Changes.Count);
        for (var s = 0; s < 8; s++)
        {
            var band = await BandAsync(db, sibling.Id, s);
            Assert.Equal(20m - s + 1.5m, band.TargetMarginPct);   // KS target + uplift
            Assert.Equal(10m - s, band.MarginFloorPct);           // KS floor, no uplift
        }
    }

    [Fact]
    public async Task Match_UsesSortOrder_NotTheCurrencyScaledBoundaries()
    {
        using var db = await SeededDbAsync();
        await SetKsAsync(db, "Gjirafa50");
        var mk = await LayerAsync(db, "Gjirafa50", "MK");

        await new BandMatchService(db).MatchToPrimaryAsync(mk.Id, 0m);
        await db.SaveChangesAsync();

        // Top band (SortOrder 7) starts at 62,000 MKD — a MinPrice match would have found nothing.
        var top = await BandAsync(db, mk.Id, 7);
        Assert.Equal(62_000m, top.MinPrice);
        Assert.Equal(13m, top.TargetMarginPct);   // KS target (20 − 7) + 0 uplift
        Assert.Equal(3m, top.MarginFloorPct);
    }

    [Fact]
    public async Task Match_LeavesBoundariesRoundingAndDeadStockUntouched()
    {
        using var db = await SeededDbAsync();
        await SetKsAsync(db, "Gjirafa50");
        var mk = await LayerAsync(db, "Gjirafa50", "MK");

        var before = await BandAsync(db, mk.Id, 3);
        var minBefore = before.MinPrice;
        var maxBefore = before.MaxPrice;
        var rcBefore = before.RoundingConvention;
        var deadFloorBefore = before.DeadStockFloorCostPct;

        await new BandMatchService(db).MatchToPrimaryAsync(mk.Id, 1.5m);
        await db.SaveChangesAsync();

        var after = await BandAsync(db, mk.Id, 3);
        Assert.Equal(minBefore, after.MinPrice);
        Assert.Equal(maxBefore, after.MaxPrice);
        Assert.Equal(rcBefore, after.RoundingConvention);          // local charm untouched
        Assert.Equal(deadFloorBefore, after.DeadStockFloorCostPct); // dead-stock ladder untouched
    }

    [Fact]
    public async Task Match_StaysWithinTheBrand()
    {
        using var db = await SeededDbAsync();
        await SetKsAsync(db, "Gjirafa50");
        var gj50Mk = await LayerAsync(db, "Gjirafa50", "MK");
        var mallMk = await LayerAsync(db, "GjirafaMall", "MK");
        var mallTargetBefore = (await BandAsync(db, mallMk.Id, 2)).TargetMarginPct;

        await new BandMatchService(db).MatchToPrimaryAsync(gj50Mk.Id, 1.5m);
        await db.SaveChangesAsync();

        Assert.Equal(mallTargetBefore, (await BandAsync(db, mallMk.Id, 2)).TargetMarginPct);
    }

    [Fact]
    public async Task Match_OnTheKsLayerItself_IsANoOp()
    {
        using var db = await SeededDbAsync();
        var ks = await LayerAsync(db, "Gjirafa50", "KS");

        var result = await new BandMatchService(db).MatchToPrimaryAsync(ks.Id, 1.5m);

        Assert.Null(result);   // KS has no KS sibling to match to
        Assert.Null(await new BandMatchService(db).GetSourceAsync(ks.Id));
    }

    [Fact]
    public async Task Match_AKsBandWithNoTarget_LeavesTheSiblingTargetUnset()
    {
        using var db = await SeededDbAsync();
        await SetKsAsync(db, "Gjirafa50");
        var ks = await LayerAsync(db, "Gjirafa50", "KS");
        (await BandAsync(db, ks.Id, 4)).TargetMarginPct = null;   // guardrail off on this KS band
        await db.SaveChangesAsync();

        var mk = await LayerAsync(db, "Gjirafa50", "MK");
        await new BandMatchService(db).MatchToPrimaryAsync(mk.Id, 1.5m);
        await db.SaveChangesAsync();

        var band = await BandAsync(db, mk.Id, 4);
        Assert.Null(band.TargetMarginPct);          // no target carried over, no uplift on null
        Assert.Equal(10m - 4, band.MarginFloorPct); // floor still copied
    }

    [Fact]
    public async Task Match_ReRun_ReportsNothingChanged()
    {
        using var db = await SeededDbAsync();
        await SetKsAsync(db, "Gjirafa50");
        var mk = await LayerAsync(db, "Gjirafa50", "MK");
        var service = new BandMatchService(db);

        await service.MatchToPrimaryAsync(mk.Id, 1.5m);
        await db.SaveChangesAsync();

        var second = await service.MatchToPrimaryAsync(mk.Id, 1.5m);
        Assert.NotNull(second);
        Assert.False(second!.AnyChange);
        Assert.Empty(second.Changes);
    }

    [Fact]
    public async Task Match_ClampsAnExtremeUpliftIntoTheValidRange()
    {
        using var db = await SeededDbAsync();
        await SetKsAsync(db, "Gjirafa50");   // KS targets 20..13
        var mk = await LayerAsync(db, "Gjirafa50", "MK");

        await new BandMatchService(db).MatchToPrimaryAsync(mk.Id, 50m);   // 20 + 50 = 70 → fine; keep < 100
        await db.SaveChangesAsync();

        var band = await BandAsync(db, mk.Id, 0);
        Assert.True(band.TargetMarginPct < 100m);
        Assert.Equal(70m, band.TargetMarginPct);
    }

    [Fact]
    public async Task GetSource_DescribesTheDirectionOfTravel()
    {
        using var db = await SeededDbAsync();
        var service = new BandMatchService(db);
        var ks = await LayerAsync(db, "Gjirafa50", "KS");
        var mk = await LayerAsync(db, "Gjirafa50", "MK");

        Assert.Null(await service.GetSourceAsync(ks.Id));                 // KS is the source, not a target
        Assert.Equal(ks.Id, (await service.GetSourceAsync(mk.Id))?.Id);   // MK pulls from KS
    }
}
