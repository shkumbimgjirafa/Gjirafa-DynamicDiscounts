SET NOCOUNT ON;
-- E29: a placebo check of E27's comparison. Reads only PricingToolHackathon (PricingTool.Approvals / ApprovalItems,
-- hk.SalesLines, hk.Products, hk.CampaignWindows); ~10 s.
--
-- For the four main runs (GjirafaMall KS #2, #12, #25; Gjirafa50 KS #1), the same SKU groups as E27 (the SKUs each run
-- repriced vs the SKUs it left untouched, same shop and category) and the same 30-day windows, campaign days out, but
-- centred on dates when the calculator had made no approval yet (its first was 21 Jul 2026): 15 May and 15 Jun 2026.
-- Next to them, the real windows (centred on each approval). The sample is E27's "with a sales history": SKUs with a
-- sale before the 30-day "before" window (hk.SalesLines starts Oct 2024).
-- Result: per run and window, the repriced SKUs' gross-profit change minus the untouched SKUs' average change in the
-- same category, summed, a month (30.4 days).

IF OBJECT_ID('tempdb..#at') IS NOT NULL DROP TABLE #at;
SELECT a.Id AS ApprovalId, a.LayerId, v.Kind, CASE WHEN v.Kind = 'real' THEN a.ApprovedUtc ELSE CAST(v.At AS datetime2) END AS At
INTO #at
FROM PricingTool.Approvals a
CROSS JOIN (VALUES ('real', CAST(NULL AS date)), ('placebo 15 Jun', CAST('2026-06-15' AS date)), ('placebo 15 May', CAST('2026-05-15' AS date))) v (Kind, At)
WHERE a.Id IN (2, 12, 25, 1);

IF OBJECT_ID('tempdb..#ch') IS NOT NULL DROP TABLE #ch;
SELECT i.ApprovalId, i.Sku INTO #ch
FROM PricingTool.ApprovalItems i WHERE i.ApprovalId IN (2, 12, 25, 1)
GROUP BY i.ApprovalId, i.Sku HAVING MAX(ABS(i.NewPrice - i.OldPrice)) > 0.005;

IF OBJECT_ID('tempdb..#first') IS NOT NULL DROP TABLE #first;
SELECT LayerId, Sku, MIN(DateAdded) AS FirstSale INTO #first FROM hk.SalesLines GROUP BY LayerId, Sku;
CREATE CLUSTERED INDEX ix ON #first (LayerId, Sku);

IF OBJECT_ID('tempdb..#s') IS NOT NULL DROP TABLE #s;
SELECT t.ApprovalId, t.Kind, s.Sku, ISNULL(p.CategoryL2, '(none)') AS Cat,
       CASE WHEN c.Sku IS NULL THEN 0 ELSE 1 END AS Repriced,
       SUM(CASE WHEN s.DateAdded <  t.At THEN s.Margin ELSE 0 END) AS GpB,
       SUM(CASE WHEN s.DateAdded >= t.At THEN s.Margin ELSE 0 END) AS GpA,
       MAX(CASE WHEN f.FirstSale < DATEADD(day, -30, t.At) THEN 1 ELSE 0 END) AS SoldEarlier
INTO #s
FROM #at t
JOIN hk.SalesLines s ON s.LayerId = t.LayerId AND s.DateAdded >= DATEADD(day, -30, t.At) AND s.DateAdded < DATEADD(day, 30, t.At)
LEFT JOIN hk.Products p ON p.LayerId = s.LayerId AND p.Sku = s.Sku
LEFT JOIN #ch c ON c.ApprovalId = t.ApprovalId AND c.Sku = s.Sku
LEFT JOIN #first f ON f.LayerId = s.LayerId AND f.Sku = s.Sku
WHERE NOT EXISTS (SELECT 1 FROM hk.CampaignWindows cw WHERE s.DateAdded >= cw.StartDate AND s.DateAdded < DATEADD(day, 1, cw.EndDate))
GROUP BY t.ApprovalId, t.Kind, s.Sku, ISNULL(p.CategoryL2, '(none)'), CASE WHEN c.Sku IS NULL THEN 0 ELSE 1 END;

WITH cat AS (
    SELECT ApprovalId, Kind, Cat,
           SUM(CASE WHEN Repriced = 1 THEN 1 ELSE 0 END) AS nR, SUM(CASE WHEN Repriced = 0 THEN 1 ELSE 0 END) AS nU,
           SUM(CASE WHEN Repriced = 1 THEN GpA - GpB ELSE 0 END) AS dR, SUM(CASE WHEN Repriced = 0 THEN GpA - GpB ELSE 0 END) AS dU
    FROM #s WHERE SoldEarlier = 1
    GROUP BY ApprovalId, Kind, Cat
)
SELECT ApprovalId, CASE WHEN ApprovalId = 1 THEN 'Gjirafa50 KS' ELSE 'GjirafaMall KS' END AS Shop, Kind,
       CAST(SUM(CASE WHEN nU > 0 THEN dR - nR * dU / nU ELSE 0 END) / 30.0 * 30.4 AS int) AS VsUntouchedPerMonth,
       SUM(nR) AS RepricedSkus
FROM cat GROUP BY ApprovalId, Kind ORDER BY ApprovalId, CASE Kind WHEN 'real' THEN 0 ELSE 1 END, Kind;
