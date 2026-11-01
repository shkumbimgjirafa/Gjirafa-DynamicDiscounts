SET NOCOUNT ON;
-- E30: how much of Kosovo's sales sit under the hand-set margin floor, and how little profit they bring: the share of net
-- revenue and of gross profit on lines whose margin on booked COGS is under their band's floor of that day, Aug-Sep 2026
-- (the calculator's era, E21's window), both KS layers. The same floors, banding and scope as e21-by-cause.sql (floor on the
-- net price = unit cost / (1 - floor), floors rebuilt from the band edits in the audit log). Reads only PricingToolHackathon.
SELECT LayerId, CAST(EntityId AS int) AS BandId, TimestampUtc,
       TRY_CAST(JSON_VALUE(OldValue, '$.MarginFloorPct') AS decimal(9, 4)) AS OldF,
       TRY_CAST(JSON_VALUE(NewValue, '$.MarginFloorPct') AS decimal(9, 4)) AS NewF
INTO #edits
FROM PricingTool.AuditLog
WHERE EntityType = 'PriceBand' AND UserName <> 'Alpha' AND ISNUMERIC(EntityId) = 1 AND LayerId IN (1, 4)
  AND ISJSON(NewValue) = 1 AND JSON_VALUE(NewValue, '$.MarginFloorPct') IS NOT NULL;

SELECT s.LayerId, CAST(s.DateAdded AS date) AS Day, s.Qty, s.NetoPrice, s.Cogs, b.Id AS BandId, b.MarginFloorPct AS TodayFloor
INTO #l
FROM hk.SalesLines s
OUTER APPLY (SELECT TOP 1 pb.Id, pb.MarginFloorPct FROM PricingTool.PriceBands pb
             WHERE pb.LayerId = s.LayerId AND s.Cogs / s.Qty * s.Vat >= pb.MinPrice AND s.Cogs / s.Qty * s.Vat < pb.MaxPrice) b
WHERE s.DateAdded >= '2026-08-01' AND s.DateAdded < '2026-10-01' AND s.Qty > 0 AND s.Cogs > 0;

SELECT l.*, COALESCE(p.NewF, n.OldF, l.TodayFloor) AS FloorPct
INTO #f
FROM #l l
OUTER APPLY (SELECT TOP 1 e.NewF FROM #edits e WHERE e.LayerId = l.LayerId AND e.BandId = l.BandId AND e.TimestampUtc < CAST(l.Day AS datetime) ORDER BY e.TimestampUtc DESC) p
OUTER APPLY (SELECT TOP 1 e.OldF FROM #edits e WHERE e.LayerId = l.LayerId AND e.BandId = l.BandId AND e.TimestampUtc >= CAST(l.Day AS datetime) ORDER BY e.TimestampUtc) n
WHERE l.BandId IS NOT NULL;

WITH u AS (
    SELECT LayerId, NetoPrice, NetoPrice - Cogs AS Gp,
           CASE WHEN FloorPct < 100 AND NetoPrice / Qty < Cogs / Qty / (1 - FloorPct / 100) THEN 1 ELSE 0 END AS UnderFloor
    FROM #f
)
SELECT CASE WHEN GROUPING(LayerId) = 1 THEN 'Both' WHEN LayerId = 1 THEN 'GjirafaMall KS' ELSE 'Gjirafa50 KS' END AS Shop,
       CAST(SUM(NetoPrice) / 2 AS decimal(12, 0)) AS NetRevenuePerMonth,
       CAST(SUM(Gp) / 2 AS decimal(12, 0)) AS GpPerMonth,
       CAST(SUM(CASE WHEN UnderFloor = 1 THEN NetoPrice END) / 2 AS decimal(12, 0)) AS UnderFloorRevenuePerMonth,
       CAST(SUM(CASE WHEN UnderFloor = 1 THEN Gp END) / 2 AS decimal(12, 0)) AS UnderFloorGpPerMonth,
       CAST(100.0 * SUM(CASE WHEN UnderFloor = 1 THEN NetoPrice END) / SUM(NetoPrice) AS decimal(5, 1)) AS RevenueSharePct,
       CAST(100.0 * SUM(CASE WHEN UnderFloor = 1 THEN Gp END) / SUM(Gp) AS decimal(5, 1)) AS GpSharePct
FROM u
GROUP BY ROLLUP (LayerId)
ORDER BY GROUPING(LayerId), LayerId;
