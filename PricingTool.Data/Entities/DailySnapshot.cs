namespace PricingTool.Data.Entities;

/// <summary>
/// One SKU row of one daily dataset pull. Every pull is snapshotted here (architecture rule 3)
/// so the dashboard has history and runs are reproducible.
/// </summary>
public class DailySnapshot
{
    public long Id { get; set; }

    /// <summary>The layer (Brand + Country) this snapshot belongs to.</summary>
    public int LayerId { get; set; }

    /// <summary>The UTC date this snapshot belongs to (one snapshot set per layer per day; same-day re-pulls replace it).</summary>
    public DateTime SnapshotDate { get; set; }

    public string Sku { get; set; } = "";
    /// <summary>Product.Name at pull time (display only). Null on legacy rows and when unknown.</summary>
    public string? ProductName { get; set; }
    public decimal? OldPrice { get; set; }
    /// <summary>Pricing anchor = ProductPricing.FinalPrice (shelf fallback applied upstream). Null on legacy rows.</summary>
    public decimal? AnchorPrice { get; set; }
    public decimal? CurrentPrice { get; set; }
    public decimal? CurrentDiscountPct { get; set; }

    /// <summary>Comma-separated ids of the active customer-facing campaign discounts on this SKU at pull
    /// time (DiscountApplyTypeId = 2, no coupon, currently live), best deal first. Null on legacy rows and
    /// when the SKU has no such discount.</summary>
    public string? ActiveDiscountIds { get; set; }

    public decimal? Pptcv { get; set; }
    public decimal? GrossMargin { get; set; }
    /// <summary>Stock in the warehouse local to this layer's store (was "KS" when single-layer).</summary>
    public int LocalWarehouseStock { get; set; }
    public int SupplierWarehouseStock { get; set; }

    /// <summary>Supplier (store) at pull time: GjirafaTranslations.ZStores.Id via ProductStoreMapping.
    /// Null on legacy rows and on SKUs with no (non-deleted) mapping.</summary>
    public int? SupplierId { get; set; }
    /// <summary>ZStores.Store — the supplier's display name at pull time. Null when unmapped.</summary>
    public string? SupplierName { get; set; }

    /// <summary>Brand (manufacturer) at pull time: the primary Manufacturer.Id via
    /// Product_Manufacturer_Mapping. Null on legacy rows and on products with no manufacturer.</summary>
    public int? BrandId { get; set; }
    /// <summary>Manufacturer.Name — the brand's display name at pull time. Null when unmapped.</summary>
    public string? Brand { get; set; }

    // Trailing sales, 7/30/90 days: what the pages and the dead-stock history read back. The pull's other
    // windows (14/60 days), its sold-at discounts and launch date feed the run from memory and are not stored.
    public int Qty7 { get; set; }
    public decimal Net7 { get; set; }
    public int Qty30 { get; set; }
    public decimal Net30 { get; set; }
    public int Qty90 { get; set; }
    public decimal Net90 { get; set; }
}
