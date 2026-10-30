SET NOCOUNT ON;
-- Sales lines for the two Kosovo layers, 1 Oct 2024 to today: the agents' sales history, the
-- backtest's base, and two years of campaign windows. Same scope rules as the pricing tool:
-- valid live orders only (not soft-deleted, which also drops load-test orders; status 10/20/30 in
-- both SR and NOP), no outlets (IsOutlet or the '[OUTLET' name tag), no Apple/Starlink, and on
-- GjirafaMall only the tool's own vendors. Money columns are LINE TOTALS; Cogs is the booked cost.
-- No order or customer ids leave the replica: the cross-dock class is computed here.
DECLARE @from datetime = '2024-10-01';

SELECT d.ID, 4 AS LayerId, 2 AS WmsPlatform, d.OrderId, d.ProductId,
       CAST(d.ProductCode AS nvarchar(400)) COLLATE DATABASE_DEFAULT AS Sku,
       d.DateAdded, d.Qty, d.NetoPrice, d.BrutoPrice, d.Cogs, d.Margin, d.ProductDiscount,
       d.SalesNoDiscount, d.Vat, d.VendorId, d.StoreId
INTO #lines
FROM GjirafaTranslations.dbo.SR_ProductsData d WITH (NOLOCK)
LEFT JOIN GjirafaEcommerce.dbo.[Order] o WITH (NOLOCK) ON o.Id = d.OrderId
LEFT JOIN GjirafaEcommerce.dbo.Product p WITH (NOLOCK) ON p.Id = d.ProductId
WHERE d.PlatformId = 1 AND d.CompanyId = 1
  AND d.DateAdded >= @from
  AND d.OrderStatusId IN (10, 20, 30)
  AND ISNULL(o.Deleted, 0) = 0 AND ISNULL(o.OrderStatusId, d.OrderStatusId) IN (10, 20, 30)
  AND ISNULL(p.IsOutlet, 0) = 0 AND CHARINDEX('[OUTLET', ISNULL(p.Name, '')) = 0
  AND NOT EXISTS (SELECT 1 FROM GjirafaEcommerce.dbo.Product_Manufacturer_Mapping pm WITH (NOLOCK)
                  JOIN GjirafaEcommerce.dbo.Manufacturer m WITH (NOLOCK) ON m.Id = pm.ManufacturerId
                  WHERE pm.ProductId = d.ProductId AND (m.Name LIKE 'Apple%' OR m.Name LIKE 'Starlink%'));

INSERT INTO #lines
SELECT d.ID, 1, 1, d.OrderId, d.ProductId,
       CAST(d.ProductCode AS nvarchar(400)) COLLATE DATABASE_DEFAULT,
       d.DateAdded, d.Qty, d.NetoPrice, d.BrutoPrice, d.Cogs, d.Margin, d.ProductDiscount,
       d.SalesNoDiscount, d.Vat, d.VendorId, d.StoreId
FROM GjirafaTranslations.dbo.SR_ProductsData d WITH (NOLOCK)
LEFT JOIN GjirafaMall.dbo.[Order] o WITH (NOLOCK) ON o.Id = d.OrderId
JOIN GjirafaMall.dbo.Product p WITH (NOLOCK) ON p.Id = d.ProductId
JOIN GjirafaMall.dbo.Vendor v WITH (NOLOCK) ON v.Id = p.VendorId
WHERE d.PlatformId = 2 AND d.CompanyId = 0
  AND d.DateAdded >= @from
  AND d.OrderStatusId IN (10, 20, 30)
  AND ISNULL(o.Deleted, 0) = 0 AND ISNULL(o.OrderStatusId, d.OrderStatusId) IN (10, 20, 30)
  AND (v.Name LIKE '%Gjiraf%' OR v.Name IN ('Dino Toys', 'Mysu', 'Apple', 'YZATT'))
  AND ISNULL(p.IsOutlet, 0) = 0 AND CHARINDEX('[OUTLET', ISNULL(p.Name, '')) = 0
  AND NOT EXISTS (SELECT 1 FROM GjirafaMall.dbo.Product_Manufacturer_Mapping pm WITH (NOLOCK)
                  JOIN GjirafaMall.dbo.Manufacturer m WITH (NOLOCK) ON m.Id = pm.ManufacturerId
                  WHERE pm.ProductId = d.ProductId AND (m.Name LIKE 'Apple%' OR m.Name LIKE 'Starlink%'));

-- First check-in per order + SKU in a Kosovo warehouse (Prishtinë 1, Prizren 12).
SELECT pci.OrderId, pci.PlatformId, CAST(pci.Sku AS nvarchar(400)) COLLATE DATABASE_DEFAULT AS Sku,
       MIN(pci.InsertDateTime) AS FirstIn
INTO #u
FROM WarehouseManagmentSystem.dbo.ProductCheckIns pci WITH (NOLOCK)
WHERE pci.OrderId > 0 AND pci.WarehouseId IN (1, 12)
GROUP BY pci.OrderId, pci.PlatformId, CAST(pci.Sku AS nvarchar(400));
CREATE CLUSTERED INDEX cx ON #u (OrderId, Sku);

-- InStock = checked in before the order; CrossDock = checked in at or after the order;
-- NoWmsUnit = never passed through a KS warehouse. WMS platform 0 = older units without a platform.
SELECT l.ID AS SrLineId, l.LayerId, l.Sku, l.ProductId, l.DateAdded, l.Qty, l.NetoPrice, l.BrutoPrice,
       l.Cogs, l.Margin, l.ProductDiscount, l.SalesNoDiscount, l.Vat, l.VendorId, l.StoreId,
       CAST(CASE WHEN x.FirstIn IS NULL THEN 'NoWmsUnit'
                 WHEN x.FirstIn < l.DateAdded THEN 'InStock'
                 ELSE 'CrossDock' END AS varchar(10)) AS StockClass
FROM #lines l
OUTER APPLY (SELECT MIN(u.FirstIn) AS FirstIn FROM #u u
             WHERE u.OrderId = l.OrderId AND u.Sku = l.Sku AND u.PlatformId IN (l.WmsPlatform, 0)) x;
