SET NOCOUNT ON;
-- The discount breakdown of each Kosovo sales line from 1 Jun 2026, keyed by the Sales Report line id (joins to
-- hk.SalesLines.SrLineId, which already applies the tool's scope). Splits the below-floor leak (E21) by cause:
-- supplier-funded promotions, order-level discounts (vouchers) and the product's own discount. Line totals, EUR; no order
-- or customer ids. Read-only.
SELECT d.ID AS SrLineId,
       CAST(d.ProductDiscount AS decimal(18, 4)) AS ProductDiscount,
       CAST(d.PromotionDiscount AS decimal(18, 4)) AS PromotionDiscount,
       CAST(d.TotalOrderDiscount AS decimal(18, 4)) AS TotalOrderDiscount,
       CAST(d.SupplierDiscount AS decimal(18, 4)) AS SupplierDiscount,
       CAST(d.SalesNoDiscount AS decimal(18, 4)) AS SalesNoDiscount
FROM GjirafaTranslations.dbo.SR_ProductsData d WITH (NOLOCK)
WHERE ((d.PlatformId = 1 AND d.CompanyId = 1) OR (d.PlatformId = 2 AND d.CompanyId = 0))
  AND d.DateAdded >= '2026-06-01'
  AND d.OrderStatusId IN (10, 20, 30);
