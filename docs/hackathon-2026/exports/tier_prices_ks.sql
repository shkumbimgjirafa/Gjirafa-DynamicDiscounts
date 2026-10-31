SET NOCOUNT ON;
-- Today's regular (shelf) prices in Kosovo: the newest TierPrice row per product for StoreId 2 in both shop DBs. Seeds
-- the regular-price publisher's sandbox (shop_sandbox_*.TierPrice), so a round-trip starts from the live shop's state.
-- Prices only; no customer data. One row per product and store is the norm (hk.TierPriceConventions).
SELECT CAST('GjirafaMall' AS varchar(20)) AS Db, x.Id AS TierPriceId, x.ProductId, x.StoreId, x.CustomerRoleId, x.Quantity,
       x.Price, x.OldPrice, x.ProfitMargin
FROM (
    SELECT tp.Id, tp.ProductId, tp.StoreId, tp.CustomerRoleId, tp.Quantity,
           CAST(tp.Price AS decimal(18, 4)) AS Price, CAST(tp.OldPrice AS decimal(18, 4)) AS OldPrice,
           CAST(tp.ProfitMargin AS decimal(6, 4)) AS ProfitMargin,
           ROW_NUMBER() OVER (PARTITION BY tp.ProductId ORDER BY tp.Id DESC) AS Rn
    FROM GjirafaMall.dbo.TierPrice tp WITH (NOLOCK)
    WHERE tp.StoreId = 2
) x
WHERE x.Rn = 1
UNION ALL
SELECT 'GjirafaEcommerce', x.Id, x.ProductId, x.StoreId, x.CustomerRoleId, x.Quantity, x.Price, x.OldPrice, x.ProfitMargin
FROM (
    SELECT tp.Id, tp.ProductId, tp.StoreId, tp.CustomerRoleId, tp.Quantity,
           CAST(tp.Price AS decimal(18, 4)) AS Price, CAST(tp.OldPrice AS decimal(18, 4)) AS OldPrice,
           CAST(tp.ProfitMargin AS decimal(6, 4)) AS ProfitMargin,
           ROW_NUMBER() OVER (PARTITION BY tp.ProductId ORDER BY tp.Id DESC) AS Rn
    FROM GjirafaEcommerce.dbo.TierPrice tp WITH (NOLOCK)
    WHERE tp.StoreId = 2
) x
WHERE x.Rn = 1;
