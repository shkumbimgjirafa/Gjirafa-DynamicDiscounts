using System.Globalization;

namespace PricingTool.NopIntegration;

/// <summary>
/// <c>Product.HasDiscountsAppliedStoreIds</c>: the stores where nopCommerce looks at a product's discounts, a
/// colon-separated, numerically sorted list in an nvarchar(40) (live values look like "1:2:3", "2", "2:3").
/// The publisher only ever adds its own store; another store's id is never dropped.
/// </summary>
public static class StoreIdList
{
    public const int MaxLength = 40;

    public static string Merge(string? existing, int storeId)
    {
        var ids = Parse(existing);
        ids.Add(storeId);
        var merged = string.Join(':', ids.Order().Select(i => i.ToString(CultureInfo.InvariantCulture)));
        if (merged.Length > MaxLength)
            throw new InvalidOperationException($"Store id list '{merged}' would not fit nvarchar({MaxLength}).");
        return merged;
    }

    public static bool Contains(string? existing, int storeId) => Parse(existing).Contains(storeId);

    /// <summary>Ignores empty parts and anything that isn't a number (it is kept out of a merge, never invented).</summary>
    private static SortedSet<int> Parse(string? value) =>
        new((value ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? (int?)i : null)
            .Where(i => i is not null).Select(i => i!.Value));
}
