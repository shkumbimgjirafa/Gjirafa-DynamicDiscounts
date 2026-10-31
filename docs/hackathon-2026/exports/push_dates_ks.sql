SET NOCOUNT ON;
-- When prices were pushed to the Kosovo store (StoreId 2; GjirafaMall = layer 1, Gjirafa50 = layer 4), 15 Jun - today:
-- every set of discounts, grouped by name prefix (the name minus its per-SKU tail), apply type, platform type and
-- start day; sets of 200+ rows only. Same query as push_dates_mkal.sql. Shows how often the manual engine's prices were
-- pushed and how long each set stayed live (the without-the-build line). DiscountPlatformTypeId 2 is app-only.
SELECT CAST(x.LayerId AS int) AS LayerId, CAST(x.Db AS varchar(20)) AS Db, x.StoreId,
       CAST(LEFT(x.Name, 14) AS nvarchar(14)) AS NamePrefix, CAST(MIN(x.Name) AS nvarchar(400)) AS ExampleName,
       CAST(x.DiscountApplyTypeId AS int) AS ApplyType, CAST(x.DiscountPlatformTypeId AS int) AS PlatformType,
       CAST(CAST(x.StartDateUtc AS date) AS datetime) AS StartDay, MAX(x.EndDateUtc) AS LastEndUtc,
       COUNT(*) AS Discounts
FROM (
    SELECT 1 AS LayerId, 'GjirafaMall' AS Db, d.StoreId,
           CAST(d.Name AS nvarchar(400)) COLLATE DATABASE_DEFAULT AS Name, d.DiscountApplyTypeId, d.DiscountPlatformTypeId,
           d.StartDateUtc, d.EndDateUtc
    FROM GjirafaMall.dbo.Discount d WITH (NOLOCK)
    WHERE d.StoreId = 2 AND (d.EndDateUtc >= '2026-07-01' OR d.EndDateUtc IS NULL)
      AND d.StartDateUtc >= '2026-06-15'
    UNION ALL
    SELECT 4, 'GjirafaEcommerce', d.StoreId,
           CAST(d.Name AS nvarchar(400)) COLLATE DATABASE_DEFAULT, d.DiscountApplyTypeId, d.DiscountPlatformTypeId,
           d.StartDateUtc, d.EndDateUtc
    FROM GjirafaEcommerce.dbo.Discount d WITH (NOLOCK)
    WHERE d.StoreId = 2 AND (d.EndDateUtc >= '2026-07-01' OR d.EndDateUtc IS NULL)
      AND d.StartDateUtc >= '2026-06-15'
) x
GROUP BY x.LayerId, x.Db, x.StoreId, LEFT(x.Name, 14), x.DiscountApplyTypeId, x.DiscountPlatformTypeId, CAST(x.StartDateUtc AS date)
HAVING COUNT(*) >= 200;
