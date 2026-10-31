SET NOCOUNT ON;
-- E22: the cost watcher on its first real cost refresh (31 Oct 2026). Reads only PricingToolHackathon (the ai tables;
-- result set 2 also the sandbox's discounts and the tool's approvals), aggregates only (no price or cost of a single SKU). The replica feed reads costs only for SKUs no other rule owns
-- (dead stock, GjirafaMall prices synced from Gjirafa50 and the holdout categories are left out before the read) that
-- are near their floor; a cost that moved at least 0.5 % from the engine run's becomes an observation, which the watch
-- then judges (CostWatcher.WatchAsync). The replica's rebuild that morning spanned two polls, and the second observed
-- 168 of the first's moves again; they are counted once here (the latest per layer and SKU) and since skipped by the feed.

DECLARE @Day date = '2026-10-31';
WITH o AS (
    SELECT o.*, ROW_NUMBER() OVER (PARTITION BY o.LayerId, o.Sku ORDER BY o.Id DESC) AS rn
    FROM ai.CostObservation o
    WHERE o.Source = 'replica' AND CAST(o.ObservedUtc AS date) = @Day
)
SELECT
    (SELECT COUNT(*) FROM o) AS Observations,
    (SELECT COUNT(*) FROM o WHERE rn > 1) AS RepeatsOfTheSameMove,
    (SELECT COUNT(*) FROM o WHERE rn = 1) AS DistinctMoves,
    (SELECT COUNT(*) FROM o WHERE rn = 1 AND Outcome <> 'not-a-rise') AS Rises,
    (SELECT COUNT(*) FROM o WHERE rn = 1 AND Outcome = 'not-a-rise') AS Falls,
    (SELECT COUNT(*) FROM o WHERE rn = 1 AND Outcome = 'above-floor') AS RisesStillAboveTheFloor,
    (SELECT COUNT(*) FROM o WHERE rn = 1 AND Outcome = 'raised') AS Raised,
    (SELECT COUNT(*) FROM o WHERE rn = 1 AND Outcome = 'held-team-discount') AS HeldTeamDeal,
    (SELECT COUNT(*) FROM o WHERE rn = 1 AND Outcome IN ('held-cost-above-shelf', 'ended-our-discount')) AS HeldCostAboveShelf,
    (SELECT COUNT(*) FROM o WHERE rn = 1 AND Outcome = 'held-data-error') AS HeldDataError,
    (SELECT COUNT(*) FROM o WHERE Outcome LIKE 'held%') AS HoldObservations,
    (SELECT MIN(DATEDIFF(second, ObservedUtc, ProcessedUtc)) FROM o) AS FirstSecondsAfterRefresh,
    (SELECT MAX(DATEDIFF(second, ObservedUtc, ProcessedUtc)) FROM o) AS LastSecondsAfterRefresh,
    (SELECT COUNT(*) FROM o WHERE rn = 1 AND Outcome LIKE '%lane%' OR rn = 1 AND Outcome IN ('synced-to-gj50', 'holdout', 'campaign')) AS InAnotherLane;

