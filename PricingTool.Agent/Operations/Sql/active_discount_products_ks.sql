SET NOCOUNT ON;
-- Product mappings of the discounts in active_discounts_ks.sql (same filter).
SELECT CAST('GjirafaMall' AS varchar(20)) AS Db, m.Discount_Id, m.Product_Id
FROM GjirafaMall.dbo.Discount_AppliedToProducts m WITH (NOLOCK)
JOIN GjirafaMall.dbo.Discount d WITH (NOLOCK) ON d.Id = m.Discount_Id
WHERE d.StoreId = 2
  AND (d.EndDateUtc IS NULL OR d.EndDateUtc >= GETUTCDATE())
  AND (d.StartDateUtc IS NULL OR d.StartDateUtc <= DATEADD(day, 60, GETUTCDATE()))
UNION ALL
SELECT 'GjirafaEcommerce', m.Discount_Id, m.Product_Id
FROM GjirafaEcommerce.dbo.Discount_AppliedToProducts m WITH (NOLOCK)
JOIN GjirafaEcommerce.dbo.Discount d WITH (NOLOCK) ON d.Id = m.Discount_Id
WHERE d.StoreId = 2
  AND (d.EndDateUtc IS NULL OR d.EndDateUtc >= GETUTCDATE())
  AND (d.StartDateUtc IS NULL OR d.StartDateUtc <= DATEADD(day, 60, GETUTCDATE()));
