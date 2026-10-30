using Microsoft.EntityFrameworkCore;
using PricingTool.Core.Options;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;

namespace PricingTool.Tests;

/// <summary>
/// The per-band margin floor is tuned on the Kosovo layer of each brand and must stay identical on that
/// brand's MK and AL layers: it is a percentage of the selling price, so it is currency-agnostic even
/// though the band boundaries are denominated in denar/lek. BandFloorMirrorService copies it across on
/// every save, matching bands by SortOrder (never by MinPrice — those differ by the currency scale).
/// </summary>
public class BandFloorMirrorTests
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

    [Theory]
    [InlineData("Gjirafa50")]
    [InlineData("GjirafaMall")]
    public async Task EditingAKosovoBand_MirrorsTheFloorToBothSiblingCountries(string brand)
    {
        using var db = await SeededDbAsync();
        var ks = await LayerAsync(db, brand, "KS");
        var mk = await LayerAsync(db, brand, "MK");
        var al = await LayerAsync(db, brand, "AL");

        var source = await BandAsync(db, ks.Id, 4);
        source.MarginFloorPct = 8m;
        var mirrored = await new BandFloorMirrorService(db).MirrorMarginFloorAsync(source);
        await db.SaveChangesAsync();

        Assert.Equal(2, mirrored.Count);
        Assert.Equal(8m, (await BandAsync(db, mk.Id, 4)).MarginFloorPct);
        Assert.Equal(8m, (await BandAsync(db, al.Id, 4)).MarginFloorPct);
    }

    [Fact]
    public async Task MirroringMatchesOnSortOrder_NotOnTheCurrencyScaledBoundaries()
    {
        using var db = await SeededDbAsync();
        var ks = await LayerAsync(db, "Gjirafa50", "KS");
        var mk = await LayerAsync(db, "Gjirafa50", "MK");

        var source = await BandAsync(db, ks.Id, 7);   // KS "1,000+" starts at 1000 EUR
        source.MarginFloorPct = 6m;
        await new BandFloorMirrorService(db).MirrorMarginFloorAsync(source);
        await db.SaveChangesAsync();

        // The MK band in the same position starts at 62,000 MKD — a MinPrice match would have found nothing.
        var target = await BandAsync(db, mk.Id, 7);
        Assert.Equal(62_000m, target.MinPrice);
        Assert.Equal(6m, target.MarginFloorPct);
    }

    [Fact]
    public async Task MirroringStaysWithinTheBrand()
    {
        using var db = await SeededDbAsync();
        var gj50Ks = await LayerAsync(db, "Gjirafa50", "KS");
        var mallMk = await LayerAsync(db, "GjirafaMall", "MK");
        var mallBefore = (await BandAsync(db, mallMk.Id, 2)).MarginFloorPct;

        var source = await BandAsync(db, gj50Ks.Id, 2);
        source.MarginFloorPct = 4m;
        var mirrored = await new BandFloorMirrorService(db).MirrorMarginFloorAsync(source);
        await db.SaveChangesAsync();

        Assert.All(mirrored, m => Assert.NotEqual(mallMk.Id, m.LayerId));
        Assert.Equal(mallBefore, (await BandAsync(db, mallMk.Id, 2)).MarginFloorPct);
    }

    [Fact]
    public async Task EditingANonKosovoBand_MirrorsNothing()
    {
        using var db = await SeededDbAsync();
        var mk = await LayerAsync(db, "Gjirafa50", "MK");
        var ks = await LayerAsync(db, "Gjirafa50", "KS");
        var ksBefore = (await BandAsync(db, ks.Id, 3)).MarginFloorPct;

        var source = await BandAsync(db, mk.Id, 3);
        source.MarginFloorPct = 2m;
        var mirrored = await new BandFloorMirrorService(db).MirrorMarginFloorAsync(source);
        await db.SaveChangesAsync();

        Assert.Empty(mirrored);
        Assert.Equal(ksBefore, (await BandAsync(db, ks.Id, 3)).MarginFloorPct);
    }

    [Fact]
    public async Task OnlyTheMarginFloorCrossesOver()
    {
        using var db = await SeededDbAsync();
        var ks = await LayerAsync(db, "Gjirafa50", "KS");
        var mk = await LayerAsync(db, "Gjirafa50", "MK");

        var mkBand = await BandAsync(db, mk.Id, 1);
        mkBand.TargetMarginPct = 27m;
        mkBand.DeadStockFloorCostPct = 95m;
        mkBand.RoundingConvention = 9;
        await db.SaveChangesAsync();

        var source = await BandAsync(db, ks.Id, 1);
        source.MarginFloorPct = 11m;
        source.TargetMarginPct = 33m;
        source.DeadStockFloorCostPct = 100m;
        source.RoundingConvention = 7;
        await new BandFloorMirrorService(db).MirrorMarginFloorAsync(source);
        await db.SaveChangesAsync();

        var after = await BandAsync(db, mk.Id, 1);
        Assert.Equal(11m, after.MarginFloorPct);
        Assert.Equal(27m, after.TargetMarginPct);          // per-country goal untouched
        Assert.Equal(95m, after.DeadStockFloorCostPct);    // dead-stock stage floors untouched
        Assert.Equal(9, after.RoundingConvention);         // local charm convention untouched
    }

    [Fact]
    public async Task AlreadyMatchingBandsAreNotReportedAsChanged()
    {
        using var db = await SeededDbAsync();
        var ks = await LayerAsync(db, "Gjirafa50", "KS");
        var service = new BandFloorMirrorService(db);

        var source = await BandAsync(db, ks.Id, 5);
        source.MarginFloorPct = 7m;
        await service.MirrorMarginFloorAsync(source);
        await db.SaveChangesAsync();

        var second = await service.MirrorMarginFloorAsync(await BandAsync(db, ks.Id, 5));
        Assert.Empty(second);
    }

    [Fact]
    public async Task MirrorTargetsAndSource_DescribeTheDirectionOfTravel()
    {
        using var db = await SeededDbAsync();
        var service = new BandFloorMirrorService(db);
        var ks = await LayerAsync(db, "Gjirafa50", "KS");
        var al = await LayerAsync(db, "Gjirafa50", "AL");

        var targets = await service.GetMirrorTargetsAsync(ks.Id);
        Assert.Equal(2, targets.Count);
        Assert.All(targets, l => Assert.Equal("Gjirafa50", l.Brand));
        Assert.DoesNotContain(targets, l => l.CountryCode == "KS");
        Assert.Null(await service.GetMirrorSourceAsync(ks.Id));

        Assert.Empty(await service.GetMirrorTargetsAsync(al.Id));
        Assert.Equal(ks.Id, (await service.GetMirrorSourceAsync(al.Id))?.Id);
    }
}
