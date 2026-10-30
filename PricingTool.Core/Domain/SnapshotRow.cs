namespace PricingTool.Core.Domain;

/// <summary>
/// One row of the daily pricing dataset (output of usp_GetDailyPricingDataset),
/// shared by the real SQL reader and the demo generator.
/// </summary>
public class SnapshotRow
{
    public required string Sku { get; init; }
    /// <summary>The operational Product.Id behind this SKU. Null when unknown (older datasets).</summary>
    public int? ProductId { get; init; }
    /// <summary>Product.Name at pull time (display only). Null when unknown (older datasets).</summary>
    public string? ProductName { get; init; }

    /// <summary>The product's EAN/GTIN (Product.Gtin), trimmed, exactly as the catalog holds it — the
    /// identity of the PHYSICAL good, which several SKUs from different suppliers can share. Null when the
    /// product carries none (or on older datasets). Recorded for auditing and for the new-product re-list
    /// gate (a re-listed barcode is priced, not held as new). The raw catalog value is kept as-is — it also
    /// holds sentinels like "PARENT" and "&lt;NO-GTIN" that are not real barcodes.</summary>
    public string? Gtin { get; init; }
    /// <summary>Display-only shelf price (TierPrice.OldPrice).</summary>
    public decimal? OldPrice { get; init; }
    /// <summary>Pricing anchor = ProductPricing.FinalPrice (shelf fallback applied in SQL).</summary>
    public decimal? AnchorPrice { get; init; }
    /// <summary>True when FinalPrice was missing/zero and the anchor fell back to the shelf OldPrice.</summary>
    public bool AnchorIsFallback { get; init; }
    public decimal? CurrentPrice { get; init; }
    public decimal? CurrentDiscountPct { get; init; }

    /// <summary>Comma-separated ids of the active, customer-facing campaign discounts on this SKU at pull
    /// time — the same eligibility used to build <see cref="CurrentPrice"/> (DiscountApplyTypeId = 2, no
    /// coupon, currently live), best deal first (the first id is the one setting the current price). Null
    /// when the SKU has no such discount (or on older datasets that didn't capture it).</summary>
    public string? ActiveDiscountIds { get; init; }

    public decimal? Pptcv { get; init; }
    public decimal? GrossMargin { get; init; }
    /// <summary>Stock held in the warehouse local to this layer's store (was "KS" when single-layer).</summary>
    public int LocalWarehouseStock { get; init; }
    public int SupplierWarehouseStock { get; init; }

    /// <summary>The supplier (store) this SKU is sourced from: GjirafaTranslations.ZStores.Id via the
    /// 1:1 ProductStoreMapping on ProductCode. Null when the SKU has no (non-deleted) mapping.</summary>
    public int? SupplierId { get; init; }
    /// <summary>ZStores.Store — the supplier's display name. Null when unmapped or the store row is gone.</summary>
    public string? SupplierName { get; init; }

    /// <summary>The brand (manufacturer) this SKU belongs to: the primary Manufacturer.Id via
    /// Product_Manufacturer_Mapping (lowest DisplayOrder). Null when the product has no manufacturer.</summary>
    public int? BrandId { get; init; }
    /// <summary>Manufacturer.Name — the brand's display name. Null when the product has no manufacturer.</summary>
    public string? Brand { get; init; }
    /// <summary>True when the product is inside its platform MarkAsNew window — hold price (no discount).</summary>
    public bool IsNewProduct { get; init; }

    /// <summary>Age in days of the oldest unit currently on hand in this layer's warehouse (WMS check-in
    /// log). Null when the SKU has no check-in row. Gates dead-stock: a freshly-stocked pre-order with no
    /// 90d sales isn't "dead" yet.</summary>
    public int? OldestUnitAgeDays { get; init; }

    public int Qty7 { get; init; }
    public decimal Net7 { get; init; }
    public decimal? Disc7 { get; init; }
    public int Qty14 { get; init; }
    public decimal Net14 { get; init; }
    public decimal? Disc14 { get; init; }
    public int Qty30 { get; init; }
    public decimal Net30 { get; init; }
    public decimal? Disc30 { get; init; }
    public int Qty60 { get; init; }
    public decimal Net60 { get; init; }
    public decimal? Disc60 { get; init; }
    public int Qty90 { get; init; }
    public decimal Net90 { get; init; }
    public decimal? Disc90 { get; init; }

    /// <summary>Not present in the v1 dataset (open decision); nullable by design.</summary>
    public DateTime? LaunchDateUtc { get; init; }
}
