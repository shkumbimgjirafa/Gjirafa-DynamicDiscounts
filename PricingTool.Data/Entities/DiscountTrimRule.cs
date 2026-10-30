using PricingTool.Core.Domain;

namespace PricingTool.Data.Entities;

/// <summary>
/// One row of a layer's discount-optimization table (edited on the Price Bands tab): a displayed discount
/// off the shelf price in [<see cref="MinDiscountPct"/>, <see cref="MaxDiscountPct"/>) is trimmed by
/// <see cref="TrimPct"/> percent of the discount. Percents are whole numbers (18 = 18%). The table only
/// moves prices on runs for layers whose <see cref="Layer.DiscountOptimizationEnabled"/> is ticked.
/// </summary>
public class DiscountTrimRule
{
    public int Id { get; set; }
    public int LayerId { get; set; }

    /// <summary>Inclusive lower edge of the displayed discount, in percent.</summary>
    public decimal MinDiscountPct { get; set; }

    /// <summary>Exclusive upper edge, in percent; null = open-ended (the top "60%+" row).</summary>
    public decimal? MaxDiscountPct { get; set; }

    /// <summary>Share of the discount taken off, in percent (0 = leave this range alone).</summary>
    public decimal TrimPct { get; set; }

    public int SortOrder { get; set; }

    public DiscountTrimRange ToRange() => new(MinDiscountPct, MaxDiscountPct, TrimPct);
}
