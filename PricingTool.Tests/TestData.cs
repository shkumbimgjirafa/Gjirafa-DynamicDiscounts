using PricingTool.Core.Algorithms;
using PricingTool.Core.Domain;
using PricingTool.Core.Options;

namespace PricingTool.Tests;

/// <summary>Builders for SkuContext / band config with sensible defaults each test overrides.</summary>
public static class TestData
{
    public static PriceBandConfig Band(
        decimal marginFloorPct = 10m,
        RoundingConvention rounding = RoundingConvention.None,
        bool roundingEnabled = false,
        Dictionary<string, BandAlgorithmConfig>? algorithms = null,
        int deadStockNoSaleWindowDays = 90,
        int deadStockMinStockAgeDays = 90,
        decimal deadStockStartDiscountPct = 10m,
        decimal deadStockStepDiscountPct = 5m,
        int deadStockPeriodDays = 14,
        decimal deadStockFloorCostPct = 100m,
        int deadStockStage2AgeDays = 180,
        decimal deadStockStage2FloorCostPct = 80m,
        decimal deadStockStage2StepDiscountPct = 5m,
        int deadStockStage2PeriodDays = 14,
        int deadStockStage3AgeDays = 360,
        decimal deadStockStage3FloorCostPct = 60m,
        decimal deadStockStage3StepDiscountPct = 5m,
        int deadStockStage3PeriodDays = 14,
        int deadStockStage4AgeDays = 1080,
        decimal deadStockStage4FloorCostPct = 40m,
        decimal deadStockStage4StepDiscountPct = 5m,
        int deadStockStage4PeriodDays = 14)
    {
        algorithms ??= AlgorithmCodes.All.ToDictionary(
            a => a.Code, a => new BandAlgorithmConfig(true, a.DefaultWeight));

        return new PriceBandConfig
        {
            BandId = 1,
            Name = "test-band",
            MinPrice = 0,
            MaxPrice = 999999,
            MarginFloorPct = marginFloorPct,
            DeadStockNoSaleWindowDays = deadStockNoSaleWindowDays,
            DeadStockMinStockAgeDays = deadStockMinStockAgeDays,
            DeadStockStartDiscountPct = deadStockStartDiscountPct,
            DeadStockStepDiscountPct = deadStockStepDiscountPct,
            DeadStockPeriodDays = deadStockPeriodDays,
            DeadStockFloorCostPct = deadStockFloorCostPct,
            DeadStockStage2AgeDays = deadStockStage2AgeDays,
            DeadStockStage2FloorCostPct = deadStockStage2FloorCostPct,
            DeadStockStage2StepDiscountPct = deadStockStage2StepDiscountPct,
            DeadStockStage2PeriodDays = deadStockStage2PeriodDays,
            DeadStockStage3AgeDays = deadStockStage3AgeDays,
            DeadStockStage3FloorCostPct = deadStockStage3FloorCostPct,
            DeadStockStage3StepDiscountPct = deadStockStage3StepDiscountPct,
            DeadStockStage3PeriodDays = deadStockStage3PeriodDays,
            DeadStockStage4AgeDays = deadStockStage4AgeDays,
            DeadStockStage4FloorCostPct = deadStockStage4FloorCostPct,
            DeadStockStage4StepDiscountPct = deadStockStage4StepDiscountPct,
            DeadStockStage4PeriodDays = deadStockStage4PeriodDays,
            Rounding = rounding,
            RoundingEnabled = roundingEnabled,
            Algorithms = algorithms,
        };
    }

    public static SkuContext Ctx(
        decimal oldPrice = 100m,
        decimal currentPrice = 100m,
        decimal? pptcv = 40m,
        decimal? grossMarginPct = null,
        int ksStock = 10,
        int supplierStock = 0,
        int qty7 = 0, int qty14 = 0, int qty30 = 0, int qty60 = 0, int qty90 = 0,
        decimal? disc7 = null, decimal? disc14 = null, decimal? disc30 = null,
        decimal? disc60 = null, decimal? disc90 = null,
        DateTime? launchDateUtc = null,
        int zeroSaleStreakDays = 0,
        PriceBandConfig? band = null,
        PricingEngineOptions? options = null,
        bool roundingDisabledForSku = false,
        decimal? anchorPrice = null,
        decimal? elasticity = null,
        decimal vatRatePct = 18m,
        bool isNewProduct = false,
        decimal? elasticityStdError = null,
        int? oldestUnitAgeDays = null,
        int deadObservedDays = 0,
        bool floorAndRoundingOnly = false,
        bool slowLanesFrozen = false,
        decimal? deadStockPacedTarget = null,
        int? deadStockPacedCaughtUpStreak = null,
        decimal? clearanceLockPrice = null,
        decimal? belowFloorTunnelPrice = null,
        IReadOnlyList<DiscountTrimRange>? discountTrimRanges = null)
    {
        return new SkuContext
        {
            Sku = "TEST-SKU",
            AnchorPrice = anchorPrice ?? oldPrice,
            OldPrice = oldPrice,
            CurrentPrice = currentPrice,
            Pptcv = pptcv,
            GrossMarginPct = grossMarginPct,
            Elasticity = elasticity,
            ElasticityStdError = elasticityStdError,
            KsStock = ksStock,
            SupplierStock = supplierStock,
            Qty7 = qty7, Qty14 = qty14, Qty30 = qty30, Qty60 = qty60, Qty90 = qty90,
            Net7 = 0, Net14 = 0, Net30 = 0, Net60 = 0, Net90 = 0,
            Disc7 = disc7, Disc14 = disc14, Disc30 = disc30, Disc60 = disc60, Disc90 = disc90,
            LaunchDateUtc = launchDateUtc,
            SnapshotDateUtc = new DateTime(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc),
            ZeroSaleStreakDays = zeroSaleStreakDays,
            DeadObservedDays = deadObservedDays,
            DeadStockPacedTarget = deadStockPacedTarget,
            DeadStockPacedCaughtUpStreak = deadStockPacedCaughtUpStreak,
            Band = band ?? Band(),
            Options = options ?? new PricingEngineOptions(),
            VatRatePct = vatRatePct,
            IsNewProduct = isNewProduct,
            OldestUnitAgeDays = oldestUnitAgeDays,
            RoundingDisabledForSku = roundingDisabledForSku,
            FloorAndRoundingOnly = floorAndRoundingOnly,
            SlowLanesFrozen = slowLanesFrozen,
            ClearanceLockPrice = clearanceLockPrice,
            BelowFloorTunnelPrice = belowFloorTunnelPrice,
            DiscountTrimRanges = discountTrimRanges,
        };
    }
}
