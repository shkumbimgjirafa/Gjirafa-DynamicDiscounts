using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PricingTool.Core.Abstractions;
using PricingTool.Core.Algorithms;
using PricingTool.Core.Domain;
using PricingTool.Core.Options;
using PricingTool.Core.Services;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;

namespace PricingTool.Tests;

internal class FakeReader : ISourceDataReader
{
    private readonly IReadOnlyList<SnapshotRow> _rows;
    /// <summary>The context the orchestrator built for the most recent pull — lets tests assert the
    /// sibling-platform cross-sales wiring.</summary>
    public LayerSourceContext? LastContext { get; private set; }
    /// <summary>Canned approved-batch refresh rows; GetApprovedBatchSalesAsync returns those whose SKU was requested.</summary>
    public List<BatchSaleRow> BatchSales { get; } = new();
    /// <summary>The SKU -> pushed-price map from the most recent approved-batch refresh — lets tests assert
    /// the pushed prices (which drive the source-side discount price match) are forwarded from the items.</summary>
    public IReadOnlyDictionary<string, decimal>? LastSkuPushedPrices { get; private set; }
    public FakeReader(IReadOnlyList<SnapshotRow> rows) => _rows = rows;
    public Task<IReadOnlyList<SnapshotRow>> GetDailyDatasetAsync(LayerSourceContext layer, CancellationToken ct = default)
    {
        LastContext = layer;
        return Task.FromResult(_rows);
    }
    public Task<IReadOnlyList<BatchSaleRow>> GetApprovedBatchSalesAsync(
        LayerSourceContext layer, DateTime sinceUtc, IReadOnlyDictionary<string, decimal> skuPushedPrices, CancellationToken ct = default)
    {
        LastContext = layer;
        LastSkuPushedPrices = skuPushedPrices;
        var requested = skuPushedPrices.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult<IReadOnlyList<BatchSaleRow>>(
            BatchSales.Where(r => requested.Contains(r.Sku)).ToList());
    }
}

/// <summary>
/// EF-backed <see cref="IBulkWriteService"/> for tests (the production one uses SqlBulkCopy, which
/// can't target the in-memory provider). Proposals carry their votes in the navigation, so adding
/// the proposals cascades the votes — BulkInsertVotesAsync is therefore a no-op here.
/// </summary>
internal class EfBulkWriter : IBulkWriteService
{
    private readonly PricingToolDbContext _db;
    public EfBulkWriter(PricingToolDbContext db) => _db = db;

    public async Task<long> GetMaxProposedPriceIdAsync(CancellationToken ct = default)
        => await _db.ProposedPrices.AnyAsync(ct) ? await _db.ProposedPrices.MaxAsync(p => p.Id, ct) : 0;

    public async Task DeleteSnapshotsForDateAsync(int layerId, DateTime date, CancellationToken ct = default)
    {
        var existing = await _db.DailySnapshots
            .Where(s => s.LayerId == layerId && s.SnapshotDate == date.Date).ToListAsync(ct);
        _db.DailySnapshots.RemoveRange(existing);
        await _db.SaveChangesAsync(ct);
    }

    public Task ReseedProposedPricesAsync(CancellationToken ct = default) => Task.CompletedTask;

    // Virtual so a test can fail the snapshot write the way SqlBulkCopy does (see ScheduledRunWorkerTests).
    public virtual async Task BulkInsertSnapshotsAsync(IReadOnlyCollection<DailySnapshot> rows, CancellationToken ct = default)
    {
        _db.DailySnapshots.AddRange(rows);
        await _db.SaveChangesAsync(ct);
    }

    public async Task BulkInsertProposalsAsync(IReadOnlyCollection<ProposedPrice> proposals, CancellationToken ct = default)
    {
        _db.ProposedPrices.AddRange(proposals);
        await _db.SaveChangesAsync(ct);
    }

    public Task BulkInsertVotesAsync(IReadOnlyCollection<AlgorithmVoteRecord> votes, CancellationToken ct = default)
        => Task.CompletedTask;

    public async Task DeleteElasticityForLayerAsync(int layerId, CancellationToken ct = default)
    {
        var existing = await _db.SkuElasticities.Where(e => e.LayerId == layerId).ToListAsync(ct);
        _db.SkuElasticities.RemoveRange(existing);
        await _db.SaveChangesAsync(ct);
    }

    public async Task BulkInsertElasticityAsync(IReadOnlyCollection<SkuElasticity> rows, CancellationToken ct = default)
    {
        _db.SkuElasticities.AddRange(rows);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpsertDeadStockStateAsync(IReadOnlyCollection<SkuDeadStockState> rows, CancellationToken ct = default)
    {
        foreach (var r in rows)
        {
            var existing = await _db.SkuDeadStockStates
                .FirstOrDefaultAsync(s => s.LayerId == r.LayerId && s.Sku == r.Sku, ct);
            if (existing is null)
            {
                _db.SkuDeadStockStates.Add(r);
            }
            else
            {
                existing.ZeroSaleStreakDays = r.ZeroSaleStreakDays;
                existing.PrevStreakDays = r.PrevStreakDays;
                existing.DeadObservedDays = r.DeadObservedDays;
                existing.PrevDeadObservedDays = r.PrevDeadObservedDays;
                existing.LastSnapshotDate = r.LastSnapshotDate;
                existing.PrevSnapshotDate = r.PrevSnapshotDate;
                existing.MarkdownTargetPct = r.MarkdownTargetPct;
                existing.MarkdownCaughtUpStreak = r.MarkdownCaughtUpStreak;
                existing.BelowFloorTunnelPrice = r.BelowFloorTunnelPrice;
            }
        }
        await _db.SaveChangesAsync(ct);
    }

    // Matched-only, like the production UPDATE-join: a row without persisted state is skipped.
    public async Task UpsertDeadStockMarkdownAsync(int layerId, IReadOnlyCollection<SkuMarkdownPacingUpdate> rows, CancellationToken ct = default)
    {
        foreach (var r in rows)
        {
            var existing = await _db.SkuDeadStockStates
                .FirstOrDefaultAsync(s => s.LayerId == layerId && s.Sku == r.Sku, ct);
            if (existing is null) continue;
            existing.MarkdownTargetPct = r.TargetPct;
            existing.MarkdownCaughtUpStreak = r.CaughtUpStreak;
        }
        await _db.SaveChangesAsync(ct);
    }

    // Matched-only, like the production UPDATE-join.
    public async Task UpsertDeadStockTunnelPriceAsync(int layerId, IReadOnlyCollection<SkuTunnelPriceUpdate> rows, CancellationToken ct = default)
    {
        foreach (var r in rows)
        {
            var existing = await _db.SkuDeadStockStates
                .FirstOrDefaultAsync(s => s.LayerId == layerId && s.Sku == r.Sku, ct);
            if (existing is null) continue;
            existing.BelowFloorTunnelPrice = r.TunnelPrice;
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpsertDailyKpiAsync(DailyLayerKpi row, CancellationToken ct = default)
    {
        var existing = await _db.DailyLayerKpi
            .FirstOrDefaultAsync(k => k.LayerId == row.LayerId && k.Date == row.Date.Date, ct);
        if (existing is null)
        {
            _db.DailyLayerKpi.Add(row);
        }
        else
        {
            existing.Net7 = row.Net7;
            existing.Qty7 = row.Qty7;
            existing.CostedNet7 = row.CostedNet7;
            existing.Cost7 = row.Cost7;
            existing.SkuCount = row.SkuCount;
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task<int> DeleteSnapshotsBeforeDateAsync(DateTime cutoff, CancellationToken ct = default)
    {
        // Mirrors production: a layer's newest snapshot day is never deleted, however old.
        var newestDay = await _db.DailySnapshots.GroupBy(s => s.LayerId)
            .Select(g => new { LayerId = g.Key, D = g.Max(s => s.SnapshotDate) }).ToListAsync(ct);
        var old = (await _db.DailySnapshots.Where(s => s.SnapshotDate < cutoff.Date).ToListAsync(ct))
            .Where(s => !newestDay.Any(k => k.LayerId == s.LayerId && k.D == s.SnapshotDate)).ToList();
        _db.DailySnapshots.RemoveRange(old);
        await _db.SaveChangesAsync(ct);
        return old.Count;
    }

    // The in-memory provider does not honour FK cascade, so delete votes then proposals explicitly
    // (production relies on the configured cascade from ProposedPrice → AlgorithmVotes). It also models
    // the production null-out of the non-cascading PriceChangeOutcome FK so tests exercise that contract.
    // Mirrors production: a layer's newest succeeded run is never deleted, however old.
    private async Task<List<long>> NewestRunIdsAsync(CancellationToken ct) =>
        await _db.PricingRuns.Where(r => r.Status == RunStatus.Succeeded || r.Status == RunStatus.SucceededWithErrors)
            .GroupBy(r => r.LayerId).Select(g => g.Max(r => r.Id)).ToListAsync(ct);

    public async Task<int> DeleteProposalsBeforeRunDateAsync(DateTime cutoff, CancellationToken ct = default)
    {
        var keep = await NewestRunIdsAsync(ct);
        var runIds = await _db.PricingRuns.Where(r => r.StartedUtc < cutoff && !keep.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct);
        var props = await _db.ProposedPrices.Where(p => runIds.Contains(p.PricingRunId)).ToListAsync(ct);
        var propIds = props.Select(p => p.Id).ToList();
        var outcomes = await _db.PriceChangeOutcomes
            .Where(o => o.ProposedPriceId != null && propIds.Contains(o.ProposedPriceId.Value)).ToListAsync(ct);
        foreach (var o in outcomes) o.ProposedPriceId = null; // outcomes outlive their proposal (nullable NoAction FK)
        var votes = await _db.AlgorithmVotes.Where(v => propIds.Contains(v.ProposedPriceId)).ToListAsync(ct);
        _db.AlgorithmVotes.RemoveRange(votes);
        _db.ProposedPrices.RemoveRange(props);
        await _db.SaveChangesAsync(ct);
        return props.Count;
    }

    public async Task<int> DeleteRunsBeforeDateAsync(DateTime cutoff, CancellationToken ct = default)
    {
        var keep = await NewestRunIdsAsync(ct);
        var old = await _db.PricingRuns.Where(r => r.StartedUtc < cutoff && !keep.Contains(r.Id)).ToListAsync(ct);
        _db.PricingRuns.RemoveRange(old);
        await _db.SaveChangesAsync(ct);
        return old.Count;
    }
}

public class OrchestratorTests
{
    private static PricingToolDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PricingToolDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>Seeds a single active layer and returns its id (tests scope everything to it).</summary>
    private static int SeedLayer(PricingToolDbContext db, bool floorAndRoundingOnly = false,
        DateTime? lastSlowLaneRunUtc = null, bool ignoreNewProducts = true, bool discountOptimization = false)
    {
        var layer = new Layer
        {
            Brand = "GjirafaMall", CountryCode = "KS", DisplayName = "Test — KS",
            OperationalDatabase = "GjirafaMall", StoreId = 2, TranslationCountryId = 1, WarehouseStoreId = 2,
            Currency = "EUR", FilterVendors = true, ExcludeUnpublished = true,
            RunTimeUtc = "03:00", CadenceHours = 24, IsActive = true, SortOrder = 0,
            FloorAndRoundingOnly = floorAndRoundingOnly,
            IgnoreNewProducts = ignoreNewProducts,
            DiscountOptimizationEnabled = discountOptimization,
            // Null (the default) leaves the slow lane DUE, so most tests exercise the engage/step path.
            // Pass a recent value to seed a FROZEN run (a daily run between 15-day ticks).
            LastSlowLaneRunUtc = lastSlowLaneRunUtc,
        };
        db.Layers.Add(layer);
        db.SaveChanges();
        return layer.Id;
    }

    private static void SeedBand(PricingToolDbContext db, int layerId, decimal min = 0, decimal max = 500,
        int roundingConvention = 0, bool roundingEnabled = false,
        decimal? targetMarginPct = null, bool algorithmsEnabled = true)
    {
        var band = new PriceBand
        {
            LayerId = layerId,
            Name = "test", MinPrice = min, MaxPrice = max,
            MarginFloorPct = 10,
            TargetMarginPct = targetMarginPct,
            RoundingConvention = roundingConvention, RoundingEnabled = roundingEnabled, SortOrder = 0,
        };
        foreach (var (code, _, weight) in AlgorithmCodes.All)
            band.AlgorithmSettings.Add(new BandAlgorithmSetting { AlgorithmCode = code, Enabled = algorithmsEnabled, Weight = weight });
        db.PriceBands.Add(band);
        db.SaveChanges();
    }

    internal static PricingRunOrchestrator NewOrchestrator(
        PricingToolDbContext db, IReadOnlyList<SnapshotRow> rows, PricingEngineOptions? options = null,
        ISourceDataReader? reader = null, IBulkWriteService? bulkWriter = null, ICategoryMapReader? categoryMap = null)
    {
        options ??= new PricingEngineOptions();
        var algorithms = new IPricingAlgorithm[]
        {
            new SellThroughAlgorithm(),
            new PriceElasticityHeuristicAlgorithm(),
            new DeadStockMarkdownAlgorithm(),
        };
        var bulk = bulkWriter ?? new EfBulkWriter(db);
        var guardrails = new GuardrailService();
        return new PricingRunOrchestrator(
            db,
            reader ?? new FakeReader(rows),
            new SnapshotService(db, bulk),
            bulk,
            new BandConfigProvider(db, categoryMap),
            new PriceCalculator(new WeightedScoringService(), guardrails, new RoundingService()),
            guardrails,
            new MarginTargetService(),
            algorithms,
            new AuditService(db),
            new OutcomeEvaluationService(db, new AuditService(db), NullLogger<OutcomeEvaluationService>.Instance,
                Options.Create(options)),
            new DeadStockEpisodeService(db),
            Options.Create(options),
            NullLogger<PricingRunOrchestrator>.Instance);
    }

    private static SnapshotRow Row(string sku, decimal? oldPrice, decimal? currentPrice, decimal? pptcv,
        int? supplierId = null, string? supplierName = null,
        int? brandId = null, string? brand = null, string? productName = null, int? productId = null) => new()
    {
        Sku = sku, OldPrice = oldPrice, CurrentPrice = currentPrice, Pptcv = pptcv,
        ProductId = productId,
        LocalWarehouseStock = 10, SupplierWarehouseStock = 0,
        SupplierId = supplierId, SupplierName = supplierName,
        BrandId = brandId, Brand = brand, ProductName = productName,
        Qty7 = 7, Net7 = 50, Disc7 = 0.1m, Qty14 = 14, Net14 = 100, Disc14 = 0.1m,
        Qty30 = 30, Net30 = 220, Disc30 = 0.1m, Qty60 = 60, Net60 = 440, Disc60 = 0.1m,
        Qty90 = 90, Net90 = 660, Disc90 = 0.1m,
    };

    /// <summary>A platform-new SKU that otherwise sells normally (so nothing but the new-product hold
    /// would stop it being priced).</summary>
    private static SnapshotRow NewProductRow(string sku) => new()
    {
        Sku = sku, OldPrice = 100m, CurrentPrice = 80m, Pptcv = 40m,
        LocalWarehouseStock = 10, SupplierWarehouseStock = 0,
        IsNewProduct = true,
        Qty7 = 7, Net7 = 50, Disc7 = 0.1m, Qty14 = 14, Net14 = 100, Disc14 = 0.1m,
        Qty30 = 30, Net30 = 220, Disc30 = 0.1m, Qty60 = 60, Net60 = 440, Disc60 = 0.1m,
        Qty90 = 90, Net90 = 660, Disc90 = 0.1m,
    };

    private static string[] Codes(ProposedPrice p) =>
        (p.ReasonCodes + "," + p.GuardrailFlags).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    public async Task Run_IgnoreNewProductsDefault_HoldsNewProductAtCurrentPrice()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);   // IgnoreNewProducts defaults to true (today's behaviour)
        SeedBand(db, layerId);

        var run = await NewOrchestrator(db, new[] { NewProductRow("SKU-NEW") })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);
        Assert.Equal(RunStatus.Succeeded, run.Status);

        var p = await db.ProposedPrices.AsNoTracking().SingleAsync(x => x.Sku == "SKU-NEW");
        Assert.Contains(GuardrailFlags.NewProductProtected, Codes(p));
        Assert.False(p.HasChange);
        Assert.Equal(p.CurrentPrice, p.ProposedPriceValue);   // held exactly at the current price
    }

    [Fact]
    public async Task Run_IgnoreNewProductsFalse_PricesNewProductLikeAnyOtherSku()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db, ignoreNewProducts: false);   // opt the layer into pricing new products
        SeedBand(db, layerId);

        var run = await NewOrchestrator(db, new[] { NewProductRow("SKU-NEW") })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);
        Assert.Equal(RunStatus.Succeeded, run.Status);