-- Result set 2: what held the "cheaper team deal" holds (Prompt 10 part A), on the sandbox's discounts as they stood at
-- the observation (the October sets were live until 31 Oct 23:59 UTC). Per held SKU (its latest observation), every
-- live public discount on the product that isn't ours and is cheaper than the price the watcher wanted (the hold's
-- exception): app-only (DiscountPlatformTypeId 2) ones no longer stop a regular publish; one carrying a price a KS
-- approval gave that SKU, on that product alone, is the calculator's own earlier push; anything else is a team deal.
-- A SKU counts under the first that fits: team deal, own push, app-only only.
WITH o AS (
    SELECT o.*, ROW_NUMBER() OVER (PARTITION BY o.LayerId, o.Sku ORDER BY o.Id DESC) AS rn
    FROM ai.CostObservation o
    WHERE o.Source = 'replica' AND CAST(o.ObservedUtc AS date) = @Day AND o.Outcome = 'held-team-discount'
), held AS (
    SELECT o.LayerId, o.Sku, o.ObservedUtc,
           MIN(CASE WHEN e.Kind = 'cheaper-deal' THEN TRY_CAST(SUBSTRING(e.Detail, CHARINDEX('cheaper than ', e.Detail) + 13,
                    CHARINDEX(' and is not ours', e.Detail) - CHARINDEX('cheaper than ', e.Detail) - 13) AS decimal(18, 2))
                    ELSE TRY_CAST(SUBSTRING(e.Detail, 7, CHARINDEX(' is not below', e.Detail) - 7) AS decimal(18, 2)) END) AS Wanted
    FROM o JOIN ai.PublishException e ON e.BatchId = o.PublishBatchId AND e.Sku = o.Sku
    WHERE o.rn = 1
    GROUP BY o.LayerId, o.Sku, o.ObservedUtc
), deals AS (
    SELECT 1 AS LayerId, p.Sku, d.Id, d.DiscountAmount, d.DiscountPlatformTypeId, d.StartDateUtc, d.EndDateUtc,
           (SELECT COUNT(*) FROM shop_sandbox_GjirafaMall.Discount_AppliedToProducts x WHERE x.Discount_Id = d.Id) AS Products
    FROM shop_sandbox_GjirafaMall.Product p
    JOIN shop_sandbox_GjirafaMall.Discount_AppliedToProducts dap ON dap.Product_Id = p.Id
    JOIN shop_sandbox_GjirafaMall.Discount d ON d.Id = dap.Discount_Id
    WHERE p.Deleted = 0 AND d.StoreId = 2 AND d.DiscountApplyTypeId = 2 AND d.RequiresCouponCode = 0 AND d.UsePercentage = 0
      AND d.Name NOT LIKE 'ai-%' AND p.Sku IN (SELECT Sku FROM held WHERE LayerId = 1)
    UNION ALL
    SELECT 4, p.Sku, d.Id, d.DiscountAmount, d.DiscountPlatformTypeId, d.StartDateUtc, d.EndDateUtc,
           (SELECT COUNT(*) FROM shop_sandbox_GjirafaEcommerce.Discount_AppliedToProducts x WHERE x.Discount_Id = d.Id)
    FROM shop_sandbox_GjirafaEcommerce.Product p
    JOIN shop_sandbox_GjirafaEcommerce.Discount_AppliedToProducts dap ON dap.Product_Id = p.Id
    JOIN shop_sandbox_GjirafaEcommerce.Discount d ON d.Id = dap.Discount_Id
    WHERE p.Deleted = 0 AND d.StoreId = 2 AND d.DiscountApplyTypeId = 2 AND d.RequiresCouponCode = 0 AND d.UsePercentage = 0
      AND d.Name NOT LIKE 'ai-%' AND p.Sku IN (SELECT Sku FROM held WHERE LayerId = 4)
), cheaper AS (
    SELECT h.LayerId, h.Sku,
           CASE WHEN d.DiscountPlatformTypeId = 2 THEN 'app'
                WHEN d.Products = 1 AND EXISTS (SELECT 1 FROM PricingTool.ApprovalItems i JOIN PricingTool.Approvals a ON a.Id = i.ApprovalId
                                                WHERE a.LayerId IN (1, 4) AND i.Sku = h.Sku AND ROUND(i.NewPrice, 2) = ROUND(d.DiscountAmount, 2)) THEN 'own'
                ELSE 'team' END AS Kind
    FROM held h
    JOIN deals d ON d.LayerId = h.LayerId AND d.Sku = h.Sku AND d.DiscountAmount < h.Wanted
     AND ISNULL(d.StartDateUtc, '1900-01-01') <= h.ObservedUtc AND ISNULL(d.EndDateUtc, '2999-01-01') >= h.ObservedUtc
), per AS (
    SELECT h.LayerId, h.Sku,
           CASE WHEN SUM(CASE WHEN c.Kind = 'team' THEN 1 ELSE 0 END) > 0 THEN 'team'
                WHEN SUM(CASE WHEN c.Kind = 'own' THEN 1 ELSE 0 END) > 0 THEN 'own'
                WHEN SUM(CASE WHEN c.Kind = 'app' THEN 1 ELSE 0 END) > 0 THEN 'app'
                ELSE 'none' END AS HeldBy,
           MAX(CASE WHEN c.Kind = 'own' THEN 1 ELSE 0 END) AS AnyOwnPush,
           MAX(CASE WHEN c.Kind = 'app' THEN 1 ELSE 0 END) AS AnyAppOnly
    FROM held h LEFT JOIN cheaper c ON c.LayerId = h.LayerId AND c.Sku = h.Sku
    GROUP BY h.LayerId, h.Sku
)
SELECT COUNT(*) AS HeldSkus,
       SUM(CASE WHEN HeldBy = 'team' THEN 1 ELSE 0 END) AS HeldByTeamDeal,
       SUM(CASE WHEN HeldBy = 'own' THEN 1 ELSE 0 END) AS HeldByOwnPush,
       SUM(CASE WHEN HeldBy = 'app' THEN 1 ELSE 0 END) AS AppOnlyOnly,
       SUM(CASE WHEN HeldBy = 'none' THEN 1 ELSE 0 END) AS NoCheaperDealFound,
       SUM(AnyOwnPush) AS WithAnOwnPush,
       SUM(AnyAppOnly) AS WithAnAppOnlyDeal,
       SUM(CASE WHEN LayerId = 1 THEN 1 ELSE 0 END) AS GjirafaMall,
       SUM(CASE WHEN LayerId = 4 THEN 1 ELSE 0 END) AS Gjirafa50
FROM per;
