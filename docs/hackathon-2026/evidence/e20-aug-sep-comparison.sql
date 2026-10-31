SET NOCOUNT ON;
-- E20 against a comparison group: the calculator's Aug-Sep 2026 test, per shop and month, three ways. Reads only
-- PricingToolHackathon (PricingTool.Approvals / ApprovalItems, hk.SalesLines, hk.Products, hk.CampaignWindows); ~15 s.
--
-- For each approval in E20's spans (same approvals, live days and shares as e20-test-phase.sql), 30 days before vs
-- 30 days after it went live, campaign days left out of both windows:
--   BeforeAfter  = GP change of the SKUs it repriced (E20's method, on repriced SKUs only).
--   VsUntouched  = per repriced SKU, its GP change minus the average GP change of the SKUs it did NOT reprice in the
--                  same shop and category (CategoryL2), same weeks; summed. Every SKU that sold in either window.
--   VsUntouchedBoth = the same on SKUs that sold in BOTH windows: a product with no sale in one window, for whatever
--                  reason, drops out of every group alike.
--   VsUntouchedHistory = the same on SKUs with a sale before the 30-day "before" window (hk.SalesLines starts Oct 2024):
--                  a SKU whose first-ever sale falls inside the windows drops out of every group alike.
-- Each approval's lift a day is spread over the days it was live in Aug and Sep, as in E20.
-- Result sets: 1 per shop and month; 2 per approval; 3 the gain split into units, price and margin per unit (repriced
-- vs untouched, the two full-catalogue Mall runs and Gjirafa50's July run).
-- GjirafaMall lines are Gjirafa's own vendors only (exports/sales_lines_ks.sql), so no marketplace commission applies;
-- Cogs is the booked cost at the sale. Returns after delivery are not netted out (cancelled orders are excluded).

IF OBJECT_ID('tempdb..#spans') IS NOT NULL DROP TABLE #spans;
SELECT * INTO #spans FROM (VALUES
    (2,  CAST('2026-07-21' AS date), CAST('2026-08-05' AS date), 1.0),
    (4,  '2026-07-22', '2026-08-05', 1.0), (5,  '2026-07-23', '2026-08-05', 1.0), (7,  '2026-08-03', '2026-08-05', 1.0),
    (12, '2026-08-06', '2026-09-02', 1.0), (25, '2026-09-03', '2026-10-05', 1.0), (19, '2026-09-03', '2026-10-04', 1.0 - 186.0 / 2750),
    (1,  '2026-07-21', '2026-10-06', 1.0), (6,  '2026-08-03', '2026-08-18', 1.0), (13, '2026-08-19', '2026-09-02', 1.0),
    (22, '2026-09-03', '2026-10-06', 1.0)) v (ApprovalId, FirstDay, LastDay, KeepShare);

IF OBJECT_ID('tempdb..#ch') IS NOT NULL DROP TABLE #ch;
SELECT i.ApprovalId, i.Sku INTO #ch
FROM PricingTool.ApprovalItems i WHERE i.ApprovalId IN (SELECT ApprovalId FROM #spans)
GROUP BY i.ApprovalId, i.Sku HAVING MAX(ABS(i.NewPrice - i.OldPrice)) > 0.005;

IF OBJECT_ID('tempdb..#first') IS NOT NULL DROP TABLE #first;
SELECT LayerId, Sku, MIN(DateAdded) AS FirstSale INTO #first FROM hk.SalesLines GROUP BY LayerId, Sku;
CREATE CLUSTERED INDEX ix ON #first (LayerId, Sku);

IF OBJECT_ID('tempdb..#s') IS NOT NULL DROP TABLE #s;
SELECT a.Id AS ApprovalId, a.LayerId, s.Sku, ISNULL(p.CategoryL2, '(none)') AS Cat,
       CASE WHEN c.Sku IS NULL THEN 0 ELSE 1 END AS Repriced,
       SUM(CASE WHEN s.DateAdded <  a.ApprovedUtc THEN s.Margin ELSE 0 END) AS GpB,
       SUM(CASE WHEN s.DateAdded >= a.ApprovedUtc THEN s.Margin ELSE 0 END) AS GpA,
       SUM(CASE WHEN s.DateAdded <  a.ApprovedUtc THEN s.Qty ELSE 0 END) AS QB,
       SUM(CASE WHEN s.DateAdded >= a.ApprovedUtc THEN s.Qty ELSE 0 END) AS QA,
       SUM(CASE WHEN s.DateAdded <  a.ApprovedUtc THEN s.NetoPrice ELSE 0 END) AS RevB,
       SUM(CASE WHEN s.DateAdded >= a.ApprovedUtc THEN s.NetoPrice ELSE 0 END) AS RevA,
       MAX(CASE WHEN f.FirstSale < DATEADD(day, -30, a.ApprovedUtc) THEN 1 ELSE 0 END) AS SoldEarlier
INTO #s
FROM PricingTool.Approvals a
JOIN #spans sp ON sp.ApprovalId = a.Id
JOIN hk.SalesLines s ON s.LayerId = a.LayerId AND s.DateAdded >= DATEADD(day, -30, a.ApprovedUtc) AND s.DateAdded < DATEADD(day, 30, a.ApprovedUtc)
LEFT JOIN hk.Products p ON p.LayerId = s.LayerId AND p.Sku = s.Sku
LEFT JOIN #ch c ON c.ApprovalId = a.Id AND c.Sku = s.Sku
LEFT JOIN #first f ON f.LayerId = s.LayerId AND f.Sku = s.Sku
WHERE NOT EXISTS (SELECT 1 FROM hk.CampaignWindows cw WHERE s.DateAdded >= cw.StartDate AND s.DateAdded < DATEADD(day, 1, cw.EndDate))
GROUP BY a.Id, a.LayerId, s.Sku, ISNULL(p.CategoryL2, '(none)'), CASE WHEN c.Sku IS NULL THEN 0 ELSE 1 END;

-- per approval: lift a day, four ways (30-day windows; categories with both groups only for the comparisons)
IF OBJECT_ID('tempdb..#lift') IS NOT NULL DROP TABLE #lift;
WITH cat AS (
    SELECT ApprovalId, LayerId, Cat, Sample,
           SUM(CASE WHEN Repriced = 1 THEN 1 ELSE 0 END) AS nR, SUM(CASE WHEN Repriced = 0 THEN 1 ELSE 0 END) AS nU,
           SUM(CASE WHEN Repriced = 1 THEN GpA - GpB ELSE 0 END) AS dR, SUM(CASE WHEN Repriced = 0 THEN GpA - GpB ELSE 0 END) AS dU
    FROM (SELECT *, 'either' AS Sample FROM #s UNION ALL SELECT *, 'both' FROM #s WHERE QB > 0 AND QA > 0
          UNION ALL SELECT *, 'history' FROM #s WHERE SoldEarlier = 1) x
    GROUP BY ApprovalId, LayerId, Cat, Sample
)
SELECT ApprovalId, LayerId,
       SUM(CASE WHEN Sample = 'either' THEN dR ELSE 0 END) / 30.0 AS BeforeAfterDay,
       SUM(CASE WHEN Sample = 'either' AND nU > 0 THEN dR - nR * dU / nU ELSE 0 END) / 30.0 AS VsUntouchedDay,
       SUM(CASE WHEN Sample = 'both' AND nU > 0 THEN dR - nR * dU / nU ELSE 0 END) / 30.0 AS VsUntouchedBothDay,
       SUM(CASE WHEN Sample = 'history' AND nU > 0 THEN dR - nR * dU / nU ELSE 0 END) / 30.0 AS VsUntouchedHistoryDay,
       SUM(CASE WHEN Sample = 'either' THEN nR ELSE 0 END) AS RepricedSkus,
       SUM(CASE WHEN Sample = 'both' THEN nR ELSE 0 END) AS RepricedSkusBoth,
       SUM(CASE WHEN Sample = 'history' THEN nR ELSE 0 END) AS RepricedSkusHistory
INTO #lift FROM cat GROUP BY ApprovalId, LayerId;

-- 1. Per shop and month
WITH months AS (SELECT * FROM (VALUES ('2026-08', CAST('2026-08-01' AS date), CAST('2026-08-31' AS date)),
                                      ('2026-09', CAST('2026-09-01' AS date), CAST('2026-09-30' AS date))) m (Month, FirstDay, LastDay)),
per AS (
    SELECT m.Month, l.LayerId, l.ApprovalId, sp.KeepShare,
           DATEDIFF(day, CASE WHEN sp.FirstDay > m.FirstDay THEN sp.FirstDay ELSE m.FirstDay END,
                         CASE WHEN sp.LastDay  < m.LastDay  THEN sp.LastDay  ELSE m.LastDay  END) + 1 AS DaysLive,
           l.BeforeAfterDay, l.VsUntouchedDay, l.VsUntouchedBothDay, l.VsUntouchedHistoryDay
    FROM #lift l JOIN #spans sp ON sp.ApprovalId = l.ApprovalId JOIN months m ON sp.FirstDay <= m.LastDay AND sp.LastDay >= m.FirstDay
)
SELECT Month, CASE LayerId WHEN 1 THEN 'GjirafaMall KS' ELSE 'Gjirafa50 KS' END AS Shop,
       CAST(SUM(BeforeAfterDay * DaysLive * KeepShare) AS int) AS BeforeAfter,
       CAST(SUM(VsUntouchedDay * DaysLive * KeepShare) AS int) AS VsUntouched,
       CAST(SUM(VsUntouchedBothDay * DaysLive * KeepShare) AS int) AS VsUntouchedBoth,
       CAST(SUM(VsUntouchedHistoryDay * DaysLive * KeepShare) AS int) AS VsUntouchedHistory
FROM per GROUP BY Month, LayerId
UNION ALL
SELECT 'Aug-Sep avg', CASE LayerId WHEN 1 THEN 'GjirafaMall KS' ELSE 'Gjirafa50 KS' END,
       CAST(SUM(BeforeAfterDay * DaysLive * KeepShare) / 2 AS int), CAST(SUM(VsUntouchedDay * DaysLive * KeepShare) / 2 AS int),
       CAST(SUM(VsUntouchedBothDay * DaysLive * KeepShare) / 2 AS int), CAST(SUM(VsUntouchedHistoryDay * DaysLive * KeepShare) / 2 AS int)
FROM per GROUP BY LayerId
ORDER BY Month, Shop;

-- 2. Per approval (a month = 30.4 days)
SELECT l.ApprovalId, CASE l.LayerId WHEN 1 THEN 'GjirafaMall KS' ELSE 'Gjirafa50 KS' END AS Shop, sp.FirstDay, sp.LastDay,
       l.RepricedSkus, l.RepricedSkusBoth, l.RepricedSkusHistory,
       CAST(l.BeforeAfterDay * 30.4 AS int) AS BeforeAfterPerMonth, CAST(l.VsUntouchedDay * 30.4 AS int) AS VsUntouchedPerMonth,
       CAST(l.VsUntouchedBothDay * 30.4 AS int) AS VsUntouchedBothPerMonth,
       CAST(l.VsUntouchedHistoryDay * 30.4 AS int) AS VsUntouchedHistoryPerMonth
FROM #lift l JOIN #spans sp ON sp.ApprovalId = l.ApprovalId ORDER BY l.LayerId, sp.FirstDay;

-- 3. Where the gain came from: units, net price and margin per unit, before vs after (SKUs that sold in both windows)
SELECT ApprovalId, CASE Repriced WHEN 1 THEN 'repriced' ELSE 'untouched' END AS Grp, COUNT(*) AS Skus,
       CAST(SUM(QB) AS int) AS UnitsBefore, CAST(SUM(QA) AS int) AS UnitsAfter,
       CAST(SUM(RevB) / NULLIF(SUM(QB), 0) AS decimal(9, 2)) AS NetPriceBefore, CAST(SUM(RevA) / NULLIF(SUM(QA), 0) AS decimal(9, 2)) AS NetPriceAfter,
       CAST(SUM(GpB) / NULLIF(SUM(QB), 0) AS decimal(9, 2)) AS GpPerUnitBefore, CAST(SUM(GpA) / NULLIF(SUM(QA), 0) AS decimal(9, 2)) AS GpPerUnitAfter,
       CAST(100 * SUM(GpB) / NULLIF(SUM(RevB), 0) AS decimal(5, 1)) AS MarginPctBefore, CAST(100 * SUM(GpA) / NULLIF(SUM(RevA), 0) AS decimal(5, 1)) AS MarginPctAfter
FROM #s WHERE ApprovalId IN (12, 25, 1) AND QB > 0 AND QA > 0
GROUP BY ApprovalId, Repriced ORDER BY ApprovalId, Repriced DESC;
