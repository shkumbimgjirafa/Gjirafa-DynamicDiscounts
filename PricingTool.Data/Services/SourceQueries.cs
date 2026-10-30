namespace PricingTool.Data.Services;

/// <summary>
/// The daily dataset query, runnable verbatim when the stored procedure cannot be deployed.
/// @now is captured once and used consistently everywhere, including the discount filter.
///
/// MULTI-LAYER: the operational database is a {opDb} token (GjirafaMall / GjirafaEcommerce) that
/// <see cref="SqlSourceDataReader"/> substitutes per layer; the cross-database GjirafaTranslations
/// and WarehouseManagmentSystem references are shared and stay fixed. The store/country filters, the
/// WMS warehouse id and the vendor / unpublished toggles are bound as command parameters (@StoreId,
/// @TranslationCountryId, @WarehouseStoreId, @WmsWarehouseId, @FilterVendors, @ExcludeUnpublished).
///
/// CROSS-PLATFORM SALES: the sales windows sum orders from BOTH platforms of the country ({opDb} and
/// the sibling {opDb2}, filtered to @StoreId / @SiblingStoreId), matched by SKU — the two brands share
/// one physical warehouse so a SKU selling on either must not read as dead on the other. This is
/// INLINE-ONLY: the stored proc cannot switch operational databases, so its #sales stays single-platform.
///
/// SET-BASED REWRITE: the latest tier price and the best active discount are now resolved in
/// bulk via ROW_NUMBER() into #tier / #disc, instead of per-product correlated OUTER APPLYs.
/// The correlated version did not complete on the full catalog (~680k SKUs); this version
/// returns the whole set in ~20s. Schema: adds AnchorPrice (= ProductPricing.FinalPrice, with a
/// shelf fallback) + AnchorFromShelf; drops the now-unused per-window avg-discount-% and the
/// Net14/60 windows (Net7/30/90 are kept for the Movers average-selling-price columns); adds
/// SupplierId/SupplierName (ZStores via ProductStoreMapping on ProductCode = SKU), ProductName
/// (Product.Name), BrandId/Brand (the primary Manufacturer via Product_Manufacturer_Mapping) and
/// ActiveDiscountIds (#discids: all live customer-facing campaign discount ids per product, best deal
/// first). Keep in sync with scripts/usp_GetDailyPricingDataset.sql EXCEPT the cross-platform #sales
/// block, which is inline-only (the proc can't switch DBs) — the proc's #sales stays single-platform by design.
/// </summary>
public static class SourceQueries
{
    public const string StoredProcedureName = "dbo.usp_GetDailyPricingDataset";

    /// <summary>The token in <see cref="DailyDatasetInline"/> replaced with the layer's operational DB name.</summary>
    public const string OperationalDbToken = "{opDb}";

    /// <summary>
    /// Guards a database name before it is interpolated into SQL (it cannot be parameterised). Letters,
    /// digits and underscore only — anything else is a configuration error, not something to escape.
    /// </summary>
    public static string SafeDbName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            !name.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            throw new InvalidOperationException($"Invalid operational database name '{name}'.");
        }
        return name;
    }

    /// <summary>
    /// The token replaced with the SIBLING platform's operational DB (the other brand in the same
    /// country, which shares the physical warehouse). Falls back to the layer's own DB when there is
    /// no sibling; @SiblingStoreId = -1 then makes the sibling sales branch match nothing. Must be
    /// substituted before <see cref="OperationalDbToken"/> is not required (the two tokens are disjoint).
    /// </summary>
    public const string SiblingOperationalDbToken = "{opDb2}";

    /// <summary>
    /// Approved-batch clearance refresh, scoped to a set of SKUs with their pushed prices (passed in @skus
    /// as pipe-joined <c>sku=price</c> pairs, split with STRING_SPLIT — SKUs are alphanumeric so '=' and '|'
    /// never collide, and the price is invariant-formatted with a '.' decimal). For each requested SKU it
    /// returns: units + net (excl. VAT) sold since @pushed across BOTH platforms of the country (this layer's
    /// {opDb} @ @StoreId plus the sibling {opDb2} @ @SiblingStoreId, matched by SKU — the two brands share one
    /// warehouse), and the SKU's CURRENT local-warehouse stock (0 when it has none left / is delisted), and
    /// the SKU's LIVE customer-facing discount id(s) on this platform WHOSE PRICE EQUALS THE PUSHED PRICE (the
    /// promo(s) we created to carry the clearance price — the ones to delete once the SKU has cleared). A live
    /// deal at any other price, or none at all, yields NULL discount ids so the sold-out export can drop the
    /// row (nothing of ours left to delete). Sales use the same NOP Order tables and OrderStatusId IN (20,30)
    /// as the dataset query's #sales block (CompletedSoldUnits is the status-30-only subset). Every requested
    /// SKU comes back (LEFT JOINs from the split set), so a SKU with no sales / no stock returns zeros (and
    /// NULL discount ids). {opDb}/{opDb2} are substituted by <see cref="SqlSourceDataReader"/>; the rest are
    /// command parameters.
    /// </summary>
    public const string ApprovedBatchSalesInline = @"
