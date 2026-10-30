using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PricingTool.Agent;
using PricingTool.Agent.Checker;
using PricingTool.Agent.ClaudeCode;
using PricingTool.Agent.Storage;
using PricingTool.Core.Algorithms;
using PricingTool.Core.Domain;
using PricingTool.Core.Options;
using PricingTool.Core.Services;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;

namespace PricingTool.Tests;

/// <summary>
/// Per-category floors and targets (plan section 5): a SKU whose band × category has a rule prices against the
/// rule's floor and target; every other SKU against its band. The engine finds the category by SKU in the product
/// map, records the rules each run used, and the run checker judges each price against the floor that produced it.
/// </summary>
public class PerCategoryFloorTests
{
    private static PriceBandConfig Band(int id = 26, decimal floor = 10m, decimal? target = null) =>
        new() { BandId = id, Name = "10-50", MinPrice = 0m, MaxPrice = 500m, MarginFloorPct = floor, TargetMarginPct = target };

    private static IReadOnlyDictionary<string, IReadOnlyList<SkuListing>> Listings(params (string Sku, int ProductId, string Category)[] rows) =>
        rows.GroupBy(r => r.Sku).ToDictionary(g => g.Key, g => (IReadOnlyList<SkuListing>)g.Select(r => new SkuListing(r.ProductId, r.Category)).ToList());

    [Fact]
    public void A_rule_gives_its_band_x_category_its_own_floor_and_target_and_leaves_the_rest()
    {
        var band = Band(floor: 13m, target: 23m);
        var bands = new CategoryBands(new[] { band },
            new[] { new CategoryRule(26, "Aksesorë", 14m, 24m), new CategoryRule(26, "SMART", null, 25m), new CategoryRule(99, "Aksesorë", 30m, null) },
            Listings(("A", 1, "Aksesorë"), ("S", 2, "SMART"), ("M", 3, "Other"), ("M", 4, "Aksesorë")));

        var a = bands.Resolve("A", 1, band);
        Assert.Equal((26, 14m, 24m), (a.BandId, a.MarginFloorPct, a.TargetMarginPct));
        Assert.Equal((13m, 25m), (bands.Resolve("S", 2, band).MarginFloorPct, bands.Resolve("S", 2, band).TargetMarginPct)); // floor kept
        Assert.Same(band, bands.Resolve("X", 9, band));                          // not in the map: the band itself
        Assert.Same(band, bands.Resolve("M", 3, band));                          // own listing "Other": no rule
        Assert.Equal(14m, bands.Resolve("M", 4, band).MarginFloorPct);           // the other listing is in "Aksesorë"
        Assert.Same(band, bands.Resolve("M", null, band));                       // no own id: the lowest id (3, "Other")
        Assert.Equal(2, bands.Rules.Count);                                      // band 99 isn't in this run
        Assert.Equal(13m, band.MarginFloorPct);                                  // the band itself is untouched
        Assert.Equal("Aksesorë", bands.TargetGroupOf("A", 1, 26));
        Assert.Null(bands.TargetGroupOf("M", 3, 26));
        Assert.Same(band, CategoryBands.None.Resolve("A", 1, band));
    }

    [Fact]
    public void The_margin_target_pass_measures_a_category_with_its_own_target_apart_from_its_band()
    {
        // Band target 20 %: B1 + B2 at cost 70 / price 100 sit at 30 % and are trimmed toward 20 %.
        // Category target 40 %: C1 at 30 % is lifted toward 40 %. Measured together the three would be trimmed.
        var candidates = new[]
        {
            new MarginCandidate("B1", 26, false, 100m, 70m, 77.78m, 100m),
            new MarginCandidate("B2", 26, false, 100m, 70m, 77.78m, 100m),
            new MarginCandidate("C1", 26, false, 100m, 70m, 77.78m, 130m, Category: "Kozmetikë"),
        };
        var result = new MarginTargetService().BuildOverrides(new[] { Band(target: 20m) }, candidates, new PricingEngineOptions(),
            new Dictionary<(int, string), decimal> { [(26, "Kozmetikë")] = 40m });

        Assert.True(result.Overrides["B1"].IsTrim);
        Assert.False(result.Overrides["C1"].IsTrim);
        Assert.Equal(116.67m, Math.Round(result.Overrides["C1"].AdjustedPrice, 2));   // 70 / (1 - 0.40)
        Assert.Equal(new[] { (string?)null, "Kozmetikë" }, result.Bands.Select(b => b.Category));
    }

