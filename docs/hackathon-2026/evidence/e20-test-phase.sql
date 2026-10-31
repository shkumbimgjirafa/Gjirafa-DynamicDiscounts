-- E20: the calculator's (v0.5) test phase in Kosovo, extra gross profit a month (B0, and the rate that
-- lever 4 borrows). Recomputed from PricingToolHackathon only: PricingTool.Approvals / ApprovalItems (copied from the
-- deployed tool DB) and hk.SalesLines (booked sales lines, tool scope, booked COGS, EUR excl. VAT).
--
-- Two result sets:
--   1. Before/after (E20 mid): each approval's daily lift = (GP of its SKUs in the 30 days after it went live − GP in the
--      30 days before) / 30, spread over the calendar days it was live in Aug and Sep 2026 and summed per month.
--      Each SKU is counted once: where a later approval contains an earlier one's SKUs, the earlier one stops when the
--      later one goes live (overlaps measured with the ApprovalItems self-join on 27 Oct, listed in the spans below).
--      Not controlled: the market moved too. The 2025-season adjustment (E20 high) is not recomputed here.
--   2. Same-weeks control (E20 low): for each regular-price approval, products whose price went up or down vs products
--      in the same approval whose price was kept, 30 days before vs 30 days after. Effect = after − before × the kept
--      group's own after/before ratio. Only the September run (#25) gives a clean result; in August's (#12) the units of
--      raised products rose after the rise, and most of its after-window profit came from products with no sale before.
--
-- Measured first on 27 Oct 2026 in the main repo (Aug €11,184, Sep €10,079; #25 controlled ≈ €3.5–4.3k a month).
-- Numbers move a little as late cancellations post (hk.SalesLines re-pulls the last 14 days daily).

-- 1. Before/after, SKU-deduplicated spans -------------------------------------------------------------------------
WITH spans AS (
    SELECT * FROM (VALUES
        -- approval, first live day, last live day, share of its SKUs not counted by another span
        (2,  CAST('2026-07-21' AS date), CAST('2026-08-05' AS date), 1.0),   -- Mall regular; 82% of it is inside #12
        (4,  '2026-07-22', '2026-08-05', 1.0),                               -- Mall floor set; 88% inside #12
        (5,  '2026-07-23', '2026-08-05', 1.0),                               -- Mall floor set; 82% inside #12
        (7,  '2026-08-03', '2026-08-05', 1.0),                               -- Mall dead stock; 98% inside #12
        (12, '2026-08-06', '2026-09-02', 1.0),                               -- Mall full catalogue; #14 and #19 sit inside it
        (25, '2026-09-03', '2026-10-05', 1.0),                               -- Mall full catalogue; #28/#29 inside it
        (19, '2026-09-03', '2026-10-04', 1.0 - 186.0 / 2750),                -- Mall dead stock; 186 SKUs shared with #25
        (1,  '2026-07-21', '2026-10-06', 1.0),                               -- Gj50 regular; dead-stock sets share <= 46 SKUs
        (6,  '2026-08-03', '2026-08-18', 1.0),                               -- Gj50 dead stock, superseded by #13
        (13, '2026-08-19', '2026-09-02', 1.0),                               -- Gj50 dead stock, superseded by #22
        (22, '2026-09-03', '2026-10-06', 1.0)                                -- Gj50 dead stock
    ) v (ApprovalId, FirstDay, LastDay, KeepShare)
),
skus AS (
    SELECT DISTINCT i.ApprovalId, i.Sku
    FROM PricingTool.ApprovalItems i
    WHERE i.ApprovalId IN (SELECT ApprovalId FROM spans)
),
gp AS (
    SELECT a.Id AS ApprovalId, a.LayerId,
           SUM(CASE WHEN s.DateAdded <  a.ApprovedUtc THEN s.Margin ELSE 0 END) AS Gp30Before,
           SUM(CASE WHEN s.DateAdded >= a.ApprovedUtc THEN s.Margin ELSE 0 END) AS Gp30After
    FROM PricingTool.Approvals a
    JOIN skus k ON k.ApprovalId = a.Id
    JOIN hk.SalesLines s ON s.LayerId = a.LayerId AND s.Sku = k.Sku
                        AND s.DateAdded >= DATEADD(day, -30, a.ApprovedUtc) AND s.DateAdded < DATEADD(day, 30, a.ApprovedUtc)
    GROUP BY a.Id, a.LayerId
),
months AS (
    SELECT * FROM (VALUES ('2026-08', CAST('2026-08-01' AS date), CAST('2026-08-31' AS date)),
                          ('2026-09', CAST('2026-09-01' AS date), CAST('2026-09-30' AS date))) m (Month, FirstDay, LastDay)
),
per_span AS (
    SELECT m.Month, g.LayerId, sp.ApprovalId,
           (g.Gp30After - g.Gp30Before) / 30.0 * sp.KeepShare AS LiftPerDay,
           DATEDIFF(day, CASE WHEN sp.FirstDay > m.FirstDay THEN sp.FirstDay ELSE m.FirstDay END,
                         CASE WHEN sp.LastDay  < m.LastDay  THEN sp.LastDay  ELSE m.LastDay  END) + 1 AS DaysLive
    FROM spans sp
    JOIN gp g ON g.ApprovalId = sp.ApprovalId
    JOIN months m ON sp.FirstDay <= m.LastDay AND sp.LastDay >= m.FirstDay
),
by_month AS (
    SELECT Month,
           CAST(SUM(CASE WHEN LayerId = 1 THEN LiftPerDay * DaysLive END) AS decimal(12, 0)) AS MallKs,
           CAST(SUM(CASE WHEN LayerId = 4 THEN LiftPerDay * DaysLive END) AS decimal(12, 0)) AS Gj50Ks,
           CAST(SUM(LiftPerDay * DaysLive) AS decimal(12, 0)) AS Total,
           STRING_AGG(CONCAT('#', ApprovalId, ' ', DaysLive, 'd x ', CAST(LiftPerDay AS decimal(10, 0)), '/d'), '; ')
               WITHIN GROUP (ORDER BY LayerId, ApprovalId) AS Spans
    FROM per_span
    GROUP BY Month
)
SELECT Month, MallKs, Gj50Ks, Total, Spans FROM by_month
UNION ALL
SELECT 'Aug-Sep average', CAST(AVG(MallKs) AS decimal(12, 0)), CAST(AVG(Gj50Ks) AS decimal(12, 0)), CAST(AVG(Total) AS decimal(12, 0)), NULL
FROM by_month
ORDER BY Month;

-- 2. Same-weeks control: changed vs unchanged products in the same weeks ------------------------------------------
-- Two controls, both from the same layer and the same 60 days: products the approval kept at their price ('Kept'), and
-- products that sold but weren't in the approval at all ('NotInApproval'). #1 and #2 changed every product they held, so
-- only the second control exists for them.
WITH events AS (
    SELECT * FROM (VALUES (1, CAST('2026-07-21' AS date)), (2, CAST('2026-07-21' AS date)),
                          (12, CAST('2026-08-06' AS date)), (25, CAST('2026-09-03' AS date))) e (ApprovalId, D0)
),
items AS (
    SELECT i.ApprovalId, i.Sku,
           MAX(CASE WHEN i.NewPrice > i.OldPrice THEN 'Up' WHEN i.NewPrice < i.OldPrice THEN 'Down' ELSE 'Kept' END) AS Grp
    FROM PricingTool.ApprovalItems i
    WHERE i.ApprovalId IN (SELECT ApprovalId FROM events)
    GROUP BY i.ApprovalId, i.Sku
),
g AS (
    SELECT e.ApprovalId, COALESCE(CAST(it.Grp AS varchar(20)), 'NotInApproval') AS Grp,
           SUM(CASE WHEN s.DateAdded <  e.D0 THEN s.Margin ELSE 0 END) AS GpBefore,
           SUM(CASE WHEN s.DateAdded >= e.D0 THEN s.Margin ELSE 0 END) AS GpAfter,
           SUM(CASE WHEN s.DateAdded <  e.D0 THEN s.Qty ELSE 0 END) AS UnitsBefore,
           SUM(CASE WHEN s.DateAdded >= e.D0 THEN s.Qty ELSE 0 END) AS UnitsAfter,
           SUM(CASE WHEN s.DateAdded <  e.D0 THEN s.NetoPrice ELSE 0 END) AS NetBefore,
           SUM(CASE WHEN s.DateAdded >= e.D0 THEN s.NetoPrice ELSE 0 END) AS NetAfter
    FROM events e
    JOIN PricingTool.Approvals a ON a.Id = e.ApprovalId
    JOIN hk.SalesLines s ON s.LayerId = a.LayerId
                        AND s.DateAdded >= DATEADD(day, -30, e.D0) AND s.DateAdded < DATEADD(day, 30, e.D0)
    LEFT JOIN items it ON it.ApprovalId = e.ApprovalId AND it.Sku = s.Sku
    GROUP BY e.ApprovalId, COALESCE(CAST(it.Grp AS varchar(20)), 'NotInApproval')
),
ratio AS (
    SELECT ApprovalId, Grp, GpAfter / NULLIF(GpBefore, 0) AS GpRatio FROM g WHERE Grp IN ('Kept', 'NotInApproval')
),
groups AS (
    SELECT g.ApprovalId, g.Grp,
           CAST(g.GpBefore AS decimal(12, 0)) AS GpBefore, CAST(g.GpAfter AS decimal(12, 0)) AS GpAfter,
           CAST(100.0 * g.GpBefore / NULLIF(g.NetBefore, 0) AS decimal(5, 1)) AS MarginBeforePct,
           CAST(100.0 * g.GpAfter / NULLIF(g.NetAfter, 0) AS decimal(5, 1)) AS MarginAfterPct,
           CAST(100.0 * (g.UnitsAfter / NULLIF(g.UnitsBefore, 0) - 1) AS decimal(6, 1)) AS UnitsChangePct,
           CAST(g.GpAfter / NULLIF(g.GpBefore, 0) AS decimal(6, 3)) AS GpRatio,
           -- effect a month of the changed products against each control: after − before × the control's ratio
           CASE WHEN g.Grp IN ('Up', 'Down') THEN CAST((g.GpAfter - g.GpBefore * k.GpRatio) / 30.0 * 30.4 AS decimal(12, 0)) END AS VsKeptPerMonth,
           CASE WHEN g.Grp IN ('Up', 'Down') THEN CAST((g.GpAfter - g.GpBefore * n.GpRatio) / 30.0 * 30.4 AS decimal(12, 0)) END AS VsNotInApprovalPerMonth
    FROM g
    LEFT JOIN ratio k ON k.ApprovalId = g.ApprovalId AND k.Grp = 'Kept'
    LEFT JOIN ratio n ON n.ApprovalId = g.ApprovalId AND n.Grp = 'NotInApproval'
)
SELECT ApprovalId, Grp, GpBefore, GpAfter, MarginBeforePct, MarginAfterPct, UnitsChangePct, GpRatio,
       VsKeptPerMonth, VsNotInApprovalPerMonth
FROM groups
UNION ALL
SELECT ApprovalId, 'Changed total', NULL, NULL, NULL, NULL, NULL, NULL, SUM(VsKeptPerMonth), SUM(VsNotInApprovalPerMonth)
FROM groups WHERE Grp IN ('Up', 'Down')
GROUP BY ApprovalId
ORDER BY ApprovalId, Grp;
