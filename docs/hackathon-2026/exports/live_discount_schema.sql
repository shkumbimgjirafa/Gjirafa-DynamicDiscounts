SET NOCOUNT ON;
-- Metadata only: columns of the discount tables and the Product discount flags, plus the discount
-- tables' indexes, in both shop DBs. The sandbox DDL and the schema contract test use this snapshot.
SELECT CAST('GjirafaMall' AS varchar(20)) AS Db, CAST('column' AS varchar(10)) AS Kind,
       CAST(t.name AS nvarchar(200)) COLLATE DATABASE_DEFAULT AS TableName, c.column_id AS Ordinal,
       CAST(c.name AS nvarchar(200)) COLLATE DATABASE_DEFAULT AS Name, CAST(ty.name AS nvarchar(100)) COLLATE DATABASE_DEFAULT AS Type,
       CAST(c.max_length AS int) AS MaxLength, CAST(c.precision AS int) AS [Precision], CAST(c.scale AS int) AS Scale,
       c.is_nullable AS IsNullable, c.is_identity AS IsIdentity, CAST(NULL AS nvarchar(800)) AS IndexColumns
FROM GjirafaMall.sys.tables t JOIN GjirafaMall.sys.columns c ON c.object_id = t.object_id
JOIN GjirafaMall.sys.types ty ON ty.user_type_id = c.user_type_id
WHERE t.name IN ('Discount', 'Discount_AppliedToProducts') OR (t.name = 'Product' AND (c.name IN ('Id', 'Sku', 'Deleted') OR c.name LIKE 'HasDiscounts%'))
UNION ALL
SELECT 'GjirafaEcommerce', 'column', CAST(t.name AS nvarchar(200)) COLLATE DATABASE_DEFAULT, c.column_id,
       CAST(c.name AS nvarchar(200)) COLLATE DATABASE_DEFAULT, CAST(ty.name AS nvarchar(100)) COLLATE DATABASE_DEFAULT,
       c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, NULL
FROM GjirafaEcommerce.sys.tables t JOIN GjirafaEcommerce.sys.columns c ON c.object_id = t.object_id
JOIN GjirafaEcommerce.sys.types ty ON ty.user_type_id = c.user_type_id
WHERE t.name IN ('Discount', 'Discount_AppliedToProducts') OR (t.name = 'Product' AND (c.name IN ('Id', 'Sku', 'Deleted') OR c.name LIKE 'HasDiscounts%'))
UNION ALL
SELECT 'GjirafaMall', 'index', CAST(o.name AS nvarchar(200)) COLLATE DATABASE_DEFAULT, i.index_id,
       CAST(i.name AS nvarchar(200)) COLLATE DATABASE_DEFAULT, CAST(i.type_desc AS nvarchar(100)) COLLATE DATABASE_DEFAULT,
       NULL, NULL, NULL, i.is_unique, i.is_primary_key,
       CAST((SELECT STRING_AGG(CAST(c.name AS nvarchar(200)) + CASE WHEN ic.is_included_column = 1 THEN ' (incl)' ELSE '' END, ',')
                    WITHIN GROUP (ORDER BY ic.is_included_column, ic.key_ordinal, ic.index_column_id)
             FROM GjirafaMall.sys.index_columns ic JOIN GjirafaMall.sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
             WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id) AS nvarchar(800)) COLLATE DATABASE_DEFAULT
FROM GjirafaMall.sys.indexes i JOIN GjirafaMall.sys.objects o ON o.object_id = i.object_id
WHERE o.name IN ('Discount', 'Discount_AppliedToProducts') AND o.type = 'U' AND i.index_id > 0
UNION ALL
SELECT 'GjirafaEcommerce', 'index', CAST(o.name AS nvarchar(200)) COLLATE DATABASE_DEFAULT, i.index_id,
       CAST(i.name AS nvarchar(200)) COLLATE DATABASE_DEFAULT, CAST(i.type_desc AS nvarchar(100)) COLLATE DATABASE_DEFAULT,
       NULL, NULL, NULL, i.is_unique, i.is_primary_key,
       CAST((SELECT STRING_AGG(CAST(c.name AS nvarchar(200)) + CASE WHEN ic.is_included_column = 1 THEN ' (incl)' ELSE '' END, ',')
                    WITHIN GROUP (ORDER BY ic.is_included_column, ic.key_ordinal, ic.index_column_id)
             FROM GjirafaEcommerce.sys.index_columns ic JOIN GjirafaEcommerce.sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
             WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id) AS nvarchar(800)) COLLATE DATABASE_DEFAULT
FROM GjirafaEcommerce.sys.indexes i JOIN GjirafaEcommerce.sys.objects o ON o.object_id = i.object_id
WHERE o.name IN ('Discount', 'Discount_AppliedToProducts') AND o.type = 'U' AND i.index_id > 0;
