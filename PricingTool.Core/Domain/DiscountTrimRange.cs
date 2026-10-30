namespace PricingTool.Core.Domain;

/// <summary>
/// One row of a layer's discount-optimization table: a SKU whose displayed discount off the shelf (Old)
/// price falls in [<see cref="MinDiscountPct"/>, <see cref="MaxDiscountPct"/>) has that discount trimmed
/// by <see cref="TrimPct"/> percent OF THE DISCOUNT — proportionally, not by points. A 25% discount under
/// an 18% trim shows 20.5% (25 × 0.82); a 65% discount under a 10% trim shows 58.5%. Percentages are
/// whole-number percents (18 = 18%). <see cref="MaxDiscountPct"/> null = open-ended (the "60%+" row).
/// </summary>
public sealed record DiscountTrimRange(decimal MinDiscountPct, decimal? MaxDiscountPct, decimal TrimPct)
{
    /// <summary>True when <paramref name="discountPct"/> sits in this row's half-open range.</summary>
    public bool Contains(decimal discountPct) =>
        discountPct >= MinDiscountPct && (MaxDiscountPct is not decimal max || discountPct < max);

    /// <summary>"20–30%" / "60%+", for display.</summary>
    public string Label => MaxDiscountPct is decimal max
        ? $"{MinDiscountPct:0.#}–{max:0.#}%"
        : $"{MinDiscountPct:0.#}%+";
}

/// <summary>
/// The discount-optimization arithmetic, shared by the pricing engine (which applies it to the price a
/// run lands on) and the Price Bands impact view (which applies it to today's prices) so the two can never
/// disagree about what a trim does. Pure: no bounds, no exemptions — the caller caps the result at its
/// no-raise ceiling and decides which SKUs are eligible.
/// </summary>
public static class DiscountOptimizer
{
    /// <summary>
    /// The ranges a new layer starts with — the inputs worked out in the Sept 2026 discount analysis:
    /// discounts under 20% are left alone, deeper ones are trimmed by a share that falls as the discount
    /// grows (18% of a 20–30% discount down to 10% of a 60%+ one), so the price moves roughly +6% at the
    /// shallow end and +19% at the deep end.
    /// </summary>
    public static readonly IReadOnlyList<DiscountTrimRange> DefaultRanges = new[]
    {
        new DiscountTrimRange(0m, 20m, 0m),
        new DiscountTrimRange(20m, 30m, 18m),
        new DiscountTrimRange(30m, 40m, 16m),
        new DiscountTrimRange(40m, 50m, 14m),
        new DiscountTrimRange(50m, 60m, 12m),
        new DiscountTrimRange(60m, null, 10m),
    };

    /// <summary>The discount <paramref name="price"/> shows off <paramref name="oldPrice"/>, in percent;
    /// null when there is no shelf reference or the price isn't below it.</summary>
    public static decimal? DisplayedDiscountPct(decimal oldPrice, decimal price) =>
        oldPrice > 0m && price > 0m && price < oldPrice ? (oldPrice - price) / oldPrice * 100m : null;

    /// <summary>The first range containing <paramref name="discountPct"/>, or null.</summary>
    public static DiscountTrimRange? FindRange(IReadOnlyList<DiscountTrimRange>? ranges, decimal discountPct)
    {
        if (ranges is null) return null;
        foreach (var range in ranges)
            if (range.Contains(discountPct)) return range;
        return null;
    }

    /// <summary>
    /// The price at which <paramref name="price"/>'s discount is trimmed by its range's share:
    /// <c>Old × (1 − d × (1 − trim))</c>. Null when the price isn't discounted, no range covers it, the
    /// range's trim is zero, or the result wouldn't raise the price. UNBOUNDED — callers cap it.
    /// </summary>
    public static decimal? TrimmedPrice(decimal oldPrice, decimal price, IReadOnlyList<DiscountTrimRange>? ranges)
    {
        if (DisplayedDiscountPct(oldPrice, price) is not decimal discount) return null;
        if (FindRange(ranges, discount) is not { TrimPct: > 0m } range) return null;
        var trimmedDiscount = discount * (1m - Math.Min(range.TrimPct, 100m) / 100m);
        var target = oldPrice * (1m - trimmedDiscount / 100m);
        return target > price ? target : null;
    }
}
