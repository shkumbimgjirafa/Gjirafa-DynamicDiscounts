-- Weekly pcs/gjirafa50 sales + stock report, emailed every Monday 09:00 (Europe/Belgrade) by the
-- engine's WeeklyPcsReportWorker. Edit freely: every column of the FINAL SELECT goes into the CSV in
-- order; columns named pro (units sold) and lag (units in stock) also feed the email summary.
-- Runs on the read-only SourceReadOnly connection. Push to main = live on the next deploy.

DECLARE @tz        sysname = 'Central European Standard Time';
DECLARE @todayLoc  date    = CAST(SYSDATETIMEOFFSET() AT TIME ZONE @tz AS date);
DECLARE @thisMon   date    = DATEADD(DAY, -(DATEDIFF(DAY, '19000101', @todayLoc) % 7), @todayLoc);
DECLARE @weekStart datetime = DATEADD(DAY, -7, @thisMon);
DECLARE @weekEnd   datetime = @thisMon;

WITH checkout AS
(
    SELECT di.OrderId, di.PlatformId, MAX(di.InsertDateTime) AS CheckoutDate
    FROM DeliveryPlatform.dbo.DeliveryItems di
    WHERE di.PlatformId IN (1,2)
    GROUP BY di.OrderId, di.PlatformId
),
appcom AS
(
    SELECT p.Sku,
           MAX(COALESCE(NULLIF(p.ManufacturerPartNumber, ''), p.Gtin)) AS sifra,
           SUM(oi.Quantity) AS quanitysold
    FROM GjirafaMall.dbo.OrderItem oi
    JOIN GjirafaMall.dbo.[Order] o ON o.Id = oi.OrderId
    JOIN GjirafaMall.dbo.Product p ON p.Id = oi.ProductId
    JOIN checkout c ON c.OrderId = o.Id AND c.PlatformId = 1
    WHERE o.OrderStatusId IN (10,20,30)
      AND o.StoreId = 2
      AND p.VendorId = 645
      AND c.CheckoutDate >= @weekStart
      AND c.CheckoutDate <  @weekEnd
    GROUP BY p.Sku

    UNION ALL

    SELECT p.Sku,
           MAX(COALESCE(NULLIF(p.ManufacturerPartNumber, ''), p.Gtin)) AS sifra,
           SUM(oi.Quantity) AS quanitysold
    FROM GjirafaEcommerce.dbo.OrderItem oi
    JOIN GjirafaEcommerce.dbo.[Order] o ON o.Id = oi.OrderId
    JOIN GjirafaEcommerce.dbo.Product p ON p.Id = oi.ProductId
    JOIN checkout c ON c.OrderId = o.Id AND c.PlatformId = 2
    WHERE o.OrderStatusId IN (10,20,30)
      AND o.StoreId = 2
      AND p.VendorId = 49
      AND c.CheckoutDate >= @weekStart
      AND c.CheckoutDate <  @weekEnd
    GROUP BY p.Sku
),
grouped AS
(
    SELECT Sku, MAX(sifra) AS sifra, SUM(quanitysold) AS qty
    FROM appcom
    GROUP BY Sku
),
stock AS
(
    SELECT pci.Sku,
           COUNT(DISTINCT pci.ProductItemUniqueIdentifier) AS units
    FROM WarehouseManagmentSystem.dbo.ProductCheckIns pci
    WHERE pci.StoreId = 645
      AND pci.StatusId IN (2,6,7)
      AND pci.WarehouseId IN (1,12)
    GROUP BY pci.Sku
)
SELECT
    'pcs/gjirafa50' AS pos,
    COALESCE(s.Sku, g.Sku) AS sku,
    COALESCE(NULLIF(pm.ManufacturerPartNumber, ''), pm.Gtin, g.sifra) AS sifra,
    ISNULL(g.qty, 0)   AS pro,
    ISNULL(s.units, 0) AS lag,
    CASE
        WHEN COALESCE(NULLIF(pm.ManufacturerPartNumber, ''), pm.Gtin, g.sifra) LIKE 'm%/a' THEN 0
        ELSE 1
    END AS flag,
    COALESCE(bm.brand COLLATE DATABASE_DEFAULT, be.brand COLLATE DATABASE_DEFAULT) AS brand
FROM stock s
FULL OUTER JOIN grouped g ON g.Sku = s.Sku
LEFT JOIN GjirafaMall.dbo.Product pm ON pm.Sku = COALESCE(s.Sku, g.Sku)
-- brand = the product's primary manufacturer (lowest DisplayOrder), from GjirafaMall
OUTER APPLY
(
    SELECT TOP 1 m.Name AS brand
    FROM GjirafaMall.dbo.Product_Manufacturer_Mapping pmm
    JOIN GjirafaMall.dbo.Manufacturer m ON m.Id = pmm.ManufacturerId
    WHERE pmm.ProductId = pm.Id
    ORDER BY pmm.DisplayOrder, pmm.Id
) bm
-- ...or from GjirafaEcommerce (Gjirafa50) when the Mall product has none
OUTER APPLY
(
    SELECT TOP 1 m.Name AS brand
    FROM GjirafaEcommerce.dbo.Product pe
    JOIN GjirafaEcommerce.dbo.Product_Manufacturer_Mapping pmm ON pmm.ProductId = pe.Id
    JOIN GjirafaEcommerce.dbo.Manufacturer m ON m.Id = pmm.ManufacturerId
    WHERE bm.brand IS NULL
      AND pe.Sku COLLATE DATABASE_DEFAULT = COALESCE(s.Sku, g.Sku) COLLATE DATABASE_DEFAULT
    ORDER BY pe.Deleted, pmm.DisplayOrder, pmm.Id
) be;
