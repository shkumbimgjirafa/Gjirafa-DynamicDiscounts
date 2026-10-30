namespace PricingTool.Core.Domain;

/// <summary>One listing of a SKU in the product map: its product id and the layer's decision category.</summary>
public sealed record SkuListing(int ProductId, string Category);

/// <summary>A per-category override of one band's floor and/or target. Null keeps the band's own value.</summary>
public sealed record CategoryRule(int BandId, string Category, decimal? MarginFloorPct, decimal? TargetMarginPct);

/// <summary>
/// Per-category floors and targets for one run (hackathon build, plan section 5 "Per-category floors"). A SKU whose
/// band × category has a rule prices against a copy of its band carrying the rule's floor and target
/// (<see cref="PriceBandConfig.WithMargins"/>), so the floor clamp and everything else that reads the band see the
/// same values; every other SKU prices against its band as before. A rule with its own target also makes its
/// band × category a group of its own in the margin-target pass (<see cref="Targets"/>).
///
/// The category is the layer's decision category (GjirafaMall main group, Gjirafa50 L1 category), found by SKU in
/// the product map: the SKU's own listing (same product id) first, else its lowest product id, the same choice as
/// <c>hk.ProductForProposal</c>.
/// </summary>
public sealed class CategoryBands
{
    public static CategoryBands None { get; } = new(Array.Empty<PriceBandConfig>(), Array.Empty<CategoryRule>(),
        new Dictionary<string, IReadOnlyList<SkuListing>>());

    private readonly IReadOnlyDictionary<string, IReadOnlyList<SkuListing>> _listings;
    private readonly Dictionary<(int BandId, string Category), PriceBandConfig> _bands = new();
    private readonly Dictionary<(int BandId, string Category), decimal> _targets = new();

    /// <param name="bands">The layer's bands for this run.</param>
    /// <param name="rules">The layer's rules; a rule for a band not in <paramref name="bands"/> is ignored.</param>
    /// <param name="listings">SKU → its listings, at least for every SKU that has a listing in a rule's category.</param>
    public CategoryBands(IReadOnlyList<PriceBandConfig> bands, IReadOnlyList<CategoryRule> rules,
        IReadOnlyDictionary<string, IReadOnlyList<SkuListing>> listings)
    {
        _listings = listings;
        var byId = bands.ToDictionary(b => b.BandId);
        foreach (var r in rules)
        {
            if (!byId.TryGetValue(r.BandId, out var band) || r.MarginFloorPct is null && r.TargetMarginPct is null) continue;
            _bands[(r.BandId, r.Category)] = band.WithMargins(r.MarginFloorPct ?? band.MarginFloorPct, r.TargetMarginPct ?? band.TargetMarginPct);
            if (r.TargetMarginPct is { } t) _targets[(r.BandId, r.Category)] = t;
        }
    }

    /// <summary>The rules in force this run (band × category).</summary>
    public IReadOnlyDictionary<(int BandId, string Category), PriceBandConfig> Rules => _bands;

    /// <summary>Band × category groups with their own target, measured apart from the rest of their band.</summary>
    public IReadOnlyDictionary<(int BandId, string Category), decimal> Targets => _targets;

    /// <summary>The SKU's category as far as the rules are concerned (null when it has no listing in a rule's category).</summary>
    public string? CategoryOf(string sku, int? productId)
    {
        if (_bands.Count == 0 || !_listings.TryGetValue(sku, out var listings) || listings.Count == 0) return null;
        var own = productId is { } id ? listings.FirstOrDefault(l => l.ProductId == id) : null;
        return (own ?? listings.MinBy(l => l.ProductId))!.Category;
    }

    /// <summary>The band config the SKU prices against: its band × category's copy when there is a rule, else the band.</summary>
    public PriceBandConfig Resolve(string sku, int? productId, PriceBandConfig band) =>
        CategoryOf(sku, productId) is { } category && _bands.TryGetValue((band.BandId, category), out var ruled) ? ruled : band;

    /// <summary>The SKU's category when its band × category has its own target (its margin-target group), else null.</summary>
    public string? TargetGroupOf(string sku, int? productId, int bandId) =>
        _targets.Count > 0 && CategoryOf(sku, productId) is { } category && _targets.ContainsKey((bandId, category)) ? category : null;
}
