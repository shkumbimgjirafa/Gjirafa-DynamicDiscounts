using Microsoft.EntityFrameworkCore;
using PricingTool.Core.Options;
using PricingTool.Data;
using PricingTool.Data.Services;

namespace PricingTool.Tests;

/// <summary>
/// DbSeeder denominates each layer's price bands in the LAYER'S currency. EUR layers keep the plain
/// EUR tiers; non-EUR layers scale those tiers into local currency (MKD ×62 — the denar's EUR peg;
/// ALL ×98 — the observed KS→AL cost ratio), because a band buckets a SKU by its PPTCV cost and that
/// cost is stored per country in the local currency. Without the scaling every MK/AL SKU costing more
/// than ~€16/~€10 collapsed into the top band.
/// </summary>
public class BandSeedingTests
{
    private static PricingToolDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PricingToolDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private const string Euro = "€";   // €
    private const string Dash = "–";   // – (en dash, matches the seed tier names)

    [Fact]
    public async Task EveryLayer_GetsTheDefaultDiscountTrimTable_SwitchedOff()
    {
        using var db = NewDb();
        await DbSeeder.SeedCoreAsync(db, new PricingEngineOptions());
        await DbSeeder.SeedCoreAsync(db, new PricingEngineOptions());   // idempotent: no duplicate rows

        foreach (var layer in await db.Layers.ToListAsync())
        {
            Assert.False(layer.DiscountOptimizationEnabled);   // inputs are seeded, the feature is not
            var rules = await db.DiscountTrimRules.Where(r => r.LayerId == layer.Id).OrderBy(r => r.SortOrder).ToListAsync();
            Assert.Equal(PricingTool.Core.Domain.DiscountOptimizer.DefaultRanges, rules.Select(r => r.ToRange()).ToList());
        }
    }

    [Fact]
    public async Task EurLayer_KeepsPlainEurTiers()
    {
        using var db = NewDb();
        await DbSeeder.SeedCoreAsync(db, new PricingEngineOptions());

        var ks = await db.Layers.SingleAsync(l => l.Brand == "GjirafaMall" && l.CountryCode == "KS");
        var bands = await db.PriceBands.Where(b => b.LayerId == ks.Id).OrderBy(b => b.SortOrder).ToListAsync();

        Assert.Equal(8, bands.Count);
        Assert.Equal($"0{Dash}10", bands[0].Name);   // no € prefix on EUR layers
        Assert.Equal(0m, bands[0].MinPrice);
        Assert.Equal(10m, bands[0].MaxPrice);
        Assert.Equal("1,000+", bands[7].Name);
        Assert.Equal(1000m, bands[7].MinPrice);
        Assert.Equal(999999m, bands[7].MaxPrice);
    }

    [Theory]
    [InlineData("MK", 62)]   // MKD — the denar's hard EUR peg
    [InlineData("AL", 98)]   // ALL — observed median KS→AL cost ratio (~97.6, rounded to 98)
    public async Task NonEurLayer_ScalesTiersIntoLocalCurrency(string country, int factor)
    {
        using var db = NewDb();
        await DbSeeder.SeedCoreAsync(db, new PricingEngineOptions());

        var layer = await db.Layers.SingleAsync(l => l.Brand == "GjirafaMall" && l.CountryCode == country);
        var bands = await db.PriceBands.Where(b => b.LayerId == layer.Id).OrderBy(b => b.SortOrder).ToListAsync();

        Assert.Equal(8, bands.Count);

        // Lower edges are the EUR tier lower bounds × factor, and every non-EUR band keeps the € tier name.
        decimal[] eurMins = { 0, 10, 50, 100, 250, 500, 750, 1000 };
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(eurMins[i] * factor, bands[i].MinPrice);
            Assert.StartsWith(Euro, bands[i].Name);
            if (i > 0) Assert.Equal(bands[i - 1].MaxPrice, bands[i].MinPrice); // contiguous, no gaps
        }

        Assert.Equal($"{Euro}0{Dash}10", bands[0].Name);
        Assert.Equal(10m * factor, bands[0].MaxPrice);
        Assert.Equal($"{Euro}10{Dash}50", bands[1].Name);
        Assert.Equal(50m * factor, bands[1].MaxPrice);

        // Open-ended top band: a clean ≈€1M-equivalent ceiling so no real cost falls out of a band.
        Assert.Equal($"{Euro}1,000+", bands[7].Name);
        Assert.Equal(1000m * factor, bands[7].MinPrice);
        Assert.Equal(1_000_000m * factor, bands[7].MaxPrice);
    }

    [Theory]
    // A €30 item — the case that used to collapse into the top "1000+" band — now buckets into €10–50.
    [InlineData("MK", 1860)]   // 30 EUR × 62 (MKD)
    [InlineData("AL", 2940)]   // 30 EUR × 98 (ALL)
    public async Task LocalCost_BucketsIntoTheEquivalentEurTier(string country, decimal localCost)
    {
        using var db = NewDb();
        await DbSeeder.SeedCoreAsync(db, new PricingEngineOptions());

        var layer = await db.Layers.SingleAsync(l => l.Brand == "GjirafaMall" && l.CountryCode == country);
        var bands = await new BandConfigProvider(db).GetBandsAsync(layer.Id);

        var band = BandConfigProvider.FindBand(bands, localCost);

        Assert.NotNull(band);
        Assert.Equal($"{Euro}10{Dash}50", band!.Name);
    }
}
