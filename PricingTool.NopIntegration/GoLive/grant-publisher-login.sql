-- Least-privilege login for the pricing tool's discount publisher (plan section 5, "Least-privilege login").
-- NOT RUN in the hackathon. A DBA runs it once per shop database (GjirafaMall, GjirafaEcommerce) at go-live.
--
-- The login itself is created by the DBA with a credential kept in the secret store (k8s Secret / vault),
-- never in this repo:  CREATE LOGIN [pricing_publisher] WITH <credential from the secret store>;
-- The publisher's connection string then lives only in that secret (Publisher:Shops:<Db>:ConnectionString).

-- USE [GjirafaMall];        -- then again for [GjirafaEcommerce]

CREATE USER [pricing_publisher] FOR LOGIN [pricing_publisher];

-- Create and edit discounts (the module's Add / Edit); expire by EndDateUtc.
GRANT SELECT, INSERT, UPDATE ON dbo.Discount TO [pricing_publisher];

-- Apply a discount to a product (the module's "Applied to products"); rows are only ever added.
GRANT SELECT, INSERT ON dbo.Discount_AppliedToProducts TO [pricing_publisher];

-- Find products by SKU and set their discount flags: these columns only.
GRANT SELECT (Id, Sku, Deleted, HasDiscountsApplied, HasDiscountsAppliedStoreIds) ON dbo.Product TO [pricing_publisher];
GRANT UPDATE (HasDiscountsApplied, HasDiscountsAppliedStoreIds) ON dbo.Product TO [pricing_publisher];

-- Never delete: order history and DiscountUsageHistory refer to discount ids.
DENY DELETE ON dbo.Discount TO [pricing_publisher];
DENY DELETE ON dbo.Discount_AppliedToProducts TO [pricing_publisher];
DENY DELETE ON dbo.Product TO [pricing_publisher];

-- Nothing else: no other table, no ALTER, no EXECUTE. Temp tables (#req, #w, ...) need no grant.

-- Check what it can do:
-- EXECUTE AS USER = 'pricing_publisher';
-- SELECT * FROM fn_my_permissions('dbo.Discount', 'OBJECT');
-- SELECT * FROM fn_my_permissions('dbo.Product', 'OBJECT');
-- REVERT;
