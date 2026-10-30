SET NOCOUNT ON;
-- Discounts on the Kosovo store (StoreId 2) in both shop DBs that are live now or start within 60
-- days, all apply types (the publisher's overlap check reads the public ones, DiscountApplyTypeId 2).
-- Seeds shop_sandbox so overlap, supersede, flags and round-trip run against real state.
SELECT CAST('GjirafaMall' AS varchar(20)) AS Db, d.Id, CAST(d.Name AS nvarchar(400)) COLLATE DATABASE_DEFAULT AS Name,
       CAST(d.CouponCode AS nvarchar(100)) COLLATE DATABASE_DEFAULT AS CouponCode,
       CAST(d.AdminComment AS nvarchar(max)) COLLATE DATABASE_DEFAULT AS AdminComment,
       d.DiscountTypeId, d.UsePercentage, d.DiscountPercentage, d.DiscountAmount, d.MaximumDiscountAmount,
       d.StartDateUtc, d.EndDateUtc, d.RequiresCouponCode, d.IsCumulative, d.DiscountLimitationId, d.LimitationTimes,
       d.MaximumDiscountedQuantity, d.AppliedToSubCategories, d.StoreId, CAST(d.DiscountApplyTypeId AS int) AS DiscountApplyTypeId,
       CAST(d.DiscountPlatformTypeId AS int) AS DiscountPlatformTypeId, d.IsVendorDiscount,
       CAST(d.DisplayAvailabilityTime AS bit) AS DisplayAvailabilityTime
FROM GjirafaMall.dbo.Discount d WITH (NOLOCK)
WHERE d.StoreId = 2
  AND (d.EndDateUtc IS NULL OR d.EndDateUtc >= GETUTCDATE())
  AND (d.StartDateUtc IS NULL OR d.StartDateUtc <= DATEADD(day, 60, GETUTCDATE()))
UNION ALL
SELECT 'GjirafaEcommerce', d.Id, CAST(d.Name AS nvarchar(400)) COLLATE DATABASE_DEFAULT,
       CAST(d.CouponCode AS nvarchar(100)) COLLATE DATABASE_DEFAULT,
       CAST(d.AdminComment AS nvarchar(max)) COLLATE DATABASE_DEFAULT,
       d.DiscountTypeId, d.UsePercentage, d.DiscountPercentage, d.DiscountAmount, d.MaximumDiscountAmount,
       d.StartDateUtc, d.EndDateUtc, d.RequiresCouponCode, d.IsCumulative, d.DiscountLimitationId, d.LimitationTimes,
       d.MaximumDiscountedQuantity, d.AppliedToSubCategories, d.StoreId, CAST(d.DiscountApplyTypeId AS int),
       CAST(d.DiscountPlatformTypeId AS int), d.IsVendorDiscount, CAST(NULL AS bit)
FROM GjirafaEcommerce.dbo.Discount d WITH (NOLOCK)
WHERE d.StoreId = 2
  AND (d.EndDateUtc IS NULL OR d.EndDateUtc >= GETUTCDATE())
  AND (d.StartDateUtc IS NULL OR d.StartDateUtc <= DATEADD(day, 60, GETUTCDATE()));