-- Materialize the SKU set (with each SKU's pushed clearance price) into an indexed temp table ONCE
-- (STRING_SPLIT is otherwise re-evaluated per reference and the SKU joins go unindexed — a full-batch pull
-- then takes minutes). Mirrors the dataset query's #stock-with-clustered-index pattern. @skus is pipe-joined
-- 'sku=price' pairs; TRY_CAST leaves the price NULL if a pair is malformed (that SKU then matches no discount).
DROP TABLE IF EXISTS #batch;
CREATE TABLE #batch (Sku nvarchar(400) NOT NULL PRIMARY KEY, PushedPrice decimal(18,2) NULL);
INSERT INTO #batch (Sku, PushedPrice)
SELECT s.Sku, MAX(s.PushedPrice)
FROM (
    SELECT LTRIM(RTRIM(LEFT(value, CHARINDEX('=', value) - 1)))               AS Sku,
           TRY_CAST(LTRIM(RTRIM(SUBSTRING(value, CHARINDEX('=', value) + 1, 64))) AS decimal(18,2)) AS PushedPrice
    FROM STRING_SPLIT(@skus, N'|')
    WHERE CHARINDEX('=', value) > 1
) s
WHERE s.Sku <> ''
GROUP BY s.Sku;

;WITH stk AS (
    SELECT p.Sku,
           SUM(CASE WHEN w.IsLocalToStoreIds = @WarehouseStoreId THEN pwi.StockQuantity ELSE 0 END) AS LocalStock
    FROM {opDb}.dbo.Product p
    INNER JOIN #batch b ON b.Sku = p.Sku
    INNER JOIN {opDb}.dbo.ProductWarehouseInventory pwi ON pwi.ProductId = p.Id
    INNER JOIN {opDb}.dbo.Warehouse w ON w.Id = pwi.WarehouseId
    GROUP BY p.Sku
),
sales AS (
    -- SoldUnits/SoldNet span both live statuses (20 processing + 30 complete); CompletedSoldUnits is the
    -- status-30-only subset: the units in fully COMPLETED orders (the sold-out export's Completed sold).
    SELECT x.Sku,
           SUM(x.Quantity)     AS SoldUnits,
           SUM(x.PriceExclTax) AS SoldNet,
           SUM(CASE WHEN x.OrderStatusId = 30 THEN x.Quantity ELSE 0 END) AS CompletedSoldUnits
    FROM (
        SELECT p.Sku, oi.Quantity, oi.PriceExclTax, o.OrderStatusId
        FROM {opDb}.dbo.OrderItem oi
        INNER JOIN {opDb}.dbo.[Order] o ON o.Id = oi.OrderId
        INNER JOIN {opDb}.dbo.Product p ON p.Id = oi.ProductId
        INNER JOIN #batch b ON b.Sku = p.Sku
        WHERE o.OrderStatusId IN (20, 30) AND o.StoreId = @StoreId AND o.CreatedOnUtc >= @pushed
        UNION ALL
        SELECT p2.Sku, oi.Quantity, oi.PriceExclTax, o.OrderStatusId
        FROM {opDb2}.dbo.OrderItem oi
        INNER JOIN {opDb2}.dbo.[Order] o ON o.Id = oi.OrderId
        INNER JOIN {opDb2}.dbo.Product p2 ON p2.Id = oi.ProductId
        INNER JOIN #batch b ON b.Sku = p2.Sku
        WHERE o.OrderStatusId IN (20, 30) AND o.StoreId = @SiblingStoreId AND o.CreatedOnUtc >= @pushed
    ) x
    GROUP BY x.Sku
),
disc AS (
    -- Live customer-facing campaign discount id(s) currently on each batch SKU, on THIS platform only
    -- ({opDb} @ @StoreId) — same eligibility as the dataset query's #discids (ApplyType=2 public deal, no
    -- coupon, inside its date window) AND priced EXACTLY at the SKU's pushed clearance price. For an
    -- ApplyType=2 amount deal, DiscountAmount IS the customer-facing final price, so we match it (rounded to
    -- the cent) against #batch.PushedPrice: this keeps only the promo WE created to carry the clearance
    -- price — the one to delete once the SKU has cleared. A deal at any other price (a re-priced promo, an
    -- unrelated campaign, or a percentage deal whose DiscountAmount is 0) never matches, so its SKU gets NULL
    -- here and drops out of the sold-out export (nothing of ours left to delete). Comma-joined, best-deal-first
    -- (lowest amount first). STRING_AGG needs SQL Server 2017+; an int id list is far under its 8000-byte cap.
    -- Scoped to this platform (not the sibling) because the discount to delete lives on the platform whose
    -- batch this is.
    SELECT p.Sku,
           STRING_AGG(CAST(d.Id AS varchar(10)), ',')
               WITHIN GROUP (ORDER BY d.DiscountAmount, d.Id) AS ActiveDiscountIds
    FROM {opDb}.dbo.Product p
    INNER JOIN #batch b ON b.Sku = p.Sku
    INNER JOIN {opDb}.dbo.Discount_AppliedToProducts dap ON dap.Product_Id = p.Id
    INNER JOIN {opDb}.dbo.Discount d ON d.Id = dap.Discount_Id
    WHERE d.StoreId = @StoreId
      AND d.RequiresCouponCode = 0
      AND d.DiscountApplyTypeId = 2
      AND GETUTCDATE() BETWEEN ISNULL(d.StartDateUtc, '1900') AND ISNULL(d.EndDateUtc, '2999')
      AND b.PushedPrice IS NOT NULL
      AND ROUND(d.DiscountAmount, 2) = ROUND(b.PushedPrice, 2)
    GROUP BY p.Sku
)
SELECT b.Sku,
       ISNULL(sa.SoldUnits, 0)          AS SoldUnits,
       ISNULL(sa.SoldNet, 0)            AS SoldNet,
       ISNULL(sa.CompletedSoldUnits, 0) AS CompletedSoldUnits,
       ISNULL(stk.LocalStock, 0)        AS CurrentStock,
       dc.ActiveDiscountIds             AS ActiveDiscountIds
FROM #batch b
LEFT JOIN sales sa ON sa.Sku = b.Sku
LEFT JOIN stk     ON stk.Sku = b.Sku
LEFT JOIN disc dc ON dc.Sku = b.Sku;";

    public const string DailyDatasetInline = @"
DROP TABLE IF EXISTS #vendors, #exclbrand, #gtinfirst, #gtinreused, #stock, #sales, #pricing, #tier, #disc, #discids, #age, #supplier, #brand;
DECLARE @now  datetime = GETUTCDATE();
DECLARE @d7   datetime = DATEADD(DAY, -7,  @now);
DECLARE @d14  datetime = DATEADD(DAY, -14, @now);
DECLARE @d30  datetime = DATEADD(DAY, -30, @now);
DECLARE @d60  datetime = DATEADD(DAY, -60, @now);
DECLARE @d90  datetime = DATEADD(DAY, -90, @now);
DECLARE @storeIdStr varchar(16) = CAST(@StoreId AS varchar(16));

-- Target vendors (GjirafaMall vendor set when @FilterVendors = 1; all vendors otherwise).
-- 'YZATT' is the local-supplier vendor behind the 'yz'-suffixed SKUs -- its whole catalogue is
-- 'yz', and those used to be priced by hand, which is why the allowlist left it out.
SELECT v.Id INTO #vendors
FROM {opDb}.dbo.Vendor v
WHERE v.Deleted = 0
  AND v.Active = 1
  AND (@FilterVendors = 0 OR (v.Name LIKE '%Gjiraf%' OR v.Name IN ('Dino Toys', 'Mysu', 'Apple', 'YZATT')));
CREATE CLUSTERED INDEX cx ON #vendors (Id);

-- Apple and Starlink are excluded at the BRAND (manufacturer) level, unconditionally for every layer:
-- any product mapped to a manufacturer whose name starts with 'Apple' ('Apple', 'APPLE', 'Apple
-- Authorized Reseller', 'Apple Refurbished') or 'Starlink' never enters the dataset. The vendor filter
-- above cannot do this — both brands also sell under Gjirafa vendors (e.g. vendor 'Gjirafa EU'), and
-- Gjirafa50 layers don't filter vendors at all.
SELECT DISTINCT pmm.ProductId INTO #exclbrand
FROM {opDb}.dbo.Product_Manufacturer_Mapping pmm
INNER JOIN {opDb}.dbo.Manufacturer m ON m.Id = pmm.ManufacturerId
WHERE m.Name LIKE 'Apple%' OR m.Name LIKE 'Starlink%';
CREATE CLUSTERED INDEX cx ON #exclbrand (ProductId);

-- RE-LISTED products (used to strip false 'new-product' protection below). MarkAsNew is set on nearly
-- every product in this catalog, so the MarkAsNew date window is what actually flags something as new —
-- and that window fires on RE-LISTS too: a fresh Product row created for an item we have already sold
-- for months (a new supplier batch, a re-catalogued SKU) is 'new' by the flag but not a genuine launch.
-- The EAN/GTIN identifies the physical good, so a product whose GTIN already appeared on an EARLIER-created
-- Product row is such a re-list. #gtinfirst is the earliest CreatedOnUtc per non-empty GTIN across the
-- WHOLE catalog (soft-deleted rows included — 'existed before' still counts when the old listing is now
-- removed); #gtinreused is every currently-in-window MarkAsNew product created strictly after its GTIN's
-- first appearance. Empty / whitespace GTINs are not an identity and are skipped. Built before #stock and
-- LEFT-JOINed into its IsNewProduct test. NOTE: this is the platform MarkAsNew hold only; genuinely
-- just-arrived stock is still protected from dead-stock markdown by the WMS freshness gate (#age), which
-- is independent of this flag.
SELECT p.Gtin, MIN(p.CreatedOnUtc) AS FirstCreatedUtc
INTO #gtinfirst
FROM {opDb}.dbo.Product p
WHERE NULLIF(LTRIM(RTRIM(p.Gtin)), '') IS NOT NULL
GROUP BY p.Gtin;

SELECT p.Id AS ProductId
INTO #gtinreused
FROM {opDb}.dbo.Product p
INNER JOIN #gtinfirst gf ON gf.Gtin = p.Gtin
WHERE p.Deleted = 0
  AND p.MarkAsNew = 1
  AND (p.MarkAsNewStartDateTimeUtc IS NULL OR @now >= p.MarkAsNewStartDateTimeUtc)
  AND (p.MarkAsNewEndDateTimeUtc   IS NULL OR @now <= p.MarkAsNewEndDateTimeUtc)
  AND p.CreatedOnUtc > gf.FirstCreatedUtc;
CREATE CLUSTERED INDEX cx ON #gtinreused (ProductId);

SELECT
    p.Id  AS ProductId,
    p.Sku,
    MAX(CAST(p.Name AS nvarchar(400))) AS ProductName,
    -- The EAN/GTIN, raw and trimmed. Carried through to the final SELECT for auditing and the new-product
    -- re-list gate (#gtinreused below), which prices a barcode first seen on an EARLIER product rather than
    -- holding it as new. NOT filtered here: this column also holds sentinels ('PARENT', '<NO-GTIN') that are
    -- no barcode at all; the re-list join handles those via its empty/whitespace guard.
    MAX(CAST(NULLIF(LTRIM(RTRIM(p.Gtin)), '') AS nvarchar(64))) AS Gtin,
    SUM(CASE WHEN w.IsLocalToStoreIds = @WarehouseStoreId THEN pwi.StockQuantity ELSE 0 END) AS LocalWarehouseStock,
    SUM(CASE WHEN w.IsLocalToStores  = 0 THEN pwi.StockQuantity ELSE 0 END) AS Supplier_WarehouseStock,
    -- New-product protection fires only for a GENUINE launch: inside the MarkAsNew window AND the GTIN is
    -- first seen here (gr.ProductId IS NULL). A re-list (GTIN already on an earlier SKU) is priced normally.
    MAX(CASE WHEN p.MarkAsNew = 1
             AND (p.MarkAsNewStartDateTimeUtc IS NULL OR @now >= p.MarkAsNewStartDateTimeUtc)
             AND (p.MarkAsNewEndDateTimeUtc   IS NULL OR @now <= p.MarkAsNewEndDateTimeUtc)
             AND gr.ProductId IS NULL
        THEN 1 ELSE 0 END) AS IsNewProduct
INTO #stock
FROM {opDb}.dbo.Product p
INNER JOIN #vendors v ON v.Id = p.VendorId
INNER JOIN {opDb}.dbo.ProductWarehouseInventory pwi ON pwi.ProductId = p.Id
INNER JOIN {opDb}.dbo.Warehouse w ON w.Id = pwi.WarehouseId
LEFT JOIN #gtinreused gr ON gr.ProductId = p.Id
WHERE p.Deleted = 0               -- soft-deleted products are removed from the storefront; never price them
  AND ISNULL(p.IsOutlet, 0) = 0   -- outlets are priced by a separate system; never include them
  -- Not every outlet gets the IsOutlet flag set, but the storefront always tags them in the NAME:
  -- '[OUTLET] Laptop Lenovo ...'. Match the OPENING tag only, never a bare 'outlet': that word occurs
  -- legitimately in real product names -- power strips, PDUs, drain hoses, chimney pipes ('Prize shtese
  -- Equip 3 Outlet French PDU', 'Zorre shkarkimi Nedis Outlet') -- and excluding those would drop
  -- sellable stock from pricing. Matching '[OUTLET' rather than the full '[OUTLET]' also catches the
  -- mis-typed tags seen live ('[OUTLET Kufje ...', '[OUTLET[ Drita ...').
  -- CHARINDEX, not LIKE: '[' is a LIKE wildcard, so LIKE '%[OUTLET]%' would match almost every name.
  AND CHARINDEX('[OUTLET', p.Name) = 0
  AND NOT EXISTS (SELECT 1 FROM #exclbrand eb WHERE eb.ProductId = p.Id)  -- excluded brands (Apple, Starlink): never priced
  AND (@ExcludeUnpublished = 0
       OR p.UnpublishedStoreids IS NULL
       OR p.UnpublishedStoreids NOT LIKE '%' + @storeIdStr + '%')
GROUP BY p.Id, p.Sku
HAVING SUM(CASE WHEN w.IsLocalToStoreIds = @WarehouseStoreId THEN pwi.StockQuantity ELSE 0 END)
     + SUM(CASE WHEN w.IsLocalToStores  = 0 THEN pwi.StockQuantity ELSE 0 END) <> 0;
CREATE CLUSTERED INDEX cx ON #stock (ProductId);

-- Sales windows, aggregated by SKU across BOTH platforms of this country. GjirafaMall and Gjirafa50
-- share one physical warehouse, so a SKU selling on EITHER brand is moving the shared stock and must
-- not read as dead on the other. Matched by SKU (not ProductId — the two operational catalogs assign
-- independent ProductIds). The sibling branch reads {opDb2} filtered to @SiblingStoreId; when the
-- country has no sibling layer, {opDb2} is this same DB and @SiblingStoreId = -1, so it contributes
-- nothing and the result is single-platform. (Cross-platform only runs in InlineQuery mode — the
-- stored proc mirror stays single-platform because it can't switch operational databases.)
SELECT
    x.Sku,
    SUM(CASE WHEN x.CreatedOnUtc >= @d7  THEN x.Quantity     ELSE 0 END) AS [7d_qty],
    SUM(CASE WHEN x.CreatedOnUtc >= @d7  THEN x.PriceExclTax ELSE 0 END) AS [7d_net],
    SUM(CASE WHEN x.CreatedOnUtc >= @d14 THEN x.Quantity     ELSE 0 END) AS [14d_qty],
    SUM(CASE WHEN x.CreatedOnUtc >= @d14 THEN x.PriceExclTax ELSE 0 END) AS [14d_net],
    SUM(CASE WHEN x.CreatedOnUtc >= @d30 THEN x.Quantity     ELSE 0 END) AS [30d_qty],
    SUM(CASE WHEN x.CreatedOnUtc >= @d30 THEN x.PriceExclTax ELSE 0 END) AS [30d_net],
    SUM(CASE WHEN x.CreatedOnUtc >= @d60 THEN x.Quantity     ELSE 0 END) AS [60d_qty],
    SUM(x.Quantity)     AS [90d_qty],
    SUM(x.PriceExclTax) AS [90d_net]
INTO #sales
FROM (
    -- This layer's own platform (already scoped to #stock by ProductId within its catalog).
    SELECT st.Sku AS Sku, o.CreatedOnUtc, oi.Quantity, oi.PriceExclTax
    FROM {opDb}.dbo.OrderItem oi
    INNER JOIN {opDb}.dbo.[Order] o ON o.Id = oi.OrderId
    INNER JOIN #stock st ON st.ProductId = oi.ProductId
    WHERE o.OrderStatusId IN (20, 30)
      AND o.StoreId = @StoreId
      AND o.CreatedOnUtc >= @d90
    UNION ALL
    -- The sibling platform sharing this country's warehouse, matched to our catalog by SKU.
    SELECT st.Sku AS Sku, o.CreatedOnUtc, oi.Quantity, oi.PriceExclTax
    FROM {opDb2}.dbo.OrderItem oi
    INNER JOIN {opDb2}.dbo.[Order] o ON o.Id = oi.OrderId
    INNER JOIN {opDb2}.dbo.Product p2 ON p2.Id = oi.ProductId
    INNER JOIN #stock st ON st.Sku = p2.Sku
    WHERE o.OrderStatusId IN (20, 30)
      AND o.StoreId = @SiblingStoreId
      AND o.CreatedOnUtc >= @d90
) x
GROUP BY x.Sku;
CREATE CLUSTERED INDEX cx ON #sales (Sku);

SELECT pp.ProductCode AS Sku, pp.PPTCV, pp.GrossMargin, pp.FinalPrice
INTO #pricing
FROM GjirafaTranslations.dbo.ProductPricing pp
INNER JOIN #stock st ON st.Sku = pp.ProductCode
WHERE pp.CountryId = @TranslationCountryId;
CREATE CLUSTERED INDEX cx ON #pricing (Sku);

-- Oldest currently-held unit per SKU in THIS layer's warehouse, from the WMS check-in log.
-- StatusId IN (2,6,7) = units physically on hand; @WmsWarehouseId selects the layer's country
-- warehouse (KS=1, AL=5, MK=6). Drives the dead-stock freshness gate — a freshly received pre-order
-- isn't 'dead', it just arrived. LEFT-joined below, so a SKU with no check-in row leaves age NULL.
-- CAST: ProductCheckIns.Sku is a MAX-length type, which can't be an index key (error 1919) — bound it
-- to nvarchar(400) (matches Product.Sku) so #age can be indexed for the final join.
SELECT CAST(pci.Sku AS nvarchar(400)) AS Sku, DATEDIFF(DAY, MIN(pci.InsertDateTime), @now) AS OldestUnitAgeDays
INTO #age
FROM WarehouseManagmentSystem.dbo.ProductCheckIns pci
INNER JOIN #stock st ON st.Sku = pci.Sku
WHERE pci.StatusId IN (2, 6, 7)
  AND pci.WarehouseId = @WmsWarehouseId
GROUP BY CAST(pci.Sku AS nvarchar(400));
CREATE CLUSTERED INDEX cx ON #age (Sku);

-- Supplier (store) per SKU from the shared translations DB: ProductStoreMapping links ProductCode
-- (= SKU) to ZStores.Id, and ZStores.Store is the supplier name. The mapping is 1:1 per ProductCode
-- today; ROW_NUMBER keeps it that way defensively so a future duplicate can never fan out the final
-- SELECT. ZStores is LEFT-joined (a dangling StoreId keeps the id, name NULL) and deleted mappings
-- are skipped. LEFT-joined below: an unmapped SKU carries NULL supplier.
SELECT x.Sku, x.SupplierId, x.SupplierName
INTO #supplier
FROM (
    SELECT st.Sku,
           psm.StoreId AS SupplierId,
           CAST(z.Store AS nvarchar(100)) AS SupplierName,
           ROW_NUMBER() OVER (PARTITION BY st.Sku ORDER BY psm.Id DESC) AS rn
    FROM GjirafaTranslations.dbo.ProductStoreMapping psm
    INNER JOIN #stock st ON st.Sku = psm.ProductCode
    LEFT JOIN GjirafaTranslations.dbo.ZStores z ON z.Id = psm.StoreId
    WHERE ISNULL(psm.Deleted, 0) = 0
) x
WHERE x.rn = 1;
CREATE CLUSTERED INDEX cx ON #supplier (Sku);

-- Brand (manufacturer) per product: Product_Manufacturer_Mapping links the product to Manufacturer.Id,
-- and Manufacturer.Name is the brand. A product can carry several manufacturers (M:N); ROW_NUMBER picks
-- the PRIMARY one (lowest DisplayOrder, then mapping Id) — the same one the storefront shows first — so
-- the final SELECT can never fan out. Manufacturer is LEFT-joined (a dangling ManufacturerId keeps the
-- id, name NULL). LEFT-joined below: a product with no manufacturer carries NULL brand. This reuses the
-- same tables the Apple/Starlink exclusion (#exclbrand) keys off.
SELECT x.ProductId, x.BrandId, x.Brand
INTO #brand
FROM (
    SELECT st.ProductId,
           pmm.ManufacturerId AS BrandId,
           CAST(m.Name AS nvarchar(200)) AS Brand,
           ROW_NUMBER() OVER (PARTITION BY st.ProductId ORDER BY pmm.DisplayOrder, pmm.Id) AS rn
    FROM {opDb}.dbo.Product_Manufacturer_Mapping pmm
    INNER JOIN #stock st ON st.ProductId = pmm.ProductId
    LEFT JOIN {opDb}.dbo.Manufacturer m ON m.Id = pmm.ManufacturerId
) x
WHERE x.rn = 1;
CREATE CLUSTERED INDEX cx ON #brand (ProductId);

-- Latest tier price per product (StoreId = @StoreId), resolved in bulk (replaces correlated OUTER APPLY).
SELECT t.ProductId, t.Price, t.OldPrice
INTO #tier
FROM (
    SELECT tp.ProductId, tp.Price, tp.OldPrice,
           ROW_NUMBER() OVER (PARTITION BY tp.ProductId ORDER BY tp.Id DESC) AS rn
    FROM {opDb}.dbo.TierPrice tp
    INNER JOIN #stock st ON st.ProductId = tp.ProductId
    WHERE tp.StoreId = @StoreId
) t
WHERE t.rn = 1;
CREATE CLUSTERED INDEX cx ON #tier (ProductId);

-- Best (lowest) active discounted price per product, resolved in bulk (replaces correlated OUTER APPLY).
-- Depends on #tier because percentage discounts apply to the tier old/current price.
SELECT x.ProductId, x.DiscountedPrice
INTO #disc
FROM (
    SELECT dap.Product_Id AS ProductId,
           IIF(d.UsePercentage = 1,
               ISNULL(NULLIF(t.OldPrice, 0), t.Price) * (1 - d.DiscountPercentage / 100.0),
               d.DiscountAmount) AS DiscountedPrice,
           ROW_NUMBER() OVER (
               PARTITION BY dap.Product_Id
               ORDER BY IIF(d.UsePercentage = 1,
                            ISNULL(NULLIF(t.OldPrice, 0), t.Price) * (1 - d.DiscountPercentage / 100.0),
                            d.DiscountAmount) ASC) AS rn
    FROM {opDb}.dbo.Discount_AppliedToProducts dap
    INNER JOIN {opDb}.dbo.Discount d ON d.Id = dap.Discount_Id
    INNER JOIN #tier t ON t.ProductId = dap.Product_Id
    WHERE d.StoreId = @StoreId
      AND d.RequiresCouponCode = 0
      -- Only customer-FACING catalog deals. DiscountApplyTypeId: 1 = customer-role / special-customer
      -- discounts (VIP/Basic/Standard/Premium/Elite %), 0 = order-level / coupon / legacy / test promos —
      -- neither is the price a normal shopper sees. 2 = the per-product campaign deal (the fixed promo
      -- price; DiscountAmount IS the final price). Without this, a role discount like 'Elite-16%' leaks
      -- in and understates CurrentPrice below the real public price.
      AND d.DiscountApplyTypeId = 2
      AND @now BETWEEN ISNULL(d.StartDateUtc, '1900') AND ISNULL(d.EndDateUtc, '2999')
) x
WHERE x.rn = 1;
CREATE CLUSTERED INDEX cx ON #disc (ProductId);

-- ALL active customer-facing campaign discount ids per product (the same eligibility filter as #disc,
-- but every qualifying discount, not just the lowest-priced winner), aggregated best-deal-first so the
-- first id is the one that set CurrentPrice. Display/audit only — surfaced on the proposal so a reviewer
-- can see which promo(s) are live on the SKU; it never feeds the pricing math. STRING_AGG requires
-- SQL Server 2017+; an int id list is far under its 8000-byte cap.
SELECT x.ProductId,
       STRING_AGG(CAST(x.DiscountId AS varchar(10)), ',')
           WITHIN GROUP (ORDER BY x.DiscountedPrice, x.DiscountId) AS ActiveDiscountIds
INTO #discids
FROM (
    SELECT dap.Product_Id AS ProductId,
           d.Id AS DiscountId,
           IIF(d.UsePercentage = 1,
               ISNULL(NULLIF(t.OldPrice, 0), t.Price) * (1 - d.DiscountPercentage / 100.0),
               d.DiscountAmount) AS DiscountedPrice
    FROM {opDb}.dbo.Discount_AppliedToProducts dap
    INNER JOIN {opDb}.dbo.Discount d ON d.Id = dap.Discount_Id
    INNER JOIN #tier t ON t.ProductId = dap.Product_Id
    WHERE d.StoreId = @StoreId
      AND d.RequiresCouponCode = 0
      AND d.DiscountApplyTypeId = 2
      AND @now BETWEEN ISNULL(d.StartDateUtc, '1900') AND ISNULL(d.EndDateUtc, '2999')
) x
GROUP BY x.ProductId;
CREATE CLUSTERED INDEX cx ON #discids (ProductId);

SELECT
    st.ProductId,
    st.Sku,
    st.ProductName,
    st.Gtin,
    px.OldPrice,
    ISNULL(NULLIF(pr.FinalPrice, 0), px.OldPrice) AS AnchorPrice,
    CASE WHEN NULLIF(pr.FinalPrice, 0) IS NULL THEN 1 ELSE 0 END AS AnchorFromShelf,
    px.CurrentPrice,
    (px.OldPrice - px.CurrentPrice) / NULLIF(px.OldPrice, 0) AS CurrentDiscountPct,
    di.ActiveDiscountIds,
    pr.PPTCV,
    pr.GrossMargin,
    st.LocalWarehouseStock,
    st.Supplier_WarehouseStock,
    sup.SupplierId,
    sup.SupplierName,
    br.BrandId,
    br.Brand,
    st.IsNewProduct,
    ag.OldestUnitAgeDays,
    ISNULL(s.[7d_qty], 0)  AS [7d_qty],  ISNULL(s.[7d_net], 0)  AS [7d_net],
    ISNULL(s.[14d_qty], 0) AS [14d_qty],
    ISNULL(s.[30d_qty], 0) AS [30d_qty], ISNULL(s.[30d_net], 0) AS [30d_net],
    ISNULL(s.[60d_qty], 0) AS [60d_qty],
    ISNULL(s.[90d_qty], 0) AS [90d_qty], ISNULL(s.[90d_net], 0) AS [90d_net]
FROM #stock st
LEFT JOIN #tier t  ON t.ProductId  = st.ProductId
LEFT JOIN #disc dx ON dx.ProductId = st.ProductId
LEFT JOIN #discids di ON di.ProductId = st.ProductId
LEFT JOIN #age  ag ON ag.Sku       = st.Sku
LEFT JOIN #supplier sup ON sup.Sku = st.Sku
LEFT JOIN #brand br ON br.ProductId = st.ProductId
-- #sales is keyed by SKU (it now spans both platforms of the country), so join on SKU, not ProductId.
CROSS APPLY (
    SELECT
        ISNULL(NULLIF(t.OldPrice, 0), t.Price) AS OldPrice,
        -- A campaign never RAISES the price. Take the active discounted price ONLY when it is below
        -- the standing tier Price; otherwise fall back to t.Price. A 'deal' whose fixed DiscountAmount
        -- lands at/above the shelf price is a mis-configured campaign, not what the customer pays —
        -- using it would invert Current above Old/Anchor and read as a negative discount.
        CASE
            WHEN dx.DiscountedPrice IS NULL          THEN t.Price
            WHEN t.Price IS NULL                     THEN dx.DiscountedPrice
            WHEN dx.DiscountedPrice < t.Price        THEN dx.DiscountedPrice
            ELSE t.Price
        END AS CurrentPrice
) px
LEFT JOIN #pricing pr ON pr.Sku = st.Sku
LEFT JOIN #sales   s  ON s.Sku = st.Sku;

DROP TABLE #vendors, #exclbrand, #gtinfirst, #gtinreused, #stock, #sales, #pricing, #tier, #disc, #discids, #age, #supplier, #brand;";
}
