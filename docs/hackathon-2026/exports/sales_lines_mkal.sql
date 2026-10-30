SET NOCOUNT ON;
-- Sales lines for the four North Macedonia / Albania layers (2 GjirafaMall MK, 3 GjirafaMall AL,
-- 5 Gjirafa50 MK, 6 Gjirafa50 AL), 1 Jul 2026 to today: the base for backtest lever 4 (coverage).
-- Same scope rules as sales_lines_ks.sql: valid live orders only (not soft-deleted, status 10/20/30
-- in both SR and NOP), no outlets (IsOutlet or the '[OUTLET' name tag), no Apple/Starlink, and on
-- GjirafaMall only the tool's own vendors. Money columns are LINE TOTALS in the layer's LOCAL
-- currency (MKD / ALL); the *Eur columns divide by the shop's Currency.Rate at extraction time
-- (EurRate on each row). Cogs is the booked cost. Kept apart from hk.SalesLines (EUR, KS only,
-- reloaded daily). No order or customer ids leave the replica.
DECLARE @from datetime = '2026-07-01';
DECLARE @mkd decimal(18, 6) = (SELECT Rate FROM GjirafaEcommerce.dbo.Currency WITH (NOLOCK) WHERE CurrencyCode = 'MKD');
DECLARE @all decimal(18, 6) = (SELECT Rate FROM GjirafaEcommerce.dbo.Currency WITH (NOLOCK) WHERE CurrencyCode = 'ALL');

SELECT d.ID AS SrLineId, x.LayerId, CAST(x.Currency AS varchar(3)) AS Currency, x.Rate AS EurRate,
       CAST(d.ProductCode AS nvarchar(400)) COLLATE DATABASE_DEFAULT AS Sku, d.ProductId, d.DateAdded,
       d.Qty, d.NetoPrice, d.BrutoPrice, d.Cogs, d.Margin, d.ProductDiscount, d.SalesNoDiscount, d.Vat,
       d.VendorId, d.StoreId,
       CAST(d.NetoPrice / x.Rate AS decimal(14, 4)) AS NetRevenueEur,
       CAST(d.BrutoPrice / x.Rate AS decimal(14, 4)) AS GrossRevenueEur,
       CAST(d.Cogs / x.Rate AS decimal(14, 4)) AS CogsEur,
       CAST(d.Margin / x.Rate AS decimal(14, 4)) AS GrossProfitEur
FROM GjirafaTranslations.dbo.SR_ProductsData d WITH (NOLOCK)
CROSS APPLY (SELECT CASE WHEN d.PlatformId = 3 AND d.CompanyId = 2 THEN 5 WHEN d.PlatformId = 1 AND d.CompanyId = 19 THEN 6 END AS LayerId,
                    CASE WHEN d.PlatformId = 3 THEN 'MKD' ELSE 'ALL' END AS Currency,
                    CASE WHEN d.PlatformId = 3 THEN @mkd ELSE @all END AS Rate) x
LEFT JOIN GjirafaEcommerce.dbo.[Order] o WITH (NOLOCK) ON o.Id = d.OrderId
LEFT JOIN GjirafaEcommerce.dbo.Product p WITH (NOLOCK) ON p.Id = d.ProductId
WHERE ((d.PlatformId = 3 AND d.CompanyId = 2) OR (d.PlatformId = 1 AND d.CompanyId = 19))
  AND d.DateAdded >= @from
  AND d.OrderStatusId IN (10, 20, 30)
  AND ISNULL(o.Deleted, 0) = 0 AND ISNULL(o.OrderStatusId, d.OrderStatusId) IN (10, 20, 30)
  AND ISNULL(p.IsOutlet, 0) = 0 AND CHARINDEX('[OUTLET', ISNULL(p.Name, '')) = 0
  AND NOT EXISTS (SELECT 1 FROM GjirafaEcommerce.dbo.Product_Manufacturer_Mapping pm WITH (NOLOCK)
                  JOIN GjirafaEcommerce.dbo.Manufacturer m WITH (NOLOCK) ON m.Id = pm.ManufacturerId
                  WHERE pm.ProductId = d.ProductId AND (m.Name LIKE 'Apple%' OR m.Name LIKE 'Starlink%'))

UNION ALL

SELECT d.ID, x.LayerId, CAST(x.Currency AS varchar(3)), x.Rate,
       CAST(d.ProductCode AS nvarchar(400)) COLLATE DATABASE_DEFAULT, d.ProductId, d.DateAdded,
       d.Qty, d.NetoPrice, d.BrutoPrice, d.Cogs, d.Margin, d.ProductDiscount, d.SalesNoDiscount, d.Vat,
       d.VendorId, d.StoreId,
       CAST(d.NetoPrice / x.Rate AS decimal(14, 4)), CAST(d.BrutoPrice / x.Rate AS decimal(14, 4)),
       CAST(d.Cogs / x.Rate AS decimal(14, 4)), CAST(d.Margin / x.Rate AS decimal(14, 4))
FROM GjirafaTranslations.dbo.SR_ProductsData d WITH (NOLOCK)
CROSS APPLY (SELECT CASE d.CompanyId WHEN 1 THEN 2 WHEN 3 THEN 3 END AS LayerId,
                    CASE d.CompanyId WHEN 1 THEN 'MKD' ELSE 'ALL' END AS Currency,
                    CASE d.CompanyId WHEN 1 THEN @mkd ELSE @all END AS Rate) x
LEFT JOIN GjirafaMall.dbo.[Order] o WITH (NOLOCK) ON o.Id = d.OrderId
JOIN GjirafaMall.dbo.Product p WITH (NOLOCK) ON p.Id = d.ProductId
JOIN GjirafaMall.dbo.Vendor v WITH (NOLOCK) ON v.Id = p.VendorId
WHERE d.PlatformId = 2 AND d.CompanyId IN (1, 3)
  AND d.DateAdded >= @from
  AND d.OrderStatusId IN (10, 20, 30)
  AND ISNULL(o.Deleted, 0) = 0 AND ISNULL(o.OrderStatusId, d.OrderStatusId) IN (10, 20, 30)
  AND (v.Name LIKE '%Gjiraf%' OR v.Name IN ('Dino Toys', 'Mysu', 'Apple', 'YZATT'))
  AND ISNULL(p.IsOutlet, 0) = 0 AND CHARINDEX('[OUTLET', ISNULL(p.Name, '')) = 0
  AND NOT EXISTS (SELECT 1 FROM GjirafaMall.dbo.Product_Manufacturer_Mapping pm WITH (NOLOCK)
                  JOIN GjirafaMall.dbo.Manufacturer m WITH (NOLOCK) ON m.Id = pm.ManufacturerId
                  WHERE pm.ProductId = d.ProductId AND (m.Name LIKE 'Apple%' OR m.Name LIKE 'Starlink%'));
