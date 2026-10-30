SET NOCOUNT ON;
-- The two product flags nopCommerce reads before it looks at a product's discounts, for the KS
-- layers' products (GjirafaMall: the tool's own vendors; Gjirafa50: all), not deleted.
SELECT CAST('GjirafaMall' AS varchar(20)) AS Db, p.Id AS ProductId, CAST(p.Sku AS nvarchar(400)) COLLATE DATABASE_DEFAULT AS Sku,
       p.HasDiscountsApplied, CAST(p.HasDiscountsAppliedStoreIds AS nvarchar(40)) COLLATE DATABASE_DEFAULT AS HasDiscountsAppliedStoreIds
FROM GjirafaMall.dbo.Product p WITH (NOLOCK)
JOIN GjirafaMall.dbo.Vendor v WITH (NOLOCK) ON v.Id = p.VendorId
WHERE p.Deleted = 0 AND (v.Name LIKE '%Gjiraf%' OR v.Name IN ('Dino Toys', 'Mysu', 'Apple', 'YZATT'))
UNION ALL
SELECT 'GjirafaEcommerce', p.Id, CAST(p.Sku AS nvarchar(400)) COLLATE DATABASE_DEFAULT,
       p.HasDiscountsApplied, CAST(p.HasDiscountsAppliedStoreIds AS nvarchar(40)) COLLATE DATABASE_DEFAULT
FROM GjirafaEcommerce.dbo.Product p WITH (NOLOCK)
WHERE p.Deleted = 0;
