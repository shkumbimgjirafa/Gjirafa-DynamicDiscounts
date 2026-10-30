SET NOCOUNT ON;
-- When prices were pushed to the North Macedonia / Albania stores (StoreId 1 = MK, 3 = AL in both
-- shop DBs), 15 Jun – today: every set of discounts the team created, grouped by name prefix
-- (the name minus its per-SKU tail), apply type, platform type and start day. Sets of 200+ rows
-- only; a regular-price push is public (DiscountApplyTypeId 2) on all platforms
-- (DiscountPlatformTypeId 0), and DiscountPlatformTypeId 2 is app-only. Uses the
-- (StoreId, EndDateUtc, ...) index: a set pushed since July ends after 1 Jul.
SELECT CAST(x.LayerId AS int) AS LayerId, CAST(x.Db AS varchar(20)) AS Db, x.StoreId,
       CAST(LEFT(x.Name, 14) AS nvarchar(14)) AS NamePrefix, CAST(MIN(x.Name) AS nvarchar(400)) AS ExampleName,
       CAST(x.DiscountApplyTypeId AS int) AS ApplyType, CAST(x.DiscountPlatformTypeId AS int) AS PlatformType,
       CAST(CAST(x.StartDateUtc AS date) AS datetime) AS StartDay, MAX(x.EndDateUtc) AS LastEndUtc,
       COUNT(*) AS Discounts
FROM (
    SELECT CASE d.StoreId WHEN 1 THEN 2 ELSE 3 END AS LayerId, 'GjirafaMall' AS Db, d.StoreId,
           CAST(d.Name AS nvarchar(400)) COLLATE DATABASE_DEFAULT AS Name, d.DiscountApplyTypeId, d.DiscountPlatformTypeId,
           d.StartDateUtc, d.EndDateUtc
    FROM GjirafaMall.dbo.Discount d WITH (NOLOCK)
    WHERE d.StoreId IN (1, 3) AND (d.EndDateUtc >= '2026-07-01' OR d.EndDateUtc IS NULL)
      AND d.StartDateUtc >= '2026-06-15'
    UNION ALL
    SELECT CASE d.StoreId WHEN 1 THEN 5 ELSE 6 END, 'GjirafaEcommerce', d.StoreId,
           CAST(d.Name AS nvarchar(400)) COLLATE DATABASE_DEFAULT, d.DiscountApplyTypeId, d.DiscountPlatformTypeId,
           d.StartDateUtc, d.EndDateUtc
    FROM GjirafaEcommerce.dbo.Discount d WITH (NOLOCK)
    WHERE d.StoreId IN (1, 3) AND (d.EndDateUtc >= '2026-07-01' OR d.EndDateUtc IS NULL)
      AND d.StartDateUtc >= '2026-06-15'
) x
GROUP BY x.LayerId, x.Db, x.StoreId, LEFT(x.Name, 14), x.DiscountApplyTypeId, x.DiscountPlatformTypeId, CAST(x.StartDateUtc AS date)
HAVING COUNT(*) >= 200;
