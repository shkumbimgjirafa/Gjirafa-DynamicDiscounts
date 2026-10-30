SET NOCOUNT ON;
-- Product map for the two Kosovo layers: category, brand and the flags the agents need.
-- Category: each product's deepest published mapping wins (ties broken by category id); L1 is its
-- root and L2 its second level (NULL when mapped only to a root). Merchandising roots (offers,
-- outlet, gift cards, "what's new", promo pages) are skipped so a product lands in its real
-- category; products mapped only there become 'Unknown'. Mall's six main groups (Kozmetikë,
-- Teknologji, Shtëpi, Sport, Vegla Pune, Fëmijë) + 'Others' follow the team's reporting rule.
-- GjirafaMall: only the tool's own vendors. Not deleted. Flags instead of filters, so views can
-- apply the tool's exclusions: IsOutlet (IsOutlet or the '[OUTLET' tag), IsExcludedBrand
-- (Apple/Starlink), IsGj50Listed (Mall SKU that also exists, not deleted, on Gjirafa50).

;WITH MallTree AS (
    SELECT c.Id, CAST(c.Name AS nvarchar(400)) AS L1, CAST(NULL AS nvarchar(400)) AS L2, 1 AS Lvl
    FROM GjirafaMall.dbo.Category c
    WHERE c.Published = 1 AND c.Deleted = 0 AND c.ParentCategoryId = 0
      AND c.Name NOT IN (N'Oferta Speciale', N'oferta-speciale-mk', N'cosmetics-promotion-blerta', N'Gift card', N'Outlet', N'Promovime')
    UNION ALL
    SELECT c.Id, t.L1, CAST(CASE WHEN t.Lvl = 1 THEN c.Name ELSE t.L2 END AS nvarchar(400)), t.Lvl + 1
    FROM GjirafaMall.dbo.Category c
    JOIN MallTree t ON c.ParentCategoryId = t.Id
    WHERE c.Published = 1 AND c.Deleted = 0
)
SELECT t.Id, t.L1, t.L2, t.Lvl INTO #mt FROM MallTree t OPTION (MAXRECURSION 20);

;WITH G50Tree AS (
    SELECT c.Id, CAST(c.Name AS nvarchar(400)) AS L1, CAST(NULL AS nvarchar(400)) AS L2, 1 AS Lvl
    FROM GjirafaEcommerce.dbo.Category c
    WHERE c.Published = 1 AND c.Deleted = 0 AND c.ParentCategoryId = 0
      AND c.Name NOT IN (N'Offers', N'OpenBox', N'TOP Card Campaign', N'Çfarë ka të re?', N'Gift card', N'Outlet', N'Promovime')
    UNION ALL
    SELECT c.Id, t.L1, CAST(CASE WHEN t.Lvl = 1 THEN c.Name ELSE t.L2 END AS nvarchar(400)), t.Lvl + 1
    FROM GjirafaEcommerce.dbo.Category c
    JOIN G50Tree t ON c.ParentCategoryId = t.Id
    WHERE c.Published = 1 AND c.Deleted = 0
)
SELECT t.Id, t.L1, t.L2, t.Lvl INTO #gt FROM G50Tree t OPTION (MAXRECURSION 20);

-- GjirafaMall KS (layer 1)
SELECT p.Id AS ProductId, p.Sku, p.Name, p.IsOutlet, p.VendorId
INTO #mp
FROM GjirafaMall.dbo.Product p WITH (NOLOCK)
JOIN GjirafaMall.dbo.Vendor v WITH (NOLOCK) ON v.Id = p.VendorId
WHERE p.Deleted = 0
  AND (v.Name LIKE '%Gjiraf%' OR v.Name IN ('Dino Toys', 'Mysu', 'Apple', 'YZATT'));
CREATE CLUSTERED INDEX cx ON #mp (ProductId);

SELECT DISTINCT CAST(g.Sku AS nvarchar(400)) COLLATE DATABASE_DEFAULT AS Sku
INTO #g50sku
FROM GjirafaEcommerce.dbo.Product g WITH (NOLOCK)
WHERE g.Deleted = 0 AND g.Sku IS NOT NULL;
CREATE CLUSTERED INDEX cx ON #g50sku (Sku);

SELECT 1 AS LayerId, p.ProductId,
       CAST(p.Sku AS nvarchar(400)) COLLATE DATABASE_DEFAULT AS Sku,
       CAST(p.Name AS nvarchar(400)) COLLATE DATABASE_DEFAULT AS Name,
       CAST(b.Brand AS nvarchar(400)) COLLATE DATABASE_DEFAULT AS Brand,
       CAST(ISNULL(c.L1, N'Unknown') AS nvarchar(400)) COLLATE DATABASE_DEFAULT AS CategoryL1,
       CAST(c.L2 AS nvarchar(400)) COLLATE DATABASE_DEFAULT AS CategoryL2,
       c.CategoryId,
       CAST(CASE WHEN c.L1 IN (N'Kozmetikë', N'Teknologji', N'Shtëpi', N'Sport', N'Vegla Pune', N'Fëmijë') THEN c.L1 ELSE N'Others' END AS nvarchar(100)) COLLATE DATABASE_DEFAULT AS MainGroup,
       CAST(CASE WHEN p.IsOutlet = 1 OR CHARINDEX('[OUTLET', ISNULL(p.Name, '')) > 0 THEN 1 ELSE 0 END AS bit) AS IsOutlet,
       CAST(CASE WHEN b.Brand LIKE 'Apple%' OR b.Brand LIKE 'Starlink%' THEN 1 ELSE 0 END AS bit) AS IsExcludedBrand,
       CAST(CASE WHEN EXISTS (SELECT 1 FROM #g50sku s WHERE s.Sku = CAST(p.Sku AS nvarchar(400)) COLLATE DATABASE_DEFAULT) THEN 1 ELSE 0 END AS bit) AS IsGj50Listed,
       p.VendorId
INTO #out
FROM #mp p
OUTER APPLY (SELECT TOP 1 m.Name AS Brand
             FROM GjirafaMall.dbo.Product_Manufacturer_Mapping pm WITH (NOLOCK)
             JOIN GjirafaMall.dbo.Manufacturer m WITH (NOLOCK) ON m.Id = pm.ManufacturerId
             WHERE pm.ProductId = p.ProductId ORDER BY pm.DisplayOrder, m.Id) b
OUTER APPLY (SELECT TOP 1 t.L1, t.L2, t.Id AS CategoryId
             FROM GjirafaMall.dbo.Product_Category_Mapping pcm WITH (NOLOCK)
             JOIN #mt t ON t.Id = pcm.CategoryId
             WHERE pcm.ProductId = p.ProductId ORDER BY t.Lvl DESC, t.Id) c;

-- Gjirafa50 KS (layer 4)
INSERT INTO #out
SELECT 4, p.Id,
       CAST(p.Sku AS nvarchar(400)) COLLATE DATABASE_DEFAULT,
       CAST(p.Name AS nvarchar(400)) COLLATE DATABASE_DEFAULT,
       CAST(b.Brand AS nvarchar(400)) COLLATE DATABASE_DEFAULT,
       CAST(ISNULL(c.L1, N'Unknown') AS nvarchar(400)) COLLATE DATABASE_DEFAULT,
       CAST(c.L2 AS nvarchar(400)) COLLATE DATABASE_DEFAULT,
       c.CategoryId,
       CAST(ISNULL(c.L1, N'Unknown') AS nvarchar(100)) COLLATE DATABASE_DEFAULT,
       CAST(CASE WHEN p.IsOutlet = 1 OR CHARINDEX('[OUTLET', ISNULL(p.Name, '')) > 0 THEN 1 ELSE 0 END AS bit),
       CAST(CASE WHEN b.Brand LIKE 'Apple%' OR b.Brand LIKE 'Starlink%' THEN 1 ELSE 0 END AS bit),
       CAST(0 AS bit),
       p.VendorId
FROM GjirafaEcommerce.dbo.Product p WITH (NOLOCK)
OUTER APPLY (SELECT TOP 1 m.Name AS Brand
             FROM GjirafaEcommerce.dbo.Product_Manufacturer_Mapping pm WITH (NOLOCK)
             JOIN GjirafaEcommerce.dbo.Manufacturer m WITH (NOLOCK) ON m.Id = pm.ManufacturerId
             WHERE pm.ProductId = p.Id ORDER BY pm.DisplayOrder, m.Id) b
OUTER APPLY (SELECT TOP 1 t.L1, t.L2, t.Id AS CategoryId
             FROM GjirafaEcommerce.dbo.Product_Category_Mapping pcm WITH (NOLOCK)
             JOIN #gt t ON t.Id = pcm.CategoryId
             WHERE pcm.ProductId = p.Id ORDER BY t.Lvl DESC, t.Id) c
WHERE p.Deleted = 0;

SELECT LayerId, ProductId, Sku, Name, Brand, CategoryL1, CategoryL2, CategoryId, MainGroup,
       IsOutlet, IsExcludedBrand, IsGj50Listed, VendorId
FROM #out;
