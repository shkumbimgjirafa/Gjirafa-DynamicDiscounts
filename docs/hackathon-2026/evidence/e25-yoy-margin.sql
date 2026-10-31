SET NOCOUNT ON;
-- E25: the year-over-year margin fingerprint (plan section 5, Evidence page): Kosovo's margin (booked gross profit over net
-- revenue, both KS layers, tool scope, every day) against the same month a year earlier, before and after the calculator went
-- live (21 Jul 2026). hk.SalesLines starts on 1 Oct 2024, so the comparison starts in Oct 2025. October 2026 is provisional
-- (late cancellations). Reads only PricingToolHackathon; about a second.
-- Result sets: 1 per month (both layers, and each layer's change); 2 per period, pooled.
SELECT CONVERT(char(7), DateAdded, 120) AS Month, SUM(Margin) AS Gp, SUM(NetoPrice) AS Net,
       SUM(CASE WHEN LayerId = 1 THEN Margin END) AS MallGp, SUM(CASE WHEN LayerId = 1 THEN NetoPrice END) AS MallNet,
       SUM(CASE WHEN LayerId = 4 THEN Margin END) AS Gj50Gp, SUM(CASE WHEN LayerId = 4 THEN NetoPrice END) AS Gj50Net
INTO #m
FROM hk.SalesLines
WHERE LayerId IN (1, 4) AND Qty > 0
GROUP BY CONVERT(char(7), DateAdded, 120);

SELECT CONVERT(char(7), DATEADD(year, -1, CAST(m.Month + '-01' AS date)), 120) AS YearBefore, m.*
INTO #y
FROM #m m;

-- 1. Per month
SELECT y.Month, CAST(100 * y.Gp / y.Net AS decimal(5, 1)) AS MarginPct, CAST(100 * p.Gp / p.Net AS decimal(5, 1)) AS YearBeforePct,
       CAST(100 * y.Gp / y.Net - 100 * p.Gp / p.Net AS decimal(5, 1)) AS DeltaPp,
       CAST(100 * y.MallGp / y.MallNet - 100 * p.MallGp / p.MallNet AS decimal(5, 1)) AS MallDeltaPp,
       CAST(100 * y.Gj50Gp / y.Gj50Net - 100 * p.Gj50Gp / p.Gj50Net AS decimal(5, 1)) AS Gj50DeltaPp
FROM #y y JOIN #m p ON p.Month = y.YearBefore
ORDER BY y.Month;

-- 2. Per period, pooled (the margin of the period's summed lines against the same months a year earlier)
SELECT x.Period, CAST(100 * SUM(y.Gp) / SUM(y.Net) AS decimal(5, 1)) AS MarginPct, CAST(100 * SUM(p.Gp) / SUM(p.Net) AS decimal(5, 1)) AS YearBeforePct,
       CAST(100 * SUM(y.Gp) / SUM(y.Net) - 100 * SUM(p.Gp) / SUM(p.Net) AS decimal(5, 1)) AS DeltaPp
FROM (VALUES ('Nov 2025 - Feb 2026', '2025-11', '2026-02'), ('Mar - Jun 2026, before the calculator', '2026-03', '2026-06'),
             ('Aug - Sep 2026, the calculator live', '2026-08', '2026-09'), ('Oct 2026, provisional', '2026-10', '2026-10')) x (Period, FromMonth, ToMonth)
JOIN #y y ON y.Month BETWEEN x.FromMonth AND x.ToMonth
JOIN #m p ON p.Month = y.YearBefore
GROUP BY x.Period, x.FromMonth
ORDER BY x.FromMonth;
