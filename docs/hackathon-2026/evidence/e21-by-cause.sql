SET NOCOUNT ON;
-- E21 by cause: the GP forgone on lines sold under the hand-set floor of their day (the same rule, floors and scope as
-- `PricingTool.AgentTools backtest-below-floor`: booked COGS, tool scope, floor on the net price = unit cost / (1 − floor)),
-- split by why the line was there. Reads only PricingToolHackathon; about 5 s. Each line gets the first cause that fits:
--   0 campaign day; 1 its SKU was in a markdown (dead-stock) batch already live (allowed below the floor);
--   2 a GjirafaMall SKU listed on Gjirafa50, which takes Gjirafa50's price (the sync rule);
--   3 above the floor before an order-level voucher (a marketing cost, not a price);
--   4 cross-dock and sold under its booked cost (lever 1's lane);
--   5 no calculator price live for the SKU that day (no regular approval contained it: before 21 Jul, or between pushes;
--     Gjirafa50's approvals list only the prices they changed, so a kept price lands here too);
--   6 sold more than 2 % under the calculator's live price (a team deal or another discount);
--   7 at the calculator's live price but under the floor on booked cost (the engine judges on today's landed cost, PPTCV).
-- The regular approvals' live spans are the ones E20 uses (e20-test-phase.sql); markdown batches are approvals 6, 7, 13,
-- 14, 19, 22, 28, 29, 31 and 33. Supplier "funding" on GjirafaMall lines is left as it is: on 80 % of lines it exactly
-- offsets the displayed promotion discount and doesn't change the line's gross profit.
-- Data session, 31 Oct 2026: totals tie to E21 (Aug €12,476, Sep €16,936 on normal days).
-- Hand-set floor timeline per (layer, band), exactly like LeverBacktester.HandFloorsAsync
SELECT LayerId, CAST(EntityId AS int) AS BandId, TimestampUtc,
       TRY_CAST(JSON_VALUE(OldValue, '$.MarginFloorPct') AS decimal(9, 4)) AS OldF,
       TRY_CAST(JSON_VALUE(NewValue, '$.MarginFloorPct') AS decimal(9, 4)) AS NewF
INTO #edits
FROM PricingTool.AuditLog
WHERE EntityType = 'PriceBand' AND UserName <> 'Alpha' AND ISNUMERIC(EntityId) = 1 AND LayerId IN (1, 4)
  AND ISJSON(NewValue) = 1 AND JSON_VALUE(NewValue, '$.MarginFloorPct') IS NOT NULL;

-- Regular approvals' live spans (SKU-level price the calculator set) and markdown batches (dead-stock lane)
SELECT * INTO #reg FROM (VALUES (1, 2, '2026-07-21', '2026-08-05'), (1, 12, '2026-08-06', '2026-09-02'), (1, 25, '2026-09-03', '2026-10-05'),
    (1, 30, '2026-10-06', '2026-10-18'), (1, 35, '2026-10-19', '2026-12-31'),
    (4, 1, '2026-07-21', '2026-10-06'), (4, 32, '2026-10-07', '2026-10-20'), (4, 37, '2026-10-21', '2026-12-31')) v (LayerId, ApprovalId, FromDay, ToDay);
SELECT DISTINCT a.LayerId, i.Sku, CAST(a.ApprovedUtc AS date) AS FromDay
INTO #mark
FROM PricingTool.Approvals a JOIN PricingTool.ApprovalItems i ON i.ApprovalId = a.Id
WHERE a.Id IN (6, 7, 13, 14, 19, 22, 28, 29, 31, 33);
CREATE INDEX ix ON #mark (LayerId, Sku, FromDay);

SELECT s.SrLineId, s.LayerId, CAST(s.DateAdded AS date) AS Day, s.Sku, s.Qty, s.NetoPrice, s.BrutoPrice, s.Cogs, s.Vat, s.StockClass,
       ISNULL(d.TotalOrderDiscount, 0) AS OrderDisc, b.Id AS BandId, b.MarginFloorPct AS TodayFloor
INTO #l
FROM hk.SalesLines s
LEFT JOIN hk.SalesLineDiscountsKs d ON d.SrLineId = s.SrLineId
OUTER APPLY (SELECT TOP 1 pb.Id, pb.MarginFloorPct FROM PricingTool.PriceBands pb
             WHERE pb.LayerId = s.LayerId AND s.Cogs / s.Qty * s.Vat >= pb.MinPrice AND s.Cogs / s.Qty * s.Vat < pb.MaxPrice) b
WHERE s.DateAdded >= '2026-06-01' AND s.DateAdded < '2026-11-01' AND s.Qty > 0 AND s.Cogs > 0;