        var p = await db.ProposedPrices.AsNoTracking().SingleAsync(x => x.Sku == "SKU-NEW");
        // The new-product hard rule is gone: the SKU flows through the normal pipeline, not held.
        Assert.DoesNotContain(GuardrailFlags.NewProductProtected, Codes(p));
        Assert.NotEqual(ProposalStatus.Skipped, p.Status);
    }

    /// <summary>Seeds the default discount-optimization table for a layer (what DbSeeder does at startup).</summary>
    private static void SeedDiscountTrims(PricingToolDbContext db, int layerId)
    {
        var order = 0;
        foreach (var range in DiscountOptimizer.DefaultRanges)
            db.DiscountTrimRules.Add(new DiscountTrimRule
            {
                LayerId = layerId, MinDiscountPct = range.MinDiscountPct, MaxDiscountPct = range.MaxDiscountPct,
                TrimPct = range.TrimPct, SortOrder = order++,
            });
        db.SaveChanges();
    }

    [Fact]
    public async Task Run_DiscountOptimizationOn_TrimsADeepLiveDiscount()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db, discountOptimization: true);
        SeedBand(db, layerId, algorithmsEnabled: false);   // no votes: the live price goes through the audit
        SeedDiscountTrims(db, layerId);

        // Shelf 100, selling at 75 = 25% off → the 20–30% row trims 18% of it → 20.5% off = 79.50.
        var run = await NewOrchestrator(db, new[] { Row("SKU-DEEP", 100m, 75m, 40m) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);
        Assert.Equal(RunStatus.Succeeded, run.Status);

        var p = await db.ProposedPrices.AsNoTracking().SingleAsync(x => x.Sku == "SKU-DEEP");
        Assert.Equal(79.50m, p.ProposedPriceValue);
        Assert.True(p.HasChange);
        Assert.Contains(GuardrailFlags.DiscountOptimized, p.GuardrailFlags.Split(','));
        Assert.Contains(GuardrailFlags.DiscountOptimized, p.ReasonCodes.Split(','));
    }

    [Fact]
    public async Task Run_DiscountOptimizationOff_LeavesTheDiscountAlone_EvenWithATable()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);   // DiscountOptimizationEnabled defaults to false
        SeedBand(db, layerId, algorithmsEnabled: false);
        SeedDiscountTrims(db, layerId);

        var run = await NewOrchestrator(db, new[] { Row("SKU-DEEP", 100m, 75m, 40m) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);
        Assert.Equal(RunStatus.Succeeded, run.Status);

        var p = await db.ProposedPrices.AsNoTracking().SingleAsync(x => x.Sku == "SKU-DEEP");
        Assert.Equal(75m, p.ProposedPriceValue);
        Assert.DoesNotContain(GuardrailFlags.DiscountOptimized, Codes(p));
    }

    [Fact]
    public async Task Run_SnapshotsBandTargetAndFloorMargins()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId, min: 0, max: 500, targetMarginPct: 22m);   // MarginFloorPct defaults to 10%

        var run = await NewOrchestrator(db, new[] { Row("SKU-OK", 100m, 80m, 40m) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);
        Assert.Equal(RunStatus.Succeeded, run.Status);

        // The run records the band TARGET + FLOOR it priced against, keyed by RunId (decoupled from the
        // run so it outlives retention), so a later look-back reads the run's own targets, not today's bands.
        var snap = await db.PricingRunBandTargets.AsNoTracking().SingleAsync(b => b.PricingRunId == run.Id);
        Assert.Equal(layerId, snap.LayerId);
        Assert.Equal("test", snap.Name);
        Assert.Equal(0m, snap.MinPrice);
        Assert.Equal(500m, snap.MaxPrice);
        Assert.Equal(22m, snap.TargetMarginPct);
        Assert.Equal(10m, snap.MarginFloorPct);
    }

    [Fact]
    public async Task Run_SnapshotsBandWithNoTarget_AsNull()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId, min: 0, max: 500);   // no target margin set

        var run = await NewOrchestrator(db, new[] { Row("SKU-OK", 100m, 80m, 40m) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);
        Assert.Equal(RunStatus.Succeeded, run.Status);

        var snap = await db.PricingRunBandTargets.AsNoTracking().SingleAsync(b => b.PricingRunId == run.Id);
        Assert.Null(snap.TargetMarginPct);
        Assert.Equal(10m, snap.MarginFloorPct);
    }

    [Fact]
    public async Task Run_SkipsNullCost_FlagsMissingPrice_PricesTheRest()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);
        var rows = new List<SnapshotRow>
        {
            Row("SKU-OK", 100m, 80m, 40m),
            Row("SKU-NOCOST", 100m, 80m, null),      // v1 policy: skip + flag, never cost=0
            Row("SKU-NOPRICE", null, null, 40m),
            Row("SKU-NOBAND", 600m, 550m, 600m),     // PPTCV outside the 0–500 test band (bands key off cost)
        };

        var returned = await NewOrchestrator(db, rows).ExecuteRunAsync("test", isOnDemand: true, layerId);

        // Assert on the PERSISTED row, not the returned in-memory object — the run record is
        // the operational source of truth and must survive any change-tracker manipulation.
        var run = await db.PricingRuns.AsNoTracking().SingleAsync(r => r.Id == returned.Id);
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(4, run.SkuCount);
        Assert.Equal(4, run.ProposalCount);
        Assert.Equal(3, run.SkippedCount);
        Assert.Equal(0, run.ErrorCount);
        Assert.NotNull(run.FinishedUtc);

        var proposals = await db.ProposedPrices.Include(p => p.Votes).ToListAsync();
        Assert.Equal(ProposalStatus.Pending, proposals.Single(p => p.Sku == "SKU-OK").Status);
        Assert.Equal("MISSING_COST", proposals.Single(p => p.Sku == "SKU-NOCOST").SkipReason);
        Assert.Equal("MISSING_PRICE", proposals.Single(p => p.Sku == "SKU-NOPRICE").SkipReason);
        Assert.Equal("NO_BAND", proposals.Single(p => p.Sku == "SKU-NOBAND").SkipReason);

        // Snapshot persisted for every pulled row (architecture rule 3).
        Assert.Equal(4, await db.DailySnapshots.CountAsync());

        // The priced SKU recorded its votes for explainability.
        Assert.NotEmpty(proposals.Single(p => p.Sku == "SKU-OK").Votes);

        // Run start/finish audited.
        Assert.True(await db.AuditLog.CountAsync() >= 2);
    }

    [Fact]
    public async Task Run_CarriesSupplierOntoSnapshotAndProposals()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);
        var rows = new List<SnapshotRow>
        {
            Row("SKU-SUPPLIED", 100m, 80m, 40m, supplierId: 42, supplierName: "Asbis"),
            Row("SKU-NOCOST", 100m, 80m, null, supplierId: 7, supplierName: "CZC"), // skipped rows keep their supplier
            Row("SKU-UNMAPPED", 100m, 80m, 40m),                                    // no ProductStoreMapping row
        };

        await NewOrchestrator(db, rows).ExecuteRunAsync("test", isOnDemand: true, layerId);

        var snap = await db.DailySnapshots.AsNoTracking().SingleAsync(s => s.Sku == "SKU-SUPPLIED");
        Assert.Equal(42, snap.SupplierId);
        Assert.Equal("Asbis", snap.SupplierName);

        var proposals = await db.ProposedPrices.AsNoTracking().ToListAsync();
        var priced = proposals.Single(p => p.Sku == "SKU-SUPPLIED");
        Assert.Equal(42, priced.SupplierId);
        Assert.Equal("Asbis", priced.SupplierName);

        var skipped = proposals.Single(p => p.Sku == "SKU-NOCOST");
        Assert.Equal(ProposalStatus.Skipped, skipped.Status);
        Assert.Equal(7, skipped.SupplierId);
        Assert.Equal("CZC", skipped.SupplierName);

        var unmapped = proposals.Single(p => p.Sku == "SKU-UNMAPPED");
        Assert.Null(unmapped.SupplierId);
        Assert.Null(unmapped.SupplierName);
    }

    [Fact]
    public async Task Run_CarriesBrandAndProductNameOntoSnapshotAndProposals()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);
        var rows = new List<SnapshotRow>
        {
            Row("SKU-BRANDED", 100m, 80m, 40m, brandId: 101, brand: "Nordis", productName: "Nordis Widget 3000"),
            Row("SKU-NOCOST", 100m, 80m, null, brandId: 102, brand: "Volteon", productName: "Volteon Cable"), // skipped rows keep brand/name
            Row("SKU-NOBRAND", 100m, 80m, 40m),                                                                // no manufacturer mapping
        };

        await NewOrchestrator(db, rows).ExecuteRunAsync("test", isOnDemand: true, layerId);

        var snap = await db.DailySnapshots.AsNoTracking().SingleAsync(s => s.Sku == "SKU-BRANDED");
        Assert.Equal(101, snap.BrandId);
        Assert.Equal("Nordis", snap.Brand);
        Assert.Equal("Nordis Widget 3000", snap.ProductName);

        var proposals = await db.ProposedPrices.AsNoTracking().ToListAsync();
        var priced = proposals.Single(p => p.Sku == "SKU-BRANDED");
        Assert.Equal(101, priced.BrandId);
        Assert.Equal("Nordis", priced.Brand);
        Assert.Equal("Nordis Widget 3000", priced.ProductName);

        var skipped = proposals.Single(p => p.Sku == "SKU-NOCOST");
        Assert.Equal(ProposalStatus.Skipped, skipped.Status);
        Assert.Equal(102, skipped.BrandId);
        Assert.Equal("Volteon", skipped.Brand);
        Assert.Equal("Volteon Cable", skipped.ProductName);

        var noBrand = proposals.Single(p => p.Sku == "SKU-NOBRAND");
        Assert.Null(noBrand.BrandId);
        Assert.Null(noBrand.Brand);
        Assert.Null(noBrand.ProductName);
    }

    /// <summary>No-sales row for the dead-stock stage stamping tests: zero across every window.</summary>
    private static SnapshotRow NoSalesRow(string sku, int? oldestUnitAgeDays,
        int localStock = 10, int supplierStock = 0, decimal? pptcv = 40m, bool isNewProduct = false) => new()
    {
        Sku = sku, OldPrice = 100m, CurrentPrice = 80m, Pptcv = pptcv,
        LocalWarehouseStock = localStock, SupplierWarehouseStock = supplierStock,
        OldestUnitAgeDays = oldestUnitAgeDays, IsNewProduct = isNewProduct,
    };

    /// <summary>Seeds a prior dead-stock state so a run resumes from a given observed-dead-day count
    /// (the run folds one more no-sale day on top). Back-dated one day so the run counts as a new day.</summary>
    private static void SeedObservedDead(PricingToolDbContext db, int layerId, string sku, int observedDeadDays)
    {
        db.SkuDeadStockStates.Add(new SkuDeadStockState
        {
            LayerId = layerId, Sku = sku,
            ZeroSaleStreakDays = observedDeadDays, PrevStreakDays = observedDeadDays,
            DeadObservedDays = observedDeadDays, PrevDeadObservedDays = observedDeadDays,
            LastSnapshotDate = DateTime.UtcNow.Date.AddDays(-1),
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Run_StampsDeadStockStage_PerObservedDeadTier()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);   // entity defaults: stage 2 at 180, stage 3 at 360 observed no-sale days

        // The stage is driven by the snapshot-tracked observed-dead counter (NOT WMS age): seed prior
        // observed-dead days for the deeper tiers; the run folds in one more no-sale day. A SKU with no
        // prior state lands at Stage 1 (observed-dead 1 after this run) — the "everyone starts at Stage 1"
        // rollout behaviour.
        SeedObservedDead(db, layerId, "SKU-DEAD-S2", 250);   // → 251 → stage 2
        SeedObservedDead(db, layerId, "SKU-DEAD-S3", 500);   // → 501 → stage 3

        var rows = new List<SnapshotRow>
        {
            NoSalesRow("SKU-DEAD-NEW", null),                                   // just went dead → stage 1
            NoSalesRow("SKU-DEAD-S2", null),                                    // seeded 250 → stage 2
            NoSalesRow("SKU-DEAD-S3", null),                                    // seeded 500 → stage 3
            NoSalesRow("SKU-FRESH", 5),                                         // freshly stocked → None
            NoSalesRow("SKU-SUPPLIER", null, localStock: 0, supplierStock: 50), // supplier-only → None
            NoSalesRow("SKU-NEW", null, isNewProduct: true),                    // platform-new → None
            NoSalesRow("SKU-NOCOST", null, pptcv: null),                        // skipped → never stamped
            Row("SKU-SELLING", 100m, 80m, 40m),                                 // selling → None
        };

        var run = await NewOrchestrator(db, rows).ExecuteRunAsync("test", isOnDemand: true, layerId);
        Assert.Equal(RunStatus.Succeeded, run.Status);

        var proposals = await db.ProposedPrices.Include(p => p.Votes).AsNoTracking().ToListAsync();
        DeadStockStage StageOf(string sku) => proposals.Single(p => p.Sku == sku).DeadStockStage;

        Assert.Equal(DeadStockStage.Stage1, StageOf("SKU-DEAD-NEW"));
        Assert.Equal(DeadStockStage.Stage2, StageOf("SKU-DEAD-S2"));
        Assert.Equal(DeadStockStage.Stage3, StageOf("SKU-DEAD-S3"));
        Assert.Equal(DeadStockStage.None, StageOf("SKU-FRESH"));
        Assert.Equal(DeadStockStage.None, StageOf("SKU-SUPPLIER"));
        Assert.Equal(DeadStockStage.None, StageOf("SKU-NEW"));
        Assert.Equal(DeadStockStage.None, StageOf("SKU-NOCOST"));
        Assert.Equal(DeadStockStage.None, StageOf("SKU-SELLING"));

        // The dead-stock algorithm voted on exactly the staged (dead) SKUs — the stamp and the
        // algorithm share one definition (SkuContext.IsDeadStock).
        var voted = proposals.Where(p => p.Votes.Any(v => v.AlgorithmCode == AlgorithmCodes.DeadStock))
            .Select(p => p.Sku).OrderBy(s => s).ToList();
        var staged = proposals.Where(p => p.DeadStockStage != DeadStockStage.None)
            .Select(p => p.Sku).OrderBy(s => s).ToList();
        Assert.Equal(staged, voted);
    }

    [Fact]
    public async Task Run_RecordsDeadStockSpells_AndTheCountryPile_AndASameDayRerunChangesNothing()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);
        SeedObservedDead(db, layerId, "SKU-DEAD-S2", 250);   // → 251 → stage 2
        var rows = new List<SnapshotRow>
        {
            NoSalesRow("SKU-DEAD-NEW", null),
            NoSalesRow("SKU-DEAD-S2", null),
            Row("SKU-SELLING", 100m, 80m, 40m),
        };

        await NewOrchestrator(db, rows).ExecuteRunAsync("test", isOnDemand: true, layerId);
        await NewOrchestrator(db, rows).ExecuteRunAsync("test", isOnDemand: true, layerId);

        var spells = await db.DeadStockEpisodes.AsNoTracking().OrderBy(e => e.Sku).ToListAsync();
        Assert.Equal(new[] { "SKU-DEAD-NEW", "SKU-DEAD-S2" }, spells.Select(e => e.Sku));
        Assert.All(spells, e =>
        {
            Assert.Equal(DateTime.UtcNow.Date, e.EnteredDate);
            Assert.Null(e.ExitedDate);
            Assert.Equal(10, e.LastLocalStock);
        });
        Assert.Equal(DeadStockStage.Stage2, spells[1].MaxStage);

        var pile = await db.DeadStockDailyKpis.AsNoTracking().SingleAsync();
        Assert.Equal("KS", pile.CountryCode);
        Assert.Equal(2, pile.DeadSkus);
        Assert.Equal(20, pile.DeadUnits);
        Assert.Equal(800m, pile.DeadCost);                  // 2 SKUs × 10 units × PPTCV 40
        Assert.Equal(1, pile.Stage1Skus);
        Assert.Equal(1, pile.Stage2Skus);
    }

    [Fact]
    public async Task Run_FloorAndRoundingOnlyLayer_StillStampsDeadStockStage()
    {
        using var db = NewDb();
        // Algorithms OFF at the layer level: no dead-stock vote can ever fire, but the stage label
        // must still be stamped — it's how dead stock stays visible on FloorAndRoundingOnly layers.
        var layerId = SeedLayer(db, floorAndRoundingOnly: true);
        SeedBand(db, layerId);
        SeedObservedDead(db, layerId, "SKU-DEAD", 250);   // → 251 → stage 2

        var rows = new List<SnapshotRow> { NoSalesRow("SKU-DEAD", null) };
        var run = await NewOrchestrator(db, rows).ExecuteRunAsync("test", isOnDemand: true, layerId);
        Assert.Equal(RunStatus.Succeeded, run.Status);

        var proposal = await db.ProposedPrices.Include(p => p.Votes).AsNoTracking().SingleAsync();
        Assert.Equal(DeadStockStage.Stage2, proposal.DeadStockStage);
        Assert.Empty(proposal.Votes);
    }

    [Fact]
    public async Task Run_AdvancesObservedDeadCounter_OnAContinuedNoSaleDay()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);
        SeedObservedDead(db, layerId, "SKU-D", 5);   // dead 5 observed days as of yesterday

        await NewOrchestrator(db, new List<SnapshotRow> { NoSalesRow("SKU-D", null) })
            .ExecuteRunAsync("t", isOnDemand: true, layerId);

        var state = await db.SkuDeadStockStates.AsNoTracking().SingleAsync(s => s.Sku == "SKU-D");
        Assert.Equal(6, state.DeadObservedDays);   // one more no-sale day folded in
    }

    [Fact]
    public async Task Run_ResetsObservedDeadCounter_OnASale()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);
        SeedObservedDead(db, layerId, "SKU-D", 200);   // was deep into the stages

        var selling = new SnapshotRow
        {
            Sku = "SKU-D", OldPrice = 100m, CurrentPrice = 80m, Pptcv = 40m,
            LocalWarehouseStock = 10, Qty7 = 4, Qty90 = 4,
        };
        await NewOrchestrator(db, new List<SnapshotRow> { selling })
            .ExecuteRunAsync("t", isOnDemand: true, layerId);

        var state = await db.SkuDeadStockStates.AsNoTracking().SingleAsync(s => s.Sku == "SKU-D");
        Assert.Equal(0, state.DeadObservedDays);   // a sale resets the stage clock back to Stage 1
    }

    [Fact]
    public async Task Run_PopulatesSiblingPlatformSourceContext_ForCrossPlatformSales()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);   // GjirafaMall — KS (opDb GjirafaMall, store 2)
        SeedBand(db, layerId);
        // The other brand in the same country shares the physical warehouse — given a distinct store id
        // to prove the orchestrator reads the SIBLING's store, not its own.
        db.Layers.Add(new Layer
        {
            Brand = "Gjirafa50", CountryCode = "KS", DisplayName = "Gj50 — KS",
            OperationalDatabase = "GjirafaEcommerce", StoreId = 7, TranslationCountryId = 1,
            WarehouseStoreId = 2, Currency = "EUR", IsActive = true,
        });
        db.SaveChanges();

        var rows = new List<SnapshotRow> { Row("SKU-OK", 100m, 80m, 40m) };
        var reader = new FakeReader(rows);
        await NewOrchestrator(db, rows, reader: reader).ExecuteRunAsync("t", isOnDemand: true, layerId);

        Assert.Equal("GjirafaEcommerce", reader.LastContext!.SiblingOperationalDatabase);
        Assert.Equal(7, reader.LastContext.SiblingStoreId);
    }

    [Fact]
    public async Task Run_SingleLayerCountry_HasNoSiblingSourceContext()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);   // no other brand in this country
        SeedBand(db, layerId);

        var rows = new List<SnapshotRow> { Row("SKU-OK", 100m, 80m, 40m) };
        var reader = new FakeReader(rows);
        await NewOrchestrator(db, rows, reader: reader).ExecuteRunAsync("t", isOnDemand: true, layerId);

        Assert.Null(reader.LastContext!.SiblingOperationalDatabase);
        Assert.Equal(-1, reader.LastContext.SiblingStoreId);   // sibling branch of the sales query matches nothing
    }

    [Fact]
    public async Task Run_WritesDailyLayerKpi_MatchingTheSnapshotGroupBy()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);
        var rows = new List<SnapshotRow>
        {
            Row("SKU-A", 100m, 80m, 40m),
            Row("SKU-B", 100m, 80m, 20m),
            Row("SKU-C", 100m, 80m, null),   // no cost → excluded from CostedNet7/Cost7
        };

        await NewOrchestrator(db, rows).ExecuteRunAsync("test", isOnDemand: true, layerId);

        // Recompute the four aggregates straight from the persisted snapshots — the GROUP BY the
        // DailyLayerKpi rollup replaces on the dashboard. The stored rollup must equal it exactly.
        var expected = await db.DailySnapshots.AsNoTracking()
            .Where(s => s.LayerId == layerId)
            .GroupBy(s => s.SnapshotDate)
            .Select(g => new
            {
                Date = g.Key,
                Net7 = g.Sum(s => s.Net7),
                Qty7 = g.Sum(s => (long)s.Qty7),
                CostedNet7 = g.Where(s => s.Pptcv != null).Sum(s => s.Net7),
                Cost7 = g.Where(s => s.Pptcv != null).Sum(s => s.Pptcv!.Value * s.Qty7),
                SkuCount = g.Count(),
            })
            .SingleAsync();

        var kpi = await db.DailyLayerKpi.AsNoTracking().SingleAsync(k => k.LayerId == layerId);
        Assert.Equal(expected.Date, kpi.Date);
        Assert.Equal(expected.Net7, kpi.Net7);
        Assert.Equal(expected.Qty7, kpi.Qty7);
        Assert.Equal(expected.CostedNet7, kpi.CostedNet7);
        Assert.Equal(expected.Cost7, kpi.Cost7);
        Assert.Equal(expected.SkuCount, kpi.SkuCount);
    }

    [Fact]
    public async Task Run_WithDefaultOptions_DoesNotPruneOldSnapshots()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);
        var ancient = DateTime.UtcNow.Date.AddDays(-400); // a 35-day retention WOULD delete this
        db.DailySnapshots.Add(new DailySnapshot
        {
            LayerId = layerId, SnapshotDate = ancient, Sku = "OLD", Qty7 = 0, Net7 = 0m, Pptcv = 5m,
        });
        db.SaveChanges();

        await NewOrchestrator(db, new List<SnapshotRow> { Row("SKU-OK", 100m, 80m, 40m) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        // RetentionEnabled defaults false, so the ancient row survives — retention ships dormant.
        Assert.True(await db.DailySnapshots.AnyAsync(s => s.Sku == "OLD" && s.SnapshotDate == ancient));
    }

    [Fact]
    public async Task Run_WithRetentionEnabled_PrunesOldSnapshots_KeepsThisRun()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);
        var ancient = DateTime.UtcNow.Date.AddDays(-400);
        db.DailySnapshots.Add(new DailySnapshot
        {
            LayerId = layerId, SnapshotDate = ancient, Sku = "OLD", Qty7 = 0, Net7 = 0m, Pptcv = 5m,
        });
        db.SaveChanges();

        // Snapshots-only retention (RetentionIncludesProposals defaults false).
        var opts = new PricingEngineOptions { RetentionEnabled = true };
        await NewOrchestrator(db, new List<SnapshotRow> { Row("SKU-OK", 100m, 80m, 40m) }, opts)
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        Assert.False(await db.DailySnapshots.AnyAsync(s => s.SnapshotDate == ancient)); // pruned
        Assert.True(await db.DailySnapshots.AnyAsync(s => s.Sku == "SKU-OK"));          // this run's snapshot kept
    }

    [Fact]
    public async Task Run_WithProposalRetentionEnabled_PrunesOldRunAndProposals_DetachesOutcome_KeepsVerdict()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);

        // An old run with a proposal + vote, and a graded outcome that references that proposal — the
        // exact shape that would throw FK 547 if outcomes weren't detached before the prune.
        var oldRun = new PricingRun
        {
            LayerId = layerId, StartedUtc = DateTime.UtcNow.AddDays(-400),
            Status = RunStatus.Succeeded, FinishedUtc = DateTime.UtcNow.AddDays(-400),
        };
        db.PricingRuns.Add(oldRun);
        db.SaveChanges();
        var oldProp = new ProposedPrice { PricingRunId = oldRun.Id, LayerId = layerId, Sku = "OLD" };
        oldProp.Votes.Add(new AlgorithmVoteRecord { AlgorithmCode = "SELL_THROUGH" });
        db.ProposedPrices.Add(oldProp);
        db.SaveChanges();
        db.PriceChangeOutcomes.Add(new PriceChangeOutcome
        {
            LayerId = layerId, ProposedPriceId = oldProp.Id, Sku = "OLD",
            AppliedUtc = DateTime.UtcNow.AddDays(-390), Verdict = OutcomeVerdict.Win, WindowDays = 7,
        });
        db.SaveChanges();

        var opts = new PricingEngineOptions { RetentionEnabled = true, RetentionIncludesProposals = true };
        await NewOrchestrator(db, new List<SnapshotRow> { Row("SKU-OK", 100m, 80m, 40m) }, opts)
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        Assert.False(await db.PricingRuns.AnyAsync(r => r.Id == oldRun.Id));         // old run pruned
        Assert.Equal(0, await db.ProposedPrices.CountAsync(p => p.Sku == "OLD"));    // its proposal pruned (votes cascaded)
        var outcome = await db.PriceChangeOutcomes.AsNoTracking().SingleAsync(o => o.Sku == "OLD");
        Assert.Null(outcome.ProposedPriceId);                                       // detached, NOT blocked/deleted
        Assert.Equal(OutcomeVerdict.Win, outcome.Verdict);                          // graded verdict preserved
    }

    [Fact]
    public async Task Run_FloorAndRoundingOnlyLayer_SkipsAlgorithms_AppliesFloorAndRounding()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db, floorAndRoundingOnly: true);
        SeedBand(db, layerId, roundingConvention: (int)RoundingConvention.EndsIn99, roundingEnabled: true);

        // SKU sells well (Row sets Qty90=90) so the algorithms would normally move it — but the layer
        // is in floor + rounding-only mode, so the only change is rounding the current price to .99.
        var run = await NewOrchestrator(db, new List<SnapshotRow> { Row("SKU-A", 100m, 87.31m, 40m) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        var proposal = await db.ProposedPrices.Include(p => p.Votes).SingleAsync(p => p.Sku == "SKU-A");
        Assert.Equal(ProposalStatus.Pending, proposal.Status);
        Assert.Equal(86.99m, proposal.ProposedPriceValue);   // current 87.31 rounded down to .99
        Assert.True(proposal.HasChange);
        Assert.Null(proposal.RawWeightedPrice);              // no algorithm produced a price
        Assert.Empty(proposal.Votes);                        // and none ran
        Assert.Contains("ALGORITHMS_DISABLED", proposal.ReasonCodes);
    }

    [Fact]
    public async Task Run_RefusesToStart_WhenAnotherRunIsInProgress()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);
        db.PricingRuns.Add(new PricingRun { LayerId = layerId, StartedUtc = DateTime.UtcNow, Status = RunStatus.Running });
        db.SaveChanges();

        // Its own type, not a bare InvalidOperationException: the scheduler keys "leave the slot due" off it,
        // and a genuinely failed run can throw the base type too (see ScheduledRunWorkerTests).
        var orchestrator = NewOrchestrator(db, new List<SnapshotRow>());
        await Assert.ThrowsAsync<RunAlreadyInProgressException>(
            () => orchestrator.ExecuteRunAsync("test", isOnDemand: true, layerId));
    }

    [Fact]
    public async Task Run_FailsStaleRunningRuns_AndProceeds()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);
        db.PricingRuns.Add(new PricingRun { LayerId = layerId, StartedUtc = DateTime.UtcNow.AddHours(-3), Status = RunStatus.Running });
        db.SaveChanges();

        var run = await NewOrchestrator(db, new List<SnapshotRow> { Row("SKU-OK", 100m, 80m, 40m) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        var stale = await db.PricingRuns.OrderBy(r => r.Id).FirstAsync();
        Assert.Equal(RunStatus.Failed, stale.Status);
    }

    [Fact]
    public async Task ZeroSaleStreaks_IncrementResetAndIdempotentReplay()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        var snapshots = new SnapshotService(db, new EfBulkWriter(db));
        var start = new DateTime(2026, 6, 9);

        // Drive four consecutive days. SKU-A sells on day 0 then nothing (streak -> 3);
        // SKU-B never sells (streak -> 4). The counter is maintained by SaveSnapshotAsync.
        var qtyA = new[] { 5, 0, 0, 0 };
        for (var i = 0; i < 4; i++)
        {
            var date = start.AddDays(i);
            await snapshots.SaveSnapshotAsync(layerId,
                new List<SnapshotRow> { StreakRow("SKU-A", qtyA[i]), StreakRow("SKU-B", 0) }, date);
        }

        var streaks = await snapshots.GetDeadStockStatesAsync(layerId);
        Assert.Equal(3, streaks["SKU-A"].Streak);
        Assert.Equal(4, streaks["SKU-B"].Streak);

        // A same-UTC-date re-pull must NOT double-count (latest pull replaces the day).
        var lastDate = start.AddDays(3);
        await snapshots.SaveSnapshotAsync(layerId,
            new List<SnapshotRow> { StreakRow("SKU-A", 0), StreakRow("SKU-B", 0) }, lastDate);
        var replayed = await snapshots.GetDeadStockStatesAsync(layerId);
        Assert.Equal(3, replayed["SKU-A"].Streak);
        Assert.Equal(4, replayed["SKU-B"].Streak);

        // A sale on the next day resets that SKU's streak; the other keeps climbing.
        var nextDate = start.AddDays(4);
        await snapshots.SaveSnapshotAsync(layerId,
            new List<SnapshotRow> { StreakRow("SKU-A", 2), StreakRow("SKU-B", 0) }, nextDate);
        var afterSale = await snapshots.GetDeadStockStatesAsync(layerId);
        Assert.Equal(0, afterSale["SKU-A"].Streak);
        Assert.Equal(5, afterSale["SKU-B"].Streak);
    }

    private static SnapshotRow StreakRow(string sku, int qty7) => new() { Sku = sku, Qty7 = qty7 };

    /// <summary>A locally-held dead-stock row: zero sales everywhere, stock old enough to pass the
    /// freshness gate. A non-zero <paramref name="qty7"/> fills every window (real data cascades).</summary>
    private static SnapshotRow DeadRow(string sku, decimal? oldPrice, decimal? currentPrice, decimal? pptcv,
        int? oldestUnitAgeDays = 120, int qty7 = 0) => new()
    {
        Sku = sku, OldPrice = oldPrice, CurrentPrice = currentPrice, Pptcv = pptcv,
        LocalWarehouseStock = 10, SupplierWarehouseStock = 0,
        OldestUnitAgeDays = oldestUnitAgeDays,
        Qty7 = qty7, Qty14 = qty7, Qty30 = qty7, Qty60 = qty7, Qty90 = qty7,
    };

    [Fact]
    public async Task Run_DeadStock_OpensOneStepBelowCurrent_AndPersistsPacingState()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);

        // Dead at -20% (anchor 100, current 80): the opening paced drop is -25% → 75, NOT a deep
        // streak-driven jump. The advanced state must be persisted for the next run.
        var run = await NewOrchestrator(db, new List<SnapshotRow> { DeadRow("SKU-DEAD", 100m, 80m, 40m) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        var proposal = await db.ProposedPrices.SingleAsync(p => p.Sku == "SKU-DEAD");
        Assert.Equal(75m, proposal.ProposedPriceValue);

        var state = await db.SkuDeadStockStates.SingleAsync(s => s.LayerId == layerId && s.Sku == "SKU-DEAD");
        Assert.Equal(25m, state.MarkdownTargetPct);
        Assert.Null(state.MarkdownCaughtUpStreak);          // the -25% is not applied yet — no clock
    }

    [Fact]
    public async Task Run_DeadStock_AppliedAndPeriodElapsed_StepsOneDeeper_ThenSameDayRerunHolds()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);

        // Persisted pacing: target -25% applied since streak 76; today's run sees streak 91 (15 ≥ the
        // 14-day period at the applied level) and the shop at -25% → step to -30%.
        db.SkuDeadStockStates.Add(new SkuDeadStockState
        {
            LayerId = layerId, Sku = "SKU-DEAD",
            ZeroSaleStreakDays = 90, PrevStreakDays = 89,
            LastSnapshotDate = DateTime.UtcNow.Date.AddDays(-1),
            MarkdownTargetPct = 25m, MarkdownCaughtUpStreak = 76,
        });
        db.SaveChanges();

        var rows = new List<SnapshotRow> { DeadRow("SKU-DEAD", 100m, 75m, 40m) };
        await NewOrchestrator(db, rows).ExecuteRunAsync("test", isOnDemand: true, layerId);

        var proposal = await db.ProposedPrices.SingleAsync(p => p.Sku == "SKU-DEAD");
        Assert.Equal(70m, proposal.ProposedPriceValue);
        var state = await db.SkuDeadStockStates.SingleAsync(s => s.Sku == "SKU-DEAD");
        Assert.Equal(91, state.ZeroSaleStreakDays);
        Assert.Equal(30m, state.MarkdownTargetPct);
        Assert.Null(state.MarkdownCaughtUpStreak);          // clock reset until the -30% is applied

        // A same-day re-run must NOT double-step: the unapplied -30% just stays proposed.
        await NewOrchestrator(db, rows).ExecuteRunAsync("test", isOnDemand: true, layerId);
        state = await db.SkuDeadStockStates.SingleAsync(s => s.Sku == "SKU-DEAD");
        Assert.Equal(30m, state.MarkdownTargetPct);
        Assert.Null(state.MarkdownCaughtUpStreak);
        var latest = await db.ProposedPrices.Where(p => p.Sku == "SKU-DEAD")
            .OrderByDescending(p => p.Id).FirstAsync();
        Assert.Equal(70m, latest.ProposedPriceValue);
    }

    [Fact]
    public async Task Run_ActiveClearanceBatch_HoldsBelowFloorDeadStockPrice_NotRaisedToMarginFloor()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId); // margin floor 10% → cost 40 gives a 44.44 floor

        // An ACTIVE clearance batch pushed a dead-stock SKU BELOW the margin floor (cost 40, pushed 30)
        // and it has SOLD since (SoldUnits ≥ 1 — the lock only pins members the clearance price moved).
        // The end-to-end guardrail wiring must HOLD 30 (deep below-cost clearance is the dead-stock tunnel's
        // domain) rather than raising it to the 44.44 margin floor — the merge defect this fix closes.
        var batch = new ApprovedBatch { LayerId = layerId, Label = "Dead-stock clearance", IsActive = true, PushedUtc = DateTime.UtcNow.Date.AddDays(-7) };
        batch.Items.Add(new ApprovedBatchItem { Sku = "SKU-DEAD", PushedPrice = 30m, StockAtPush = 10, SoldUnits = 2 });
        db.ApprovedBatches.Add(batch);
        db.SaveChanges();

        await NewOrchestrator(db, new List<SnapshotRow> { DeadRow("SKU-DEAD", 100m, 30m, 40m) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        var proposal = await db.ProposedPrices.SingleAsync(p => p.Sku == "SKU-DEAD");
        Assert.Equal(30m, proposal.ProposedPriceValue);                                 // held, not raised to 44.44
        Assert.Contains(GuardrailFlags.ClearanceLockHeld, proposal.GuardrailFlags);
        Assert.DoesNotContain(GuardrailFlags.MarginFloorClamped, proposal.GuardrailFlags);
    }

    [Fact]
    public async Task Run_ActiveClearanceBatch_UnsoldMember_NotPinned_TunnelKeepsWalking()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);

        // An active batch member that has NOT sold since the push (SoldUnits = 0) must NOT be pinned:
        // it stays in the paced dead-stock walk — applied at -25% since streak 76, period elapsed →
        // steps to -30% (70) exactly like an un-batched dead SKU. Pinning unsold members would freeze
        // the entire markdown tunnel at the batch prices; the lock exists to stop a SALE from bouncing
        // the clearance price back up, not to stop the walk.
        var batch = new ApprovedBatch { LayerId = layerId, Label = "Dead-stock clearance", IsActive = true, PushedUtc = DateTime.UtcNow.Date.AddDays(-7) };
        batch.Items.Add(new ApprovedBatchItem { Sku = "SKU-DEAD", PushedPrice = 75m, StockAtPush = 10, SoldUnits = 0 });
        db.ApprovedBatches.Add(batch);
        db.SkuDeadStockStates.Add(new SkuDeadStockState
        {
            LayerId = layerId, Sku = "SKU-DEAD",
            ZeroSaleStreakDays = 90, PrevStreakDays = 89,
            LastSnapshotDate = DateTime.UtcNow.Date.AddDays(-1),
            MarkdownTargetPct = 25m, MarkdownCaughtUpStreak = 76,
        });
        db.SaveChanges();

        await NewOrchestrator(db, new List<SnapshotRow> { DeadRow("SKU-DEAD", 100m, 75m, 40m) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        var proposal = await db.ProposedPrices.SingleAsync(p => p.Sku == "SKU-DEAD");
        Assert.DoesNotContain(GuardrailFlags.ClearanceLockHeld, proposal.GuardrailFlags ?? "");
        Assert.Equal(70m, proposal.ProposedPriceValue);     // stepped -25% → -30%, not pinned at 75
    }

    [Fact]
    public async Task Run_ClearanceLock_SurvivesTheNextBatchBeingPushed_WhileStockRemains()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId); // margin floor 10% → cost 40 gives a 44.44 floor

        // The rollover defect. A SKU marked down to 30 (below the 44.44 floor) SOLD under the markdown,
        // which is exactly why it is absent from the NEXT push: selling took it out of the dead-stock
        // population the new batch was built from. Deactivating the superseded batch used to release the
        // pin, and the margin-floor audit then proposed raising the clearance price with stock still on
        // the shelf. The older batch must keep pinning the members the newer one did not re-price.
        var older = new ApprovedBatch { LayerId = layerId, Label = "push 1", IsActive = true, PushedUtc = DateTime.UtcNow.Date.AddDays(-20) };
        older.Items.Add(new ApprovedBatchItem { Sku = "SKU-DEAD", PushedPrice = 30m, StockAtPush = 10, SoldUnits = 2 });
        var newer = new ApprovedBatch { LayerId = layerId, Label = "push 2", IsActive = true, PushedUtc = DateTime.UtcNow.Date.AddDays(-2) };
        newer.Items.Add(new ApprovedBatchItem { Sku = "SKU-OTHER", PushedPrice = 55m, StockAtPush = 4, SoldUnits = 1 });
        db.ApprovedBatches.AddRange(older, newer);
        db.SaveChanges();

        await NewOrchestrator(db, new List<SnapshotRow> { DeadRow("SKU-DEAD", 100m, 30m, 40m) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        var proposal = await db.ProposedPrices.SingleAsync(p => p.Sku == "SKU-DEAD");
        Assert.Equal(30m, proposal.ProposedPriceValue);          // still held, not raised to the 44.44 floor
        Assert.Contains(GuardrailFlags.ClearanceLockHeld, proposal.GuardrailFlags);
    }

    [Fact]
    public async Task Run_ClearanceLock_NewestBatchWins_WhenTheSkuWasRepushedHigher()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);

        // The SKU sits in both generations at different prices. The pin is the most recent approved
        // decision (50), not the deepest price ever pushed (30) — carrying old pins forward must never
        // override a later one. "Deepest wins" remains the tie-break WITHIN a generation, not across them.
        var older = new ApprovedBatch { LayerId = layerId, Label = "push 1", IsActive = true, PushedUtc = DateTime.UtcNow.Date.AddDays(-20) };
        older.Items.Add(new ApprovedBatchItem { Sku = "SKU-DEAD", PushedPrice = 30m, StockAtPush = 10, SoldUnits = 2 });
        var newer = new ApprovedBatch { LayerId = layerId, Label = "push 2", IsActive = true, PushedUtc = DateTime.UtcNow.Date.AddDays(-2) };
        newer.Items.Add(new ApprovedBatchItem { Sku = "SKU-DEAD", PushedPrice = 50m, StockAtPush = 8, SoldUnits = 1 });
        db.ApprovedBatches.AddRange(older, newer);
        db.SaveChanges();

        await NewOrchestrator(db, new List<SnapshotRow> { DeadRow("SKU-DEAD", 100m, 50m, 40m) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        var proposal = await db.ProposedPrices.SingleAsync(p => p.Sku == "SKU-DEAD");
        Assert.Equal(50m, proposal.ProposedPriceValue);
        Assert.Contains(GuardrailFlags.ClearanceLockHeld, proposal.GuardrailFlags);
    }

    [Fact]
    public async Task Run_ClearanceLock_NeverRaisesAPriceThatHasSinceGoneDeeper()
    {
        using var db= NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);

        // The shopper is already paying 25 — a deeper promo or a later manual markdown — while the batch
        // approved 30. Pegging back up to 30 would RAISE a clearance price on stock we are still clearing,
        // the exact move the lock exists to prevent. The lock is a hold: 25 stands.
        var batch = new ApprovedBatch { LayerId = layerId, Label = "clearance", IsActive = true, PushedUtc = DateTime.UtcNow.Date.AddDays(-7) };
        batch.Items.Add(new ApprovedBatchItem { Sku = "SKU-DEAD", PushedPrice = 30m, StockAtPush = 10, SoldUnits = 2 });
        db.ApprovedBatches.Add(batch);
        db.SaveChanges();

        await NewOrchestrator(db, new List<SnapshotRow> { DeadRow("SKU-DEAD", 100m, 25m, 40m) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        var proposal = await db.ProposedPrices.SingleAsync(p => p.Sku == "SKU-DEAD");
        Assert.Equal(25m, proposal.ProposedPriceValue);
        Assert.Contains(GuardrailFlags.ClearanceLockHeld, proposal.GuardrailFlags);
    }

    [Fact]
    public async Task Run_ClearanceBatchOutsideTrackingWindow_DoesNotLock()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);

        // Past the refresh tracking window the batch's sold/stock figures stop being updated, so the
        // clearance episode is over and the lock must retire with it — otherwise a months-old pushed price
        // would keep overriding current pricing on data nothing refreshes any more.
        var batch = new ApprovedBatch
        {
            LayerId = layerId, Label = "old clearance", IsActive = true,
            PushedUtc = DateTime.UtcNow.Date.AddDays(-(ApprovedBatchRefreshService.RefreshWindowDays + 1)),
        };
        batch.Items.Add(new ApprovedBatchItem { Sku = "SKU-DEAD", PushedPrice = 30m, StockAtPush = 10, SoldUnits = 2 });
        db.ApprovedBatches.Add(batch);
        db.SaveChanges();

        await NewOrchestrator(db, new List<SnapshotRow> { DeadRow("SKU-DEAD", 100m, 30m, 40m) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        var proposal = await db.ProposedPrices.SingleAsync(p => p.Sku == "SKU-DEAD");
        Assert.DoesNotContain(GuardrailFlags.ClearanceLockHeld, proposal.GuardrailFlags ?? "");
    }

    [Fact]
    public async Task Run_InactiveClearanceBatch_DoesNotLock_DeadStockPricesNormally()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);

        // An INACTIVE batch (e.g. the pre-feature dashboard-only batch the migration deactivates) must NOT
        // pin prices: the dead-stock lane prices the SKU normally (opens a paced markdown, no lock flag).
        var batch = new ApprovedBatch { LayerId = layerId, Label = "tracking only", IsActive = false, PushedUtc = DateTime.UtcNow.Date.AddDays(-7) };
        batch.Items.Add(new ApprovedBatchItem { Sku = "SKU-DEAD", PushedPrice = 30m, StockAtPush = 10 });
        db.ApprovedBatches.Add(batch);
        db.SaveChanges();

        await NewOrchestrator(db, new List<SnapshotRow> { DeadRow("SKU-DEAD", 100m, 80m, 40m) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        var proposal = await db.ProposedPrices.SingleAsync(p => p.Sku == "SKU-DEAD");
        Assert.DoesNotContain(GuardrailFlags.ClearanceLockHeld, proposal.GuardrailFlags ?? "");
    }

    [Fact]
    public async Task Run_DeadStock_UnappliedProposal_DoesNotDeepenWithTheStreak()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);

        // Target -25% has been pending for 30 streak days, the shop never applied it (still -20%):
        // the run must re-propose the SAME -25%, not walk deeper on streak growth alone.
        db.SkuDeadStockStates.Add(new SkuDeadStockState
        {
            LayerId = layerId, Sku = "SKU-DEAD",
            ZeroSaleStreakDays = 120, PrevStreakDays = 119,
            LastSnapshotDate = DateTime.UtcNow.Date.AddDays(-1),
            MarkdownTargetPct = 25m, MarkdownCaughtUpStreak = null,
        });
        db.SaveChanges();

        await NewOrchestrator(db, new List<SnapshotRow> { DeadRow("SKU-DEAD", 100m, 80m, 40m) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        var proposal = await db.ProposedPrices.SingleAsync(p => p.Sku == "SKU-DEAD");
        Assert.Equal(75m, proposal.ProposedPriceValue);
        var state = await db.SkuDeadStockStates.SingleAsync(s => s.Sku == "SKU-DEAD");
        Assert.Equal(25m, state.MarkdownTargetPct);
        Assert.Null(state.MarkdownCaughtUpStreak);
    }

    [Fact]
    public async Task Run_DeadStock_SaleClearsPacingState()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);

        db.SkuDeadStockStates.Add(new SkuDeadStockState
        {
            LayerId = layerId, Sku = "SKU-REVIVED",
            ZeroSaleStreakDays = 120, PrevStreakDays = 119,
            LastSnapshotDate = DateTime.UtcNow.Date.AddDays(-1),
            MarkdownTargetPct = 30m, MarkdownCaughtUpStreak = 110,
        });
        db.SaveChanges();

        // It finally sold: the streak resets and the pacing state must reset with it — a future
        // second death re-engages from whatever discount the SKU carries then.
        await NewOrchestrator(db, new List<SnapshotRow> { DeadRow("SKU-REVIVED", 100m, 70m, 40m, qty7: 2) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        var state = await db.SkuDeadStockStates.SingleAsync(s => s.Sku == "SKU-REVIVED");
        Assert.Equal(0, state.ZeroSaleStreakDays);
        Assert.Null(state.MarkdownTargetPct);
        Assert.Null(state.MarkdownCaughtUpStreak);
    }

    // ---- Dead-stock tunnel provenance (SkuDeadStockState.BelowFloorTunnelPrice) ----------------
    //
    // The guardrail's "a below-floor price that started selling again is HELD, never raised" rule reads this
    // marker instead of inferring provenance from "old local stock + selling + below floor" — a shape a SKU
    // also lands in when the FLOOR moves under it (a PPTCV rise), where the right answer is to repair the
    // price, not pin it. Cost 69.3 at the seeded 10% floor ⇒ a floor price of exactly 77.

    [Fact]
    public async Task Run_DeadStock_MarkedDownBelowTheFloor_RecordsTunnelProvenance()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);

        // Dead at -20% (anchor 100, current 80): the paced opening drop is -25% ⇒ 75, which pierces the 77
        // margin floor through the tunnel (stage-1 floor is 100% of the 69.3 cost, so 75 is allowed).
        await NewOrchestrator(db, new List<SnapshotRow> { DeadRow("SKU-DEAD", 100m, 80m, 69.3m) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        var proposal = await db.ProposedPrices.SingleAsync(p => p.Sku == "SKU-DEAD");
        Assert.Equal(75m, proposal.ProposedPriceValue);
        Assert.Contains(GuardrailFlags.DeadStockFloorRelaxed, proposal.GuardrailFlags);

        // The below-floor price the lane now owns, recorded for the runs after the streak ends.
        var state = await db.SkuDeadStockStates.SingleAsync(s => s.LayerId == layerId && s.Sku == "SKU-DEAD");
        Assert.Equal(75m, state.BelowFloorTunnelPrice);
    }

    [Fact]
    public async Task Run_TunnelPrice_SurvivesTheSaleThatEndsTheStreak_AndIsStillHeld()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId, algorithmsEnabled: false);   // nothing votes — the production shape

        db.SkuDeadStockStates.Add(new SkuDeadStockState
        {
            LayerId = layerId, Sku = "SKU-MOVING",
            ZeroSaleStreakDays = 120, PrevStreakDays = 119,
            LastSnapshotDate = DateTime.UtcNow.Date.AddDays(-1),
            MarkdownTargetPct = 25m, MarkdownCaughtUpStreak = 110,
            BelowFloorTunnelPrice = 75m,
        });
        db.SaveChanges();

        // The 75 tunnel price is finally moving units. The pacing state resets with the streak, but the
        // provenance must NOT — a sale is precisely the event the hold has to survive.
        await NewOrchestrator(db, new List<SnapshotRow> { DeadRow("SKU-MOVING", 100m, 75m, 69.3m, qty7: 2) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        var proposal = await db.ProposedPrices.SingleAsync(p => p.Sku == "SKU-MOVING");
        Assert.Equal(75m, proposal.ProposedPriceValue);          // held, not clawed back up to 77
        Assert.False(proposal.HasChange);
        Assert.DoesNotContain(GuardrailFlags.CurrentBelowFloorLifted, proposal.GuardrailFlags);

        var state = await db.SkuDeadStockStates.SingleAsync(s => s.Sku == "SKU-MOVING");
        Assert.Equal(0, state.ZeroSaleStreakDays);
        Assert.Null(state.MarkdownTargetPct);                    // pacing state resets with the streak…
        Assert.Equal(75m, state.BelowFloorTunnelPrice);          // …provenance does not
    }

    [Fact]
    public async Task Run_SellingBelowFloor_WithoutTunnelProvenance_IsLiftedToTheFloor()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId, algorithmsEnabled: false);

        // THE REGRESSION, end to end: the identical selling-below-floor row with no provenance — the price
        // never moved, the floor did (cost rose to 69.3). This used to be pinned at 75 and written with no
        // flags at all; it must now be repaired to the 77 floor and say so.
        await NewOrchestrator(db, new List<SnapshotRow> { DeadRow("SKU-COSTROSE", 100m, 75m, 69.3m, qty7: 2) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        var proposal = await db.ProposedPrices.SingleAsync(p => p.Sku == "SKU-COSTROSE");
        Assert.Equal(77m, proposal.ProposedPriceValue);
        Assert.True(proposal.HasChange);
        Assert.Contains(GuardrailFlags.CurrentBelowFloorLifted, proposal.GuardrailFlags);
        Assert.DoesNotContain(GuardrailFlags.DeadStockTunnelHeld, proposal.GuardrailFlags);
    }

    [Fact]
    public async Task Run_TunnelProvenance_IsRetired_OnceThePriceIsBackAtTheFloor()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId, algorithmsEnabled: false);

        db.SkuDeadStockStates.Add(new SkuDeadStockState
        {
            LayerId = layerId, Sku = "SKU-RECOVERED",
            ZeroSaleStreakDays = 0, PrevStreakDays = 0,
            LastSnapshotDate = DateTime.UtcNow.Date.AddDays(-1),
            BelowFloorTunnelPrice = 75m,
        });
        db.SaveChanges();

        // Priced back at 90 — at/above the 77 floor, so the tunnel is over and the marker must not linger
        // (a stale marker would exempt this SKU from the floor forever after its next cost rise).
        await NewOrchestrator(db, new List<SnapshotRow> { DeadRow("SKU-RECOVERED", 100m, 90m, 69.3m, qty7: 2) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        var state = await db.SkuDeadStockStates.SingleAsync(s => s.Sku == "SKU-RECOVERED");
        Assert.Null(state.BelowFloorTunnelPrice);
    }

    [Fact]
    public async Task Run_SlowLaneFrozen_DeadStock_HoldsAppliedTarget_DoesNotStep_NorAdvanceState()
    {
        using var db = NewDb();
        // Frozen: the slow lane last ticked TODAY, so this daily run is inside the 15-day window.
        var tick = DateTime.UtcNow.Date;
        var layerId = SeedLayer(db, lastSlowLaneRunUtc: tick);
        SeedBand(db, layerId);

        // Applied at -25% since streak 76; the run sees streak 90 → 91 (past the 14-day period). On a DUE
        // run this steps to -30%/70 (see Run_DeadStock_AppliedAndPeriodElapsed_StepsOneDeeper). Frozen, it
        // must HOLD -25%/75 and leave the pacing state untouched.
        db.SkuDeadStockStates.Add(new SkuDeadStockState
        {
            LayerId = layerId, Sku = "SKU-DEAD",
            ZeroSaleStreakDays = 90, PrevStreakDays = 89,
            LastSnapshotDate = DateTime.UtcNow.Date.AddDays(-1),
            MarkdownTargetPct = 25m, MarkdownCaughtUpStreak = 76,
        });
        db.SaveChanges();

        await NewOrchestrator(db, new List<SnapshotRow> { DeadRow("SKU-DEAD", 100m, 75m, 40m) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        var proposal = await db.ProposedPrices.SingleAsync(p => p.Sku == "SKU-DEAD");
        Assert.Equal(75m, proposal.ProposedPriceValue);              // held, NOT stepped to 70
        Assert.Contains("DEAD_STOCK_MARKDOWN", proposal.ReasonCodes);

        var state = await db.SkuDeadStockStates.SingleAsync(s => s.Sku == "SKU-DEAD");
        Assert.Equal(25m, state.MarkdownTargetPct);                  // target NOT advanced to 30
        Assert.Equal(76, state.MarkdownCaughtUpStreak);             // clock preserved

        var layer = await db.Layers.SingleAsync(l => l.Id == layerId);
        Assert.Equal(tick, layer.LastSlowLaneRunUtc);               // a frozen run does not re-stamp the tick
    }

    [Fact]
    public async Task Run_SlowLaneFrozen_ApprovedTargetNowLive_StartsThePeriodClock()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db, lastSlowLaneRunUtc: DateTime.UtcNow.Date);   // frozen
        SeedBand(db, layerId);

        // Today's-cycle shape: the tick proposed -25% and reset the clock (caught NULL); the price was
        // pushed AFTER the tick run, so only an off-tick daily run can observe it applied. The frozen
        // run must stamp the clock start — and ONLY that: target unchanged, no step (even though the
        // streak is way past the period), tick stamp untouched.
        db.SkuDeadStockStates.Add(new SkuDeadStockState
        {
            LayerId = layerId, Sku = "SKU-DEAD",
            ZeroSaleStreakDays = 90, PrevStreakDays = 89,
            LastSnapshotDate = DateTime.UtcNow.Date.AddDays(-1),
            MarkdownTargetPct = 25m, MarkdownCaughtUpStreak = null,
        });
        db.SaveChanges();

        await NewOrchestrator(db, new List<SnapshotRow> { DeadRow("SKU-DEAD", 100m, 75m, 40m) })   // shop AT the -25% target
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        var proposal = await db.ProposedPrices.SingleAsync(p => p.Sku == "SKU-DEAD");
        Assert.Equal(75m, proposal.ProposedPriceValue);              // held — no step on a frozen run

        var state = await db.SkuDeadStockStates.SingleAsync(s => s.Sku == "SKU-DEAD");
        Assert.Equal(25m, state.MarkdownTargetPct);                  // target NOT moved
        Assert.Equal(91, state.MarkdownCaughtUpStreak);              // clock STARTED at today's streak
    }

    [Fact]
    public async Task Run_SlowLaneFrozen_TargetNotLiveYet_DoesNotStartTheClock()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db, lastSlowLaneRunUtc: DateTime.UtcNow.Date);   // frozen
        SeedBand(db, layerId);

        // Same shape but the push has NOT landed (shop still at -20% vs the -25% target): the frozen
        // run must persist nothing — no clock, no re-anchored target.
        db.SkuDeadStockStates.Add(new SkuDeadStockState
        {
            LayerId = layerId, Sku = "SKU-DEAD",
            ZeroSaleStreakDays = 90, PrevStreakDays = 89,
            LastSnapshotDate = DateTime.UtcNow.Date.AddDays(-1),
            MarkdownTargetPct = 25m, MarkdownCaughtUpStreak = null,
        });
        db.SaveChanges();

        await NewOrchestrator(db, new List<SnapshotRow> { DeadRow("SKU-DEAD", 100m, 80m, 40m) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        var state = await db.SkuDeadStockStates.SingleAsync(s => s.Sku == "SKU-DEAD");
        Assert.Equal(25m, state.MarkdownTargetPct);
        Assert.Null(state.MarkdownCaughtUpStreak);
    }

    [Fact]
    public async Task Run_SlowLaneFrozen_NewlyDeadSku_Abstains_HoldsCurrentPrice()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db, lastSlowLaneRunUtc: DateTime.UtcNow.Date);   // frozen
        SeedBand(db, layerId);

        // No prior pacing state: the SKU just went dead. Frozen, the dead-stock lane must NOT engage a
        // fresh markdown — the price holds at the current 80 until the next 15-day tick.
        await NewOrchestrator(db, new List<SnapshotRow> { DeadRow("SKU-DEAD", 100m, 80m, 40m) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        var proposal = await db.ProposedPrices.Include(p => p.Votes).SingleAsync(p => p.Sku == "SKU-DEAD");
        Assert.Equal(80m, proposal.ProposedPriceValue);                    // unchanged — no engage
        Assert.False(proposal.HasChange);
        Assert.DoesNotContain(proposal.Votes, v => v.AlgorithmCode == AlgorithmCodes.DeadStock);

        var state = await db.SkuDeadStockStates.SingleAsync(s => s.Sku == "SKU-DEAD");
        Assert.Null(state.MarkdownTargetPct);                              // never engaged
    }

    [Fact]
    public async Task Run_SlowLaneDue_DeadStock_Engages_AndStampsTheTick()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db, lastSlowLaneRunUtc: null);   // never ticked → DUE
        SeedBand(db, layerId);

        var before = DateTime.UtcNow;
        await NewOrchestrator(db, new List<SnapshotRow> { DeadRow("SKU-DEAD", 100m, 80m, 40m) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        var proposal = await db.ProposedPrices.SingleAsync(p => p.Sku == "SKU-DEAD");
        Assert.Equal(75m, proposal.ProposedPriceValue);              // opened one step below current (-25%)
        var state = await db.SkuDeadStockStates.SingleAsync(s => s.Sku == "SKU-DEAD");
        Assert.Equal(25m, state.MarkdownTargetPct);

        var layer = await db.Layers.SingleAsync(l => l.Id == layerId);
        Assert.NotNull(layer.LastSlowLaneRunUtc);                    // the due tick was stamped…
        Assert.True(layer.LastSlowLaneRunUtc >= before.AddSeconds(-1)); // …at ~now
    }

    [Fact]
    public async Task Run_FloorAndRoundingOnly_NeverAdvancesPacingState()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db, floorAndRoundingOnly: true);
        SeedBand(db, layerId);

        await NewOrchestrator(db, new List<SnapshotRow> { DeadRow("SKU-DEAD", 100m, 80m, 40m) })
            .ExecuteRunAsync("test", isOnDemand: true, layerId);

        // Algorithms are off for the layer — no markdown proposed, so no pacing state may accrue.
        var state = await db.SkuDeadStockStates.SingleAsync(s => s.Sku == "SKU-DEAD");
        Assert.Null(state.MarkdownTargetPct);
        var proposal = await db.ProposedPrices.SingleAsync(p => p.Sku == "SKU-DEAD");
        Assert.Equal(80m, proposal.ProposedPriceValue);
    }

    /// <summary>Loads the single seeded band (with its algorithm settings) for the layer.</summary>
    private static PriceBand BandOf(PricingToolDbContext db, int layerId) =>
        db.PriceBands.Include(b => b.AlgorithmSettings).Single(b => b.LayerId == layerId);

    [Fact]
    public async Task Run_MarginTarget_AdjustsAlgorithmSilentSkus_TowardBandGoal()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);   // MarginFloorPct 10, all algorithms enabled, rounding off

        // Turn the algorithms OFF for the band (per-band toggles) and give the band a 25% goal: every
        // SKU is now algorithm-silent ("free"), so the guardrail trims them all toward 25%.
        var band = BandOf(db, layerId);
        band.TargetMarginPct = 25m;
        foreach (var s in band.AlgorithmSettings) s.Enabled = false;
        db.SaveChanges();

        // Two SKUs at 50% margin (anchor 200, price 100, cost 50); blended 50% is well over the 25% goal.
        var rows = new List<SnapshotRow> { Row("SKU-1", 200m, 100m, 50m), Row("SKU-2", 200m, 100m, 50m) };
        var run = await NewOrchestrator(db, rows).ExecuteRunAsync("test", isOnDemand: true, layerId);
        Assert.Equal(RunStatus.Succeeded, run.Status);

        var proposals = await db.ProposedPrices.AsNoTracking().ToListAsync();
        Assert.Equal(2, proposals.Count);
        Assert.All(proposals, p => Assert.Contains("MARGIN_TARGET_ADJUSTED", p.GuardrailFlags));
        Assert.All(proposals, p => Assert.Contains("MARGIN_TARGET_TRIM", p.ReasonCodes));
        Assert.All(proposals, p => Assert.True(p.HasChange));
        // Trimmed below the original 100 but never below the ~55.56 floor (cost 50 @ 10% floor).
        Assert.All(proposals, p => Assert.InRange(p.ProposedPriceValue, 55.56m, 99.99m));
        Assert.InRange(BlendedMargin(proposals), 24.5m, 25.5m);
    }

    [Fact]
    public async Task Run_MarginTarget_WorksWhenLayerAlgorithmsOff_FloorAndRoundingOnly()
    {
        using var db = NewDb();
        // Algorithms OFF at the LAYER level (FloorAndRoundingOnly). The margin guardrail must still run.
        var layerId = SeedLayer(db, floorAndRoundingOnly: true);
        SeedBand(db, layerId);

        var band = BandOf(db, layerId);
        band.TargetMarginPct = 25m;
        db.SaveChanges();

        var rows = new List<SnapshotRow> { Row("SKU-1", 200m, 100m, 50m), Row("SKU-2", 200m, 100m, 50m) };
        var run = await NewOrchestrator(db, rows).ExecuteRunAsync("test", isOnDemand: true, layerId);
        Assert.Equal(RunStatus.Succeeded, run.Status);

        var proposals = await db.ProposedPrices.AsNoTracking().ToListAsync();
        Assert.Equal(2, proposals.Count);
        Assert.All(proposals, p => Assert.Contains("MARGIN_TARGET_ADJUSTED", p.GuardrailFlags));
        Assert.All(proposals, p => Assert.InRange(p.ProposedPriceValue, 55.56m, 99.99m));
        Assert.InRange(BlendedMargin(proposals), 24.5m, 25.5m);
    }

    [Fact]
    public async Task Run_MarginTarget_MeasuresFromAnchor_NotCurrentPrice()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);   // MarginFloorPct 10, rounding off

        var band = BandOf(db, layerId);
        band.TargetMarginPct = 25m;
        foreach (var s in band.AlgorithmSettings) s.Enabled = false;   // every SKU is algorithm-silent ("free")
        db.SaveChanges();

        // SKU-HI's CURRENT price (150) sits ABOVE its anchor/list (100) — the real "tier price over FinalPrice"
        // case. Its ANCHOR margin is (100−80)/100 = 20%, BELOW the 25% goal, so the anchor-based guardrail must
        // leave it alone. A naive current-price basis would read (150−80)/150 ≈ 47% (over goal) and trim it.
        // SKU-LO is a plain over-goal SKU (anchor margin 75%) that DOES get trimmed toward 25%, for contrast.
        // Row(sku, oldPrice→anchor fallback, current, cost).
        var rows = new List<SnapshotRow> { Row("SKU-HI", 100m, 150m, 80m), Row("SKU-LO", 200m, 100m, 50m) };
        var run = await NewOrchestrator(db, rows).ExecuteRunAsync("test", isOnDemand: true, layerId);
        Assert.Equal(RunStatus.Succeeded, run.Status);

        var props = await db.ProposedPrices.AsNoTracking().ToListAsync();
        var hi = props.Single(p => p.Sku == "SKU-HI");
        var lo = props.Single(p => p.Sku == "SKU-LO");

        // Anchor basis: SKU-HI is UNDER goal at its list price, so it is never trimmed (a current-price basis would).
        Assert.DoesNotContain("MARGIN_TARGET", hi.ReasonCodes ?? "");
        Assert.Equal(150m, hi.ProposedPriceValue);
        Assert.False(hi.HasChange);

        // SKU-LO is genuinely over goal at its anchor, so it is trimmed toward the band target.
        Assert.Contains("MARGIN_TARGET_TRIM", lo.ReasonCodes);
        Assert.True(lo.ProposedPriceValue < 100m);
    }

    [Fact]
    public async Task Run_NoBandTarget_LeavesAlgorithmSilentSkusUnchanged()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId);

        var band = BandOf(db, layerId);            // algorithms off, but NO target set
        foreach (var s in band.AlgorithmSettings) s.Enabled = false;
        db.SaveChanges();

        var rows = new List<SnapshotRow> { Row("SKU-1", 200m, 100m, 50m) };
        await NewOrchestrator(db, rows).ExecuteRunAsync("test", isOnDemand: true, layerId);

        var p = await db.ProposedPrices.AsNoTracking().SingleAsync();
        Assert.DoesNotContain("MARGIN_TARGET", p.GuardrailFlags ?? "");
        Assert.Equal(100m, p.ProposedPriceValue);  // unchanged: no algorithm, no target
        Assert.False(p.HasChange);
    }

    // --- Sync-to-Gj50: a GjirafaMall run adopts the same-country Gjirafa50 layer's latest proposals. ---

    /// <summary>Adds the same-country Gjirafa50 sibling layer and returns its id.</summary>
    private static int SeedGj50Layer(PricingToolDbContext db)
    {
        var l = new Layer
        {
            Brand = "Gjirafa50", CountryCode = "KS", DisplayName = "Gj50 — KS",
            OperationalDatabase = "GjirafaEcommerce", StoreId = 2, TranslationCountryId = 1,
            WarehouseStoreId = 2, Currency = "EUR", IsActive = true, SortOrder = 3,
        };
        db.Layers.Add(l);
        db.SaveChanges();
        return l.Id;
    }

    /// <summary>Seeds a completed Gjirafa50 run with the given proposals (the map Mall would adopt).</summary>
    private static void SeedGj50Run(PricingToolDbContext db, int gj50LayerId, RunStatus status,
        params (string Sku, decimal Price, bool HasChange, ProposalStatus Status)[] proposals)
    {
        var run = new PricingRun
        {
            LayerId = gj50LayerId, StartedUtc = DateTime.UtcNow.AddMinutes(-30),
            FinishedUtc = DateTime.UtcNow.AddMinutes(-25), Status = status,
        };
        db.PricingRuns.Add(run);
        db.SaveChanges();
        foreach (var p in proposals)
            db.ProposedPrices.Add(new ProposedPrice
            {
                PricingRunId = run.Id, LayerId = gj50LayerId, Sku = p.Sku,
                ProposedPriceValue = p.Price, CurrentPrice = p.Price, HasChange = p.HasChange, Status = p.Status,
            });
        db.SaveChanges();
    }

    [Fact]
    public async Task Run_SyncsToGj50_AdoptsSiblingProposalVerbatim_SkippingMallAlgorithms()
    {
        using var db = NewDb();
        var mallId = SeedLayer(db);          // GjirafaMall — KS
        SeedBand(db, mallId);
        var gj50Id = SeedGj50Layer(db);
        SeedGj50Run(db, gj50Id, RunStatus.Succeeded, ("SKU-SHARED", 47.50m, true, ProposalStatus.Pending));

        var rows = new List<SnapshotRow>
        {
            Row("SKU-SHARED", 100m, 80m, 40m),    // Mall could price it, but Gj50 owns the price
            Row("SKU-MALLONLY", 100m, 80m, 40m),  // not in Gj50 → Mall prices it normally
        };
        var run = await NewOrchestrator(db, rows).ExecuteRunAsync("test", isOnDemand: true, mallId);
        Assert.Equal(RunStatus.Succeeded, run.Status);

        var props = await db.ProposedPrices.Include(p => p.Votes).AsNoTracking()
            .Where(p => p.LayerId == mallId).ToListAsync();

        var shared = props.Single(p => p.Sku == "SKU-SHARED");
        Assert.Equal(47.50m, shared.ProposedPriceValue);        // adopted Gj50's price verbatim
        Assert.Contains("SYNCED_TO_GJ50", shared.ReasonCodes);
        Assert.Empty(shared.Votes);                             // Mall's algorithms did not run
        Assert.True(shared.HasChange);                          // 47.50 vs current 80

        var mallOnly = props.Single(p => p.Sku == "SKU-MALLONLY");
        Assert.DoesNotContain("SYNCED_TO_GJ50", mallOnly.ReasonCodes ?? "");
        Assert.NotEmpty(mallOnly.Votes);                        // priced by Mall's own algorithms
    }

    [Fact]
    public async Task Run_SyncsToGj50_CarriesOverGj50DecisionReasonCodes()
    {
        using var db = NewDb();
        var mallId = SeedLayer(db);          // GjirafaMall — KS
        SeedBand(db, mallId);
        var gj50Id = SeedGj50Layer(db);

        // Seed a Gjirafa50 run whose adopted proposal was driven by a specific algorithm reason.
        var gj50Run = new PricingRun
        {
            LayerId = gj50Id, StartedUtc = DateTime.UtcNow.AddMinutes(-30),
            FinishedUtc = DateTime.UtcNow.AddMinutes(-25), Status = RunStatus.Succeeded,
        };
        db.PricingRuns.Add(gj50Run);
        db.SaveChanges();
        db.ProposedPrices.Add(new ProposedPrice
        {
            PricingRunId = gj50Run.Id, LayerId = gj50Id, Sku = "SKU-SHARED",
            ProposedPriceValue = 47.50m, CurrentPrice = 47.50m, HasChange = true,
            Status = ProposalStatus.Pending, ReasonCodes = "SELL_THROUGH_SLOW",
        });
        db.SaveChanges();

        var rows = new List<SnapshotRow> { Row("SKU-SHARED", 100m, 80m, 40m) };
        await NewOrchestrator(db, rows).ExecuteRunAsync("test", isOnDemand: true, mallId);

        var shared = await db.ProposedPrices.AsNoTracking()
            .SingleAsync(p => p.LayerId == mallId && p.Sku == "SKU-SHARED");
        Assert.Equal(47.50m, shared.ProposedPriceValue);
        Assert.StartsWith("SYNCED_TO_GJ50", shared.ReasonCodes);   // synced flag stays first…
        Assert.Contains("SELL_THROUGH_SLOW", shared.ReasonCodes);  // …then Gj50's own decision reason
    }

    [Fact]
    public async Task Run_SyncsToGj50_StampsGj50EcommerceProductId_NotMallCatalogId()
    {
        using var db = NewDb();
        var mallId = SeedLayer(db);          // GjirafaMall — KS
        SeedBand(db, mallId);
        var gj50Id = SeedGj50Layer(db);

        // Gj50 proposal carries its GjirafaEcommerce catalog id (555); Mall's own catalog id for the
        // same SKU is different (111). A synced row must surface the Ecommerce id, since the product is
        // primarily a Gjirafa50 product. A Mall-only SKU keeps Mall's own id.
        var gj50Run = new PricingRun
        {
            LayerId = gj50Id, StartedUtc = DateTime.UtcNow.AddMinutes(-30),
            FinishedUtc = DateTime.UtcNow.AddMinutes(-25), Status = RunStatus.Succeeded,
        };
        db.PricingRuns.Add(gj50Run);
        db.SaveChanges();
        db.ProposedPrices.Add(new ProposedPrice
        {
            PricingRunId = gj50Run.Id, LayerId = gj50Id, Sku = "SKU-SHARED", ProductId = 555,
            ProposedPriceValue = 47.50m, CurrentPrice = 47.50m, HasChange = true, Status = ProposalStatus.Pending,
        });
        db.SaveChanges();

        var rows = new List<SnapshotRow>
        {
            Row("SKU-SHARED", 100m, 80m, 40m, productId: 111),   // Mall id 111, but Gj50 owns it
            Row("SKU-MALLONLY", 100m, 80m, 40m, productId: 222), // priced by Mall → keeps id 222
        };
        await NewOrchestrator(db, rows).ExecuteRunAsync("test", isOnDemand: true, mallId);

        var shared = await db.ProposedPrices.AsNoTracking()
            .SingleAsync(p => p.LayerId == mallId && p.Sku == "SKU-SHARED");
        Assert.Equal(555, shared.ProductId);      // adopted the Gj50 (Ecommerce) id, not Mall's 111
        Assert.Contains("SYNCED_TO_GJ50", shared.ReasonCodes);

        var mallOnly = await db.ProposedPrices.AsNoTracking()
            .SingleAsync(p => p.LayerId == mallId && p.Sku == "SKU-MALLONLY");
        Assert.Equal(222, mallOnly.ProductId);    // untouched: Mall's own catalog id
    }

    [Fact]
    public async Task Run_SyncsToGj50_AdoptsEvenBelowMallMarginFloor()
    {
        using var db = NewDb();
        var mallId = SeedLayer(db);
        SeedBand(db, mallId);                // MarginFloorPct 10 → cost 40 floors at ~44.44
        var gj50Id = SeedGj50Layer(db);
        // Gj50 priced it at 30 — below Mall's floor AND below cost. Verbatim adoption ignores Mall's floor.
        SeedGj50Run(db, gj50Id, RunStatus.Succeeded, ("SKU-CHEAP", 30m, true, ProposalStatus.Pending));

        var rows = new List<SnapshotRow> { Row("SKU-CHEAP", 100m, 80m, 40m) };
        await NewOrchestrator(db, rows).ExecuteRunAsync("test", isOnDemand: true, mallId);

        var p = await db.ProposedPrices.AsNoTracking().SingleAsync(x => x.LayerId == mallId && x.Sku == "SKU-CHEAP");
        Assert.Equal(30m, p.ProposedPriceValue);                // no floor clamp — Gj50 is primary
        Assert.Contains("SYNCED_TO_GJ50", p.ReasonCodes);
    }

    [Fact]
    public async Task Run_SyncsToGj50_NoSuccessfulGj50Run_PricesNormally()
    {
        using var db = NewDb();
        var mallId = SeedLayer(db);
        SeedBand(db, mallId);
        var gj50Id = SeedGj50Layer(db);
        // Only a FAILED Gj50 run exists — its (partial) proposals must NOT be adopted.
        SeedGj50Run(db, gj50Id, RunStatus.Failed, ("SKU-SHARED", 47.50m, true, ProposalStatus.Pending));

        var rows = new List<SnapshotRow> { Row("SKU-SHARED", 100m, 80m, 40m) };
        await NewOrchestrator(db, rows).ExecuteRunAsync("test", isOnDemand: true, mallId);

        var p = await db.ProposedPrices.Include(x => x.Votes).AsNoTracking()
            .SingleAsync(x => x.LayerId == mallId);
        Assert.DoesNotContain("SYNCED_TO_GJ50", p.ReasonCodes ?? "");
        Assert.NotEqual(47.50m, p.ProposedPriceValue);          // priced by Mall, not adopted
        Assert.NotEmpty(p.Votes);
    }

    [Fact]
    public async Task Run_SyncsToGj50_AdoptsUnchanged_ButNotSkipped()
    {
        using var db = NewDb();
        var mallId = SeedLayer(db);
        SeedBand(db, mallId);
        var gj50Id = SeedGj50Layer(db);
        SeedGj50Run(db, gj50Id, RunStatus.Succeeded,
            ("SKU-UNCHANGED", 60m, false, ProposalStatus.Pending),   // Gj50 left it unchanged → still adopted
            ("SKU-SKIPPED", 0m, false, ProposalStatus.Skipped));     // Gj50 skipped it → NOT adopted

        var rows = new List<SnapshotRow>
        {
            Row("SKU-UNCHANGED", 100m, 80m, 40m),
            Row("SKU-SKIPPED", 100m, 80m, 40m),
        };
        await NewOrchestrator(db, rows).ExecuteRunAsync("test", isOnDemand: true, mallId);

        var props = await db.ProposedPrices.Include(p => p.Votes).AsNoTracking()
            .Where(p => p.LayerId == mallId).ToListAsync();

        var unchanged = props.Single(p => p.Sku == "SKU-UNCHANGED");
        Assert.Equal(60m, unchanged.ProposedPriceValue);          // adopted despite Gj50 not changing it
        Assert.Contains("SYNCED_TO_GJ50", unchanged.ReasonCodes);

        var skipped = props.Single(p => p.Sku == "SKU-SKIPPED");
        Assert.DoesNotContain("SYNCED_TO_GJ50", skipped.ReasonCodes ?? "");  // Gj50 skipped → Mall prices it
        Assert.NotEmpty(skipped.Votes);
    }

    /// <summary>
    /// The margin-floor audit's verdict must survive the margin-target post-pass. That pass REPLACES the
    /// whole decision (PriceCalculator.Reprice), which used to drop CURRENT_BELOW_FLOOR_LIFTED — so a
    /// below-floor SKU that margin-target happened to move fell out of the Guardrails filter for
    /// below-floor repairs, even though its price was still lifted above the floor.
    /// </summary>
    [Fact]
    public async Task BelowFloorLift_SurvivesTheMarginTargetOverride()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        // Target 20% with every algorithm off: nothing votes (so the floor audit is the only thing that
        // can price the SKU) and every SKU is "free" for the margin-target pass to move.
        SeedBand(db, layerId, targetMarginPct: 20m, algorithmsEnabled: false);

        // Supplier-only (so the dead-stock tunnel doesn't hold it), anchor 100, cost 50, selling at 40:
        // a 10% floor puts the floor price at 55.56, so the current price is well under it. At the anchor
        // it shows a 50% margin, far above the 20% band goal — so margin-target also wants to trim it.
        var row = new SnapshotRow
        {
            Sku = "SKU-BELOW-FLOOR", OldPrice = 100m, AnchorPrice = 100m, CurrentPrice = 40m, Pptcv = 50m,
            LocalWarehouseStock = 0, SupplierWarehouseStock = 5,
            Qty7 = 7, Net7 = 50, Qty14 = 14, Net14 = 100, Qty30 = 30, Net30 = 220,
            Qty60 = 60, Net60 = 440, Qty90 = 90, Net90 = 660,
        };

        await NewOrchestrator(db, new[] { row }).ExecuteRunAsync("test", isOnDemand: true, layerId);

        var p = await db.ProposedPrices.AsNoTracking().SingleAsync(x => x.Sku == "SKU-BELOW-FLOOR");

        // Margin-target authored the number (it trimmed the anchor to the band goal, 50 / 0.8 = 62.50)...
        Assert.Equal(62.50m, p.ProposedPriceValue);
        Assert.Contains(GuardrailFlags.MarginTargetAdjusted, p.GuardrailFlags);
        // ...but the below-floor repair is still recorded, so the Guardrails filter finds this row.
        Assert.Contains(GuardrailFlags.CurrentBelowFloorLifted, p.GuardrailFlags);
        // And the proposal genuinely clears the floor (50 / 0.9 = 55.56).
        Assert.True(p.ProposedPriceValue > 55.55m, $"proposed {p.ProposedPriceValue} is under the margin floor");
    }

    [Fact]
    public async Task AboveFloorSku_MovedByMarginTarget_IsNotLabelledAsABelowFloorLift()
    {
        using var db = NewDb();
        var layerId = SeedLayer(db);
        SeedBand(db, layerId, targetMarginPct: 20m, algorithmsEnabled: false);

        // Same shape, but selling at 90 — comfortably above the 55.56 floor. Margin-target still trims it,
        // and the row must NOT claim to be a below-floor repair.
        var row = new SnapshotRow
        {
            Sku = "SKU-ABOVE-FLOOR", OldPrice = 100m, AnchorPrice = 100m, CurrentPrice = 90m, Pptcv = 50m,
            LocalWarehouseStock = 0, SupplierWarehouseStock = 5,
            Qty7 = 7, Net7 = 50, Qty14 = 14, Net14 = 100, Qty30 = 30, Net30 = 220,
            Qty60 = 60, Net60 = 440, Qty90 = 90, Net90 = 660,
        };

        await NewOrchestrator(db, new[] { row }).ExecuteRunAsync("test", isOnDemand: true, layerId);

        var p = await db.ProposedPrices.AsNoTracking().SingleAsync(x => x.Sku == "SKU-ABOVE-FLOOR");

        Assert.Contains(GuardrailFlags.MarginTargetAdjusted, p.GuardrailFlags);
        Assert.DoesNotContain(GuardrailFlags.CurrentBelowFloorLifted, p.GuardrailFlags);
    }

    private static decimal BlendedMargin(IReadOnlyList<ProposedPrice> proposals)
    {
        var sumPrice = proposals.Sum(p => p.ProposedPriceValue);
        var sumCost = proposals.Sum(p => p.Pptcv!.Value);
        return (sumPrice - sumCost) / sumPrice * 100m;
    }
}
