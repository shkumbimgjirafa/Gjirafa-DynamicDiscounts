SET NOCOUNT ON;
-- How the shops store regular prices today, so the regular-price publisher writes them the way the team's push does.
-- Aggregates only (no product rows leave the replica): per shop DB and store, how many TierPrice rows each product has,
-- which optional columns are used, how Price and OldPrice relate, and whether Product.Price follows the store's newest
-- tier row. Read-only.
WITH t AS (
    SELECT 'GjirafaMall' AS Db, tp.Id, tp.ProductId, tp.StoreId, tp.CustomerRoleId, tp.Quantity,
           CAST(tp.Price AS decimal(18, 4)) AS Price, CAST(tp.OldPrice AS decimal(18, 4)) AS OldPrice,
           tp.StartDateTimeUtc, tp.EndDateTimeUtc, tp.ProfitMargin,
           ROW_NUMBER() OVER (PARTITION BY tp.ProductId, tp.StoreId ORDER BY tp.Id DESC) AS Rn,
           CAST(p.Price AS decimal(18, 4)) AS ProductPrice, CAST(p.OldPrice AS decimal(18, 4)) AS ProductOldPrice,
           p.HasTierPrices, p.Deleted
    FROM GjirafaMall.dbo.TierPrice tp WITH (NOLOCK)
    JOIN GjirafaMall.dbo.Product p WITH (NOLOCK) ON p.Id = tp.ProductId
    UNION ALL
    SELECT 'GjirafaEcommerce', tp.Id, tp.ProductId, tp.StoreId, tp.CustomerRoleId, tp.Quantity,
           CAST(tp.Price AS decimal(18, 4)), CAST(tp.OldPrice AS decimal(18, 4)),
           tp.StartDateTimeUtc, tp.EndDateTimeUtc, tp.ProfitMargin,
           ROW_NUMBER() OVER (PARTITION BY tp.ProductId, tp.StoreId ORDER BY tp.Id DESC),
           CAST(p.Price AS decimal(18, 4)), CAST(p.OldPrice AS decimal(18, 4)), p.HasTierPrices, p.Deleted
    FROM GjirafaEcommerce.dbo.TierPrice tp WITH (NOLOCK)
    JOIN GjirafaEcommerce.dbo.Product p WITH (NOLOCK) ON p.Id = tp.ProductId
)
SELECT CAST(Db AS varchar(20)) AS Db, StoreId,
       COUNT_BIG(*) AS TierRows,
       SUM(CASE WHEN Rn = 1 THEN 1 ELSE 0 END) AS Products,
       SUM(CASE WHEN Rn = 2 THEN 1 ELSE 0 END) AS ProductsWithOlderRows,
       MAX(Rn) AS MaxRowsPerProduct,
       SUM(CASE WHEN Rn = 1 AND CustomerRoleId IS NULL THEN 1 ELSE 0 END) AS Newest_RoleNull,
       SUM(CASE WHEN Rn = 1 AND Quantity = 1 THEN 1 ELSE 0 END) AS Newest_Qty1,
       SUM(CASE WHEN Rn = 1 AND Quantity = 0 THEN 1 ELSE 0 END) AS Newest_Qty0,
       SUM(CASE WHEN Rn = 1 AND StartDateTimeUtc IS NOT NULL THEN 1 ELSE 0 END) AS Newest_StartSet,
       SUM(CASE WHEN Rn = 1 AND EndDateTimeUtc IS NOT NULL THEN 1 ELSE 0 END) AS Newest_EndSet,
       SUM(CASE WHEN Rn = 1 AND ProfitMargin IS NOT NULL THEN 1 ELSE 0 END) AS Newest_ProfitMarginSet,
       SUM(CASE WHEN Rn = 1 AND OldPrice = 0 THEN 1 ELSE 0 END) AS Newest_OldPriceZero,
       SUM(CASE WHEN Rn = 1 AND OldPrice > Price THEN 1 ELSE 0 END) AS Newest_OldAbovePrice,
       SUM(CASE WHEN Rn = 1 AND OldPrice = Price THEN 1 ELSE 0 END) AS Newest_OldEqualsPrice,
       SUM(CASE WHEN Rn = 1 AND OldPrice > 0 AND OldPrice < Price THEN 1 ELSE 0 END) AS Newest_OldBelowPrice,
       SUM(CASE WHEN Rn = 1 AND Price = ProductPrice THEN 1 ELSE 0 END) AS Newest_PriceEqualsProductPrice,
       SUM(CASE WHEN Rn = 1 AND OldPrice = ProductOldPrice THEN 1 ELSE 0 END) AS Newest_OldEqualsProductOld,
       SUM(CASE WHEN Rn = 1 AND HasTierPrices = 0 THEN 1 ELSE 0 END) AS Newest_ProductHasTierPricesOff,
       SUM(CASE WHEN Rn = 1 AND Deleted = 1 THEN 1 ELSE 0 END) AS Newest_ProductDeleted,
       SUM(CASE WHEN Rn = 1 AND Price <> ROUND(Price, 2) THEN 1 ELSE 0 END) AS Newest_PriceMoreThan2dp,
       MIN(Id) AS MinId, MAX(Id) AS MaxId
FROM t
GROUP BY Db, StoreId;