SELECT l.*, COALESCE(p.NewF, n.OldF, l.TodayFloor) AS FloorPct,
       CASE WHEN EXISTS (SELECT 1 FROM agent.CampaignHistory c WHERE c.LayerId = l.LayerId AND l.Day BETWEEN c.StartDate AND c.EndDate) THEN 1 ELSE 0 END AS CampaignDay
INTO #f
FROM #l l
OUTER APPLY (SELECT TOP 1 e.NewF FROM #edits e WHERE e.LayerId = l.LayerId AND e.BandId = l.BandId AND e.TimestampUtc < CAST(l.Day AS datetime) ORDER BY e.TimestampUtc DESC) p
OUTER APPLY (SELECT TOP 1 e.OldF FROM #edits e WHERE e.LayerId = l.LayerId AND e.BandId = l.BandId AND e.TimestampUtc >= CAST(l.Day AS datetime) ORDER BY e.TimestampUtc) n
WHERE l.BandId IS NOT NULL;

SELECT f.*, f.Cogs / f.Qty / (1 - f.FloorPct / 100) AS FloorNet, f.NetoPrice / f.Qty AS UnitNet,
       (f.Cogs / f.Qty / (1 - f.FloorPct / 100) - f.NetoPrice / f.Qty) * f.Qty AS Forgone,
       r.ApprovalId, ai.NewPrice AS CalcPrice, pr.IsGj50Listed,
       CASE WHEN EXISTS (SELECT 1 FROM #mark m WHERE m.LayerId = f.LayerId AND m.Sku = f.Sku AND m.FromDay <= f.Day) THEN 1 ELSE 0 END AS Markdown
INTO #b
FROM #f f
LEFT JOIN #reg r ON r.LayerId = f.LayerId AND f.Day BETWEEN r.FromDay AND r.ToDay
OUTER APPLY (SELECT TOP 1 i.NewPrice FROM PricingTool.ApprovalItems i WHERE i.ApprovalId = r.ApprovalId AND i.Sku = f.Sku) ai
OUTER APPLY (SELECT TOP 1 p.IsGj50Listed FROM agent.Products p WHERE p.LayerId = f.LayerId AND p.Sku = f.Sku) pr
WHERE f.NetoPrice / f.Qty < f.Cogs / f.Qty / (1 - f.FloorPct / 100) AND f.FloorPct < 100;

SELECT *, CASE
    WHEN CampaignDay = 1 THEN '0 campaign day'
    WHEN Markdown = 1 THEN '1 markdown / dead-stock lane'
    WHEN LayerId = 1 AND IsGj50Listed = 1 THEN '2 Mall SKU on Gj50 price'
    WHEN OrderDisc > 0 AND (NetoPrice + OrderDisc) / Qty >= FloorNet THEN '3 voucher only'
    WHEN StockClass = 'CrossDock' AND NetoPrice < Cogs THEN '4 cross-dock under cost'
    WHEN CalcPrice IS NULL THEN '5 no calculator price that day'
    WHEN (BrutoPrice + OrderDisc * Vat) / Qty < 0.98 * CalcPrice THEN '6 sold under the calculator price'
    ELSE '7 at the calculator price' END AS Cause
INTO #c FROM #b;


-- 1. Per month and cause (EUR)
SELECT CONVERT(char(7), Day, 120) AS Month, Cause, COUNT(*) AS Lines_, CAST(SUM(Forgone) AS decimal(12, 0)) AS Forgone,
       CAST(SUM(CASE WHEN LayerId = 1 THEN Forgone END) AS decimal(12, 0)) AS MallKs, CAST(SUM(CASE WHEN LayerId = 4 THEN Forgone END) AS decimal(12, 0)) AS Gj50Ks
FROM #c GROUP BY CONVERT(char(7), Day, 120), Cause ORDER BY Month, Cause;

-- 2. Cause 2, Aug-Sep: are those GjirafaMall lines under Gjirafa50's own floor for the band too, or only under Mall's? (EUR a month)
WITH c2 AS (SELECT * FROM #c WHERE Cause LIKE '2%' AND Day >= '2026-08-01' AND Day < '2026-10-01'),
g AS (
    SELECT c2.*, COALESCE(p.NewF, n.OldF, gb.MarginFloorPct) AS Gj50Floor
    FROM c2
    JOIN PricingTool.PriceBands mb ON mb.Id = c2.BandId
    JOIN PricingTool.PriceBands gb ON gb.LayerId = 4 AND gb.MinPrice = mb.MinPrice
    OUTER APPLY (SELECT TOP 1 e.NewF FROM #edits e WHERE e.LayerId = 4 AND e.BandId = gb.Id AND e.TimestampUtc < CAST(c2.Day AS datetime) ORDER BY e.TimestampUtc DESC) p
    OUTER APPLY (SELECT TOP 1 e.OldF FROM #edits e WHERE e.LayerId = 4 AND e.BandId = gb.Id AND e.TimestampUtc >= CAST(c2.Day AS datetime) ORDER BY e.TimestampUtc) n
)
SELECT CASE WHEN NetoPrice / Qty < Cogs / Qty / (1 - Gj50Floor / 100) THEN 'below Gj50''s floor too' ELSE 'only below Mall''s floor' END AS Where_,
       COUNT(*) AS Lines_, CAST(SUM(Forgone) / 2 AS decimal(12, 0)) AS ForgoneVsMallFloorPerMonth,
       CAST(SUM(CASE WHEN NetoPrice / Qty < Cogs / Qty / (1 - Gj50Floor / 100) THEN (Cogs / Qty / (1 - Gj50Floor / 100) - NetoPrice / Qty) * Qty END) / 2 AS decimal(12, 0)) AS ForgoneVsGj50FloorPerMonth,
       CAST(SUM(CASE WHEN NetoPrice < Cogs THEN Cogs - NetoPrice END) / 2 AS decimal(12, 0)) AS UnderCostPerMonth,
       CAST(AVG(FloorPct) AS decimal(5, 1)) AS MallFloor, CAST(AVG(Gj50Floor) AS decimal(5, 1)) AS Gj50Floor
FROM g GROUP BY CASE WHEN NetoPrice / Qty < Cogs / Qty / (1 - Gj50Floor / 100) THEN 'below Gj50''s floor too' ELSE 'only below Mall''s floor' END;

-- 3. Cause 5, Aug-Sep: how today's engine treats those SKUs (the latest local run per layer), and whether the SKU first sold
--    after the calculator's first approval (EUR a month). A proxy only: the engine's runs of August and September weren't kept.
WITH b5 AS (SELECT * FROM #c WHERE Cause LIKE '5%' AND Day >= '2026-08-01' AND Day < '2026-10-01'),
first_sale AS (SELECT LayerId, Sku, MIN(DateAdded) AS FirstSale FROM hk.SalesLines GROUP BY LayerId, Sku)
SELECT b5.LayerId,
       CASE WHEN fs.FirstSale >= '2026-07-21' THEN 'first sale after 21 Jul' ELSE 'sold before' END AS Age,
       CASE WHEN p.Id IS NULL THEN 'not in today''s run'
            WHEN p.IsNewProduct = 1 THEN 'new product (held)'
            WHEN p.ReasonCodes LIKE '%SYNC%' THEN 'synced'
            WHEN p.ProposedPriceValue > p.CurrentPrice THEN 'today: raise'
            WHEN p.ProposedPriceValue < p.CurrentPrice THEN 'today: cut'
            ELSE 'today: keep' END AS Today,
       COUNT(*) AS Lines_, CAST(SUM(b5.Forgone) / 2 AS decimal(12, 0)) AS ForgonePerMonth,
       CAST(AVG(b5.FloorPct) AS decimal(5,1)) AS AvgFloor,
       CAST(SUM(b5.NetoPrice - b5.Cogs) / NULLIF(SUM(b5.NetoPrice), 0) * 100 AS decimal(6, 1)) AS BookedMarginPct,
       CAST(SUM(CASE WHEN p.Pptcv > 0 THEN (p.CurrentPrice - p.Pptcv) / p.CurrentPrice END) / NULLIF(SUM(CASE WHEN p.Pptcv > 0 THEN 1 END), 0) * 100 AS decimal(6, 1)) AS TodayMarginOnPptcvPct
FROM b5
LEFT JOIN first_sale fs ON fs.LayerId = b5.LayerId AND fs.Sku = b5.Sku
OUTER APPLY (SELECT TOP 1 * FROM PricingTool.ProposedPrices pp WHERE pp.PricingRunId = (SELECT MAX(r.Id) FROM PricingTool.PricingRuns r WHERE r.LayerId = b5.LayerId AND EXISTS (SELECT 1 FROM PricingTool.ProposedPrices x WHERE x.PricingRunId = r.Id)) AND pp.Sku = b5.Sku) p
GROUP BY b5.LayerId, CASE WHEN fs.FirstSale >= '2026-07-21' THEN 'first sale after 21 Jul' ELSE 'sold before' END,
         CASE WHEN p.Id IS NULL THEN 'not in today''s run' WHEN p.IsNewProduct = 1 THEN 'new product (held)' WHEN p.ReasonCodes LIKE '%SYNC%' THEN 'synced'
              WHEN p.ProposedPriceValue > p.CurrentPrice THEN 'today: raise' WHEN p.ProposedPriceValue < p.CurrentPrice THEN 'today: cut' ELSE 'today: keep' END
ORDER BY b5.LayerId, 2, 3;
