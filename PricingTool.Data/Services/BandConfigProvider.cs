using Microsoft.EntityFrameworkCore;
using PricingTool.Core.Domain;
using PricingTool.Data.Entities;

namespace PricingTool.Data.Services;

/// <summary>Loads admin-edited band rows and maps them to the Core domain's runtime band config.</summary>
public class BandConfigProvider
{
    private readonly PricingToolDbContext _db;
    private readonly ICategoryMapReader? _categoryMap;

    public BandConfigProvider(PricingToolDbContext db, ICategoryMapReader? categoryMap = null)
    {
        _db = db;
        _categoryMap = categoryMap;
    }

    /// <summary>
    /// The layer's per-category floors and targets for this run (<see cref="BandCategoryRule"/>, written by the pricing
    /// agent within its safety limits), with each affected SKU's category from the product map. No rules, or no map
    /// to find the categories in, gives <see cref="CategoryBands.None"/>: every SKU prices against its band.
    /// </summary>
    public async Task<CategoryBands> GetCategoryBandsAsync(Layer layer, IReadOnlyList<PriceBandConfig> bands, CancellationToken ct = default)
    {
        var rules = await _db.BandCategoryRules.AsNoTracking().Where(r => r.LayerId == layer.Id)
            .Select(r => new CategoryRule(r.BandId, r.Category, r.MarginFloorPct, r.TargetMarginPct)).ToListAsync(ct);
        if (rules.Count == 0 || _categoryMap is null) return CategoryBands.None;
        var listings = await _categoryMap.ReadAsync(layer.Id, layer.Brand == "GjirafaMall", rules.Select(r => r.Category).Distinct().ToList(), ct);
        return listings is null ? CategoryBands.None : new CategoryBands(bands, rules, listings);
    }

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