    private sealed class FakeCategoryMap : ICategoryMapReader
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<SkuListing>> _map;
        public FakeCategoryMap(IReadOnlyDictionary<string, IReadOnlyList<SkuListing>> map) => _map = map;
        public IReadOnlyCollection<string>? AskedFor { get; private set; }
        public Task<IReadOnlyDictionary<string, IReadOnlyList<SkuListing>>?> ReadAsync(int layerId, bool isMall, IReadOnlyCollection<string> categories, CancellationToken ct = default)
        {
            AskedFor = categories;
            return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<SkuListing>>?>(_map);
        }
    }

    private static SnapshotRow Row(string sku, int productId) => new()
    {
        Sku = sku, ProductId = productId, OldPrice = 100m, CurrentPrice = 80m, Pptcv = 60m,
        LocalWarehouseStock = 10, SupplierWarehouseStock = 0,
        Qty7 = 7, Net7 = 50, Disc7 = 0.1m, Qty14 = 14, Net14 = 100, Disc14 = 0.1m,
        Qty30 = 30, Net30 = 220, Disc30 = 0.1m, Qty60 = 60, Net60 = 440, Disc60 = 0.1m,
        Qty90 = 90, Net90 = 660, Disc90 = 0.1m,
    };

    [Fact]
    public async Task The_engine_lifts_a_sku_to_its_category_floor_and_records_the_rule_it_used()
    {
        using var db = new PricingToolDbContext(new DbContextOptionsBuilder<PricingToolDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var layer = new Layer
        {
            Brand = "GjirafaMall", CountryCode = "KS", DisplayName = "Test — KS", OperationalDatabase = "GjirafaMall", StoreId = 2,
            TranslationCountryId = 1, WarehouseStoreId = 2, Currency = "EUR", RunTimeUtc = "03:00", CadenceHours = 24, IsActive = true,
        };
        db.Layers.Add(layer);
        db.SaveChanges();
        var band = new PriceBand { LayerId = layer.Id, Name = "all", MinPrice = 0, MaxPrice = 500, MarginFloorPct = 10 };
        foreach (var (code, _, weight) in AlgorithmCodes.All)
            band.AlgorithmSettings.Add(new BandAlgorithmSetting { AlgorithmCode = code, Enabled = false, Weight = weight });
        db.PriceBands.Add(band);
        db.SaveChanges();
        db.BandCategoryRules.Add(new BandCategoryRule { LayerId = layer.Id, BandId = band.Id, Category = "Kozmetikë", MarginFloorPct = 30m, UpdatedBy = "Alpha" });
        db.SaveChanges();
        var map = new FakeCategoryMap(Listings(("IN-RULE", 1, "Kozmetikë"), ("OTHER", 2, "Sport")));

        // Cost 60, current 80: above the band's 10 % floor (66.67), below the category's 30 % floor (85.71).
        var run = await OrchestratorTests.NewOrchestrator(db, new[] { Row("IN-RULE", 1), Row("OTHER", 2), Row("NOT-MAPPED", 3) }, categoryMap: map)
            .ExecuteRunAsync("test", isOnDemand: true, layer.Id);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(new[] { "Kozmetikë" }, map.AskedFor);
        var prices = await db.ProposedPrices.AsNoTracking().Where(p => p.PricingRunId == run.Id).ToDictionaryAsync(p => p.Sku, p => p.ProposedPriceValue);
        Assert.Equal(85.72m, prices["IN-RULE"]);                                 // lifted to 60 / (1 - 0.30) = 85.714, up to the cent
        Assert.Equal(80m, prices["OTHER"]);                                      // band floor 10 %: unchanged
        Assert.Equal(80m, prices["NOT-MAPPED"]);
        var used = Assert.Single(await db.PricingRunCategoryRules.AsNoTracking().Where(r => r.PricingRunId == run.Id).ToListAsync());
        Assert.Equal((band.Id, "Kozmetikë", 30m, (decimal?)null, 1), (used.BandId, used.Category, used.MarginFloorPct, used.TargetMarginPct, used.Skus));
    }

    [Fact]
    public async Task Without_rules_the_engine_never_asks_for_the_category_map()
    {
        using var db = new PricingToolDbContext(new DbContextOptionsBuilder<PricingToolDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Layers.Add(new Layer { Id = 7, Brand = "Gjirafa50", CountryCode = "KS", DisplayName = "x", OperationalDatabase = "GjirafaEcommerce", Currency = "EUR" });
        db.SaveChanges();
        var map = new FakeCategoryMap(Listings(("A", 1, "Kozmetikë")));
        var bands = await new BandConfigProvider(db, map).GetCategoryBandsAsync(db.Layers.Single(), Array.Empty<PriceBandConfig>());
        Assert.Same(CategoryBands.None, bands);
        Assert.Null(map.AskedFor);
    }

    private sealed class FakeCategories : IProposalCategoryLookup
    {
        public Dictionary<long, string> Map { get; } = new();
        public Task<IReadOnlyDictionary<long, string>> CategoriesAsync(long runId, int layerId, IReadOnlyCollection<int> bandIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<long, string>>(Map);
    }

    [Fact]
    public async Task The_run_checker_judges_a_price_against_the_floor_its_run_used()
    {
        await using var ai = new AgentDbContext(new DbContextOptionsBuilder<AgentDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        ai.Database.EnsureCreated();
        await using var tool = new PricingToolDbContext(new DbContextOptionsBuilder<PricingToolDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        tool.Layers.Add(new Layer { Id = 4, Brand = "Gjirafa50", CountryCode = "KS", DisplayName = "Gjirafa50 — Kosovo", OperationalDatabase = "GjirafaEcommerce", StoreId = 2, Currency = "EUR", VatRatePct = 18m });
        tool.PriceBands.Add(new PriceBand { Id = 26, LayerId = 4, Name = "10–50", MinPrice = 10, MaxPrice = 50, MarginFloorPct = 13 });
        tool.PricingRuns.Add(new PricingRun { Id = 7001, LayerId = 4, Status = RunStatus.Succeeded, StartedUtc = new DateTime(2026, 10, 31, 6, 0, 0) });
        tool.PricingRunBandTargets.Add(new PricingRunBandTarget { PricingRunId = 7001, LayerId = 4, BandId = 26, Name = "10–50", MarginFloorPct = 13m });
        tool.PricingRunCategoryRules.Add(new PricingRunCategoryRule { PricingRunId = 7001, LayerId = 4, BandId = 26, Category = "Aksesorë", MarginFloorPct = 11m, Skus = 1 });
        // Cost 30, new price 34: margin 11.8 %, under the band's 13 % but over the category's 11 %. Rounding off.
        foreach (var (id, sku) in new[] { (1L, "IN-RULE"), (2L, "OTHER") })
            tool.ProposedPrices.Add(new ProposedPrice
            {
                Id = id, PricingRunId = 7001, LayerId = 4, Sku = sku, ProductName = $"[INJECTED-TEST] {sku}", PriceBandId = 26, HasChange = true,
                ProposedPriceValue = 34m, CurrentPrice = 35m, OldPrice = 40m, AnchorPrice = 40m, Pptcv = 30m, ChangePct = -2.86m, ReasonCodes = "", GuardrailFlags = "",
            });
        tool.SaveChanges();
        var categories = new FakeCategories();
        categories.Map[1] = "Aksesorë";
        categories.Map[2] = "SMART";

        // The model call fails (no Claude Code here), so the run stays blocked; the rule flags are what this test reads.
        var options = Options.Create(new AgentOptions { Enabled = true, RuntimeDirectory = Path.Combine(Path.GetTempPath(), "pt-catfloor-" + Guid.NewGuid().ToString("N")) });
        var client = new ClaudeCodeClient(options, new NoProcess(), ai, NullLogger<ClaudeCodeClient>.Instance);
        await new RunChecker(tool, ai, client, options, NullLogger<RunChecker>.Instance, categories: categories).CheckAsync(7001, isTest: true);

        var flags = await ai.AgentRunCheckRows.AsNoTracking().ToDictionaryAsync(r => r.Sku, r => r.Flags);
        Assert.DoesNotContain("below-floor", flags.GetValueOrDefault("IN-RULE") ?? "");
        Assert.Contains("below-floor", flags["OTHER"]);
    }

    private sealed class NoProcess : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default) =>
            Task.FromResult(new ProcessResult(1, "", "no Claude Code in this test", false, TimeSpan.Zero));
    }
}
