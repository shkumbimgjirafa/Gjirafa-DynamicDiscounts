SET NOCOUNT ON;
-- E28: the test, run by hand: the facts beside the without-the-build line (Prompt 15). Reads only PricingToolHackathon:
-- PricingTool.Approvals / ApprovalItems (the deployed tool's runs, copied from its database), hk.PushDatesMkAl and
-- hk.PushDatesKs (sets of discounts pushed to the shops, exported read-only: exports/push_dates_mkal.sql, push_dates_ks.sql).
-- Under a second.
-- Kinds as in e21-by-cause.sql: dead-stock (markdown) sets are approvals 6, 7, 13, 14, 19, 22, 28, 29, 31 and 33; 4 and 5 are
-- floor sets; the rest are regular price runs. A full push = 50,000+ public (all-platform) discounts started in one month.
-- Result sets: 1 every Kosovo run from 21 Jul; 2 the days from each regular run to the next one on its shop;
-- 3 the full pushes per MK/AL layer and month; 4 Kosovo sets of 50,000+ public discounts and the days each stayed live
-- (the sets are grouped by the first 14 characters of their names, so one push can show as several sets).

SELECT a.Id AS ApprovalId, a.LayerId, CASE a.LayerId WHEN 1 THEN 'GjirafaMall KS' ELSE 'Gjirafa50 KS' END AS Shop,
       CAST(a.ApprovedUtc AS date) AS RunDay,
       CASE WHEN a.Id IN (6, 7, 13, 14, 19, 22, 28, 29, 31, 33) THEN 'dead stock' WHEN a.Id IN (4, 5) THEN 'floor set' ELSE 'regular' END AS Kind,
       (SELECT COUNT(*) FROM PricingTool.ApprovalItems i WHERE i.ApprovalId = a.Id) AS Items
INTO #runs
FROM PricingTool.Approvals a
WHERE a.LayerId IN (1, 4) AND a.ApprovedUtc >= '2026-07-21' AND a.ApprovedUtc < '2026-11-01';

-- 1. Every Kosovo run
SELECT ApprovalId, Shop, RunDay, Kind, Items FROM #runs ORDER BY LayerId, RunDay, ApprovalId;

-- 2. Regular runs and the days to the next one on the same shop
SELECT Shop, RunDay, LEAD(RunDay) OVER (PARTITION BY LayerId ORDER BY RunDay) AS NextRegularRun,
       DATEDIFF(day, RunDay, LEAD(RunDay) OVER (PARTITION BY LayerId ORDER BY RunDay)) AS DaysToNext
FROM #runs WHERE Kind = 'regular'
ORDER BY LayerId, RunDay;

-- 3. Full pushes on MK/AL, per layer and month
SELECT CASE LayerId WHEN 2 THEN 'GjirafaMall MK' WHEN 3 THEN 'GjirafaMall AL' WHEN 5 THEN 'Gjirafa50 MK' WHEN 6 THEN 'Gjirafa50 AL' END AS Layer,
       CONVERT(char(7), StartDay, 120) AS Month, MIN(StartDay) AS FirstDay, SUM(Discounts) AS Discounts
FROM hk.PushDatesMkAl
WHERE PlatformType = 0
GROUP BY LayerId, CONVERT(char(7), StartDay, 120)
HAVING SUM(Discounts) >= 50000
ORDER BY LayerId, Month;

-- 4. Kosovo sets of 50,000+ public discounts and how long each stayed live
SELECT CASE LayerId WHEN 1 THEN 'GjirafaMall KS' ELSE 'Gjirafa50 KS' END AS Shop, NamePrefix, StartDay,
       CAST(LastEndUtc AS date) AS LastDay, DATEDIFF(day, StartDay, CAST(LastEndUtc AS date)) + 1 AS LiveDays, Discounts
FROM hk.PushDatesKs
WHERE PlatformType = 0 AND Discounts >= 50000
ORDER BY LayerId, StartDay;
