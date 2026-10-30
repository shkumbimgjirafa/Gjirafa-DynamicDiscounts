namespace PricingTool.Core.Domain;

/// <summary>
/// The per-layer parameters that scope the source dataset pull. Carries everything the source
/// query needs to fetch one layer's catalog: which operational database to read, the store/country
/// filter ids, the local-warehouse split id, and the per-brand query toggles.
/// </summary>
public record LayerSourceContext
{
    /// <summary>NOP operational database name substituted into the query (GjirafaMall / GjirafaEcommerce).</summary>
    public required string OperationalDatabase { get; init; }

    /// <summary>Store id used to filter orders, tier prices and discounts.</summary>
    public required int StoreId { get; init; }

    /// <summary>
    /// The OTHER platform of the same country, which shares this country's physical warehouse. Its
    /// order lines are folded into the sales windows (matched by SKU, since ProductIds differ across
    /// catalogs) so a SKU selling on either brand is never treated as dead on the other. Null / -1 when
    /// the country has no sibling layer — then the sibling branch of the sales query matches nothing.
    /// (Cross-platform only applies in InlineQuery mode; the stored proc can't switch databases.)
    /// </summary>
    public string? SiblingOperationalDatabase { get; init; }

    /// <summary>Store id of the sibling platform's orders; -1 when there is no sibling layer.</summary>
    public int SiblingStoreId { get; init; } = -1;

    /// <summary>CountryId used to filter product pricing in GjirafaTranslations.</summary>
    public required int TranslationCountryId { get; init; }

    /// <summary>Warehouse store id for the IsLocalToStoreIds local-stock split.</summary>
    public required int WarehouseStoreId { get; init; }

    /// <summary>WMS warehouse id (KS=1, AL=5, MK=6) scoping the per-SKU oldest-on-hand-unit age lookup.</summary>
    public required int WmsWarehouseId { get; init; }

    /// <summary>True = restrict to the GjirafaMall vendor set; false = all vendors (Gjirafa50).</summary>
    public bool FilterVendors { get; init; } = true;

    /// <summary>True = exclude products not published in this store (UnpublishedStoreids NOT LIKE '%StoreId%').</summary>
    public bool ExcludeUnpublished { get; init; } = true;
}
