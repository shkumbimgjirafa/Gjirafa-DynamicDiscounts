SET NOCOUNT ON;
-- What TierPrice.ProfitMargin holds: compared, on the newest 20,000 KS tier rows of each shop, with the margins that can be
-- computed from ProductPricing (PPTCV incl. VAT, local currency; KS = CountryId 1). Aggregates only. Read-only.
WITH t AS (
    SELECT TOP (20000) 'GjirafaMall' AS Db, tp.Id, p.Sku, CAST(tp.Price AS decimal(18, 4)) AS Price,
           CAST(tp.OldPrice AS decimal(18, 4)) AS OldPrice, CAST(tp.ProfitMargin AS decimal(9, 4)) AS ProfitMargin
    FROM GjirafaMall.dbo.TierPrice tp WITH (NOLOCK) JOIN GjirafaMall.dbo.Product p WITH (NOLOCK) ON p.Id = tp.ProductId
    WHERE tp.StoreId = 2 AND tp.ProfitMargin IS NOT NULL AND tp.Price > 0
    ORDER BY tp.Id DESC
), u AS (
    SELECT TOP (20000) 'GjirafaEcommerce' AS Db, tp.Id, p.Sku, CAST(tp.Price AS decimal(18, 4)) AS Price,
           CAST(tp.OldPrice AS decimal(18, 4)) AS OldPrice, CAST(tp.ProfitMargin AS decimal(9, 4)) AS ProfitMargin
    FROM GjirafaEcommerce.dbo.TierPrice tp WITH (NOLOCK) JOIN GjirafaEcommerce.dbo.Product p WITH (NOLOCK) ON p.Id = tp.ProductId
    WHERE tp.StoreId = 2 AND tp.ProfitMargin IS NOT NULL AND tp.Price > 0
    ORDER BY tp.Id DESC
), x AS (
    SELECT a.Db, a.Price, a.OldPrice, a.ProfitMargin, pp.PPTCV, pp.GrossMargin, pp.FinalPrice
    FROM (SELECT * FROM t UNION ALL SELECT * FROM u) a
    JOIN GjirafaTranslations.dbo.ProductPricing pp WITH (NOLOCK)
      ON pp.ProductCode = CAST(a.Sku AS nvarchar(400)) COLLATE DATABASE_DEFAULT AND pp.CountryId = 1
    WHERE pp.PPTCV > 0
)
SELECT CAST(Db AS varchar(20)) AS Db, COUNT(*) AS Rows_,
       CAST(AVG(ProfitMargin) AS decimal(9, 4)) AS AvgProfitMargin,
       CAST(MIN(ProfitMargin) AS decimal(9, 4)) AS MinPM, CAST(MAX(ProfitMargin) AS decimal(9, 4)) AS MaxPM,
       SUM(CASE WHEN ABS(ProfitMargin - (Price - PPTCV) / Price) < 0.005 THEN 1 ELSE 0 END) AS Match_PriceMarginFraction,
       SUM(CASE WHEN ABS(ProfitMargin - 100 * (Price - PPTCV) / Price) < 0.5 THEN 1 ELSE 0 END) AS Match_PriceMarginPct,
       SUM(CASE WHEN ABS(ProfitMargin - (Price - PPTCV) / PPTCV) < 0.005 THEN 1 ELSE 0 END) AS Match_MarkupFraction,
       SUM(CASE WHEN ABS(ProfitMargin - GrossMargin) < 0.005 THEN 1 ELSE 0 END) AS Match_GrossMargin,
       SUM(CASE WHEN ABS(ProfitMargin - GrossMargin / 100.0) < 0.005 THEN 1 ELSE 0 END) AS Match_GrossMarginOver100,
       SUM(CASE WHEN OldPrice > 0 AND ABS(ProfitMargin - (OldPrice - PPTCV) / OldPrice) < 0.005 THEN 1 ELSE 0 END) AS Match_OldMarginFraction,
       SUM(CASE WHEN ABS(Price - FinalPrice) < 0.01 THEN 1 ELSE 0 END) AS PriceEqualsFinalPrice,
       SUM(CASE WHEN OldPrice > 0 AND ABS(OldPrice - FinalPrice) < 0.01 THEN 1 ELSE 0 END) AS OldEqualsFinalPrice,
       CAST(AVG(GrossMargin) AS decimal(9, 4)) AS AvgGrossMargin
FROM x GROUP BY Db;
