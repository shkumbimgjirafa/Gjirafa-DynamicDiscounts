using Microsoft.EntityFrameworkCore;
using PricingTool.Core.Domain;

namespace PricingTool.Data.Services;

/// <summary>Loads admin-edited band rows and maps them to the Core domain's runtime band config.</summary>
public class BandConfigProvider
{
    private readonly PricingToolDbContext _db;

    public BandConfigProvider(PricingToolDbContext db) => _db = db;

    public async Task<IReadOnlyList<PriceBandConfig>> GetBandsAsync(int layerId, CancellationToken ct = default)
    {
        var bands = await _db.PriceBands
            .Where(b => b.LayerId == layerId)
            .Include(b => b.AlgorithmSettings)
            .OrderBy(b => b.SortOrder)
            .AsNoTracking()
            .ToListAsync(ct);

        return bands.Select(b => new PriceBandConfig
        {
            BandId = b.Id,
            Name = b.Name,
            MinPrice = b.MinPrice,
            MaxPrice = b.MaxPrice,
            MarginFloorPct = b.MarginFloorPct,
            TargetMarginPct = b.TargetMarginPct,
            DeadStockNoSaleWindowDays = b.DeadStockNoSaleWindowDays,
            DeadStockMinStockAgeDays = b.DeadStockMinStockAgeDays,
            DeadStockStartDiscountPct = b.DeadStockStartDiscountPct,
            DeadStockStepDiscountPct = b.DeadStockStepDiscountPct,
            DeadStockPeriodDays = b.DeadStockPeriodDays,
            DeadStockFloorCostPct = b.DeadStockFloorCostPct,
            DeadStockStage2AgeDays = b.DeadStockStage2AgeDays,
            DeadStockStage2FloorCostPct = b.DeadStockStage2FloorCostPct,
            DeadStockStage2StepDiscountPct = b.DeadStockStage2StepDiscountPct,
            DeadStockStage2PeriodDays = b.DeadStockStage2PeriodDays,
            DeadStockStage3AgeDays = b.DeadStockStage3AgeDays,
            DeadStockStage3FloorCostPct = b.DeadStockStage3FloorCostPct,
            DeadStockStage3StepDiscountPct = b.DeadStockStage3StepDiscountPct,
            DeadStockStage3PeriodDays = b.DeadStockStage3PeriodDays,
            DeadStockStage4AgeDays = b.DeadStockStage4AgeDays,
            DeadStockStage4FloorCostPct = b.DeadStockStage4FloorCostPct,
            DeadStockStage4StepDiscountPct = b.DeadStockStage4StepDiscountPct,
            DeadStockStage4PeriodDays = b.DeadStockStage4PeriodDays,
            Rounding = (RoundingConvention)b.RoundingConvention,
            RoundingEnabled = b.RoundingEnabled,
            Algorithms = b.AlgorithmSettings.ToDictionary(
                s => s.AlgorithmCode,
                s => new BandAlgorithmConfig(s.Enabled, s.Weight)),
        }).ToList();
    }

    /// <summary>Bands are keyed off PPTCV (cost), not the selling price.</summary>
    public static PriceBandConfig? FindBand(IReadOnlyList<PriceBandConfig> bands, decimal pptcv) =>
        bands.FirstOrDefault(b => b.Contains(pptcv));
}
