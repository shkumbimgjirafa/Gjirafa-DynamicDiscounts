SET NOCOUNT ON;
-- Approved raises that didn't reach the shelf in today's process (the CSV push the team imports as discounts).
-- Reads only PricingToolHackathon: PricingTool.ApprovalItems, hk.SalesLines, hk.CampaignWindows and
-- hk.DiscountHistoryRaisedKs (the discount history of the products below, loaded by the data session on 31 Oct by
-- exports/discount_history_raised_ks.sql). About 15 s.
--
-- A raise = an approval item with NewPrice > OldPrice x 1.02. Lines = sales of those SKUs on the same layer from the day
-- after the approval went live to the day before the next one did (the same live spans as e20-test-phase.sql), outside
-- campaign days. Unit price = BrutoPrice / Qty (with VAT, as the shelf shows it). "At the old price" = within 1.1 % of
-- OldPrice; "at the new price" = within 1.1 % of NewPrice. GP forgone on a line sold at the old price: at constant units
-- Qty x (NewPrice - unit price) / VAT; at elasticity -1 (the backtest's assumption) Cogs x (1 - unit price / NewPrice).
--
-- What held the old price: the cheapest fixed-amount public discount (DiscountApplyTypeId 2) live at the sale whose
-- amount is the unit price. It is the calculator's EARLIER PUSH still live when its amount is a price an earlier approval
-- gave that SKU; otherwise a TEAM DEAL (DiscountPlatformTypeId 2 = app only); no such discount = the REGULAR PRICE was
-- never raised (a discount can't raise a price). v2's publisher removes the first and third (it ends its own discount in
-- the same transaction and writes raises to TierPrice); it leaves team deals alone, as a person's call.
--
-- Result sets: 1 per approval; 2 per approval and holder (with the holder's discount family); 3 Aug-Sep per month.

IF OBJECT_ID('tempdb..#spans') IS NOT NULL DROP TABLE #spans;
SELECT * INTO #spans FROM (VALUES
    (12, 1, CAST('2026-08-06' AS date), CAST('2026-09-02' AS date)),
    (25, 1, CAST('2026-09-03' AS date), CAST('2026-10-05' AS date)),
    (35, 1, CAST('2026-10-19' AS date), CAST('2026-10-30' AS date)),
    (37, 4, CAST('2026-10-21' AS date), CAST('2026-10-30' AS date))) v(ApprovalId, LayerId, LiveFrom, LiveTo);

IF OBJECT_ID('tempdb..#l') IS NOT NULL DROP TABLE #l;
SELECT sp.ApprovalId, sp.LayerId, sp.LiveFrom, sp.LiveTo, sl.Sku, sl.ProductId, sl.DateAdded, sl.Qty, sl.Cogs, sl.Vat,
       sl.BrutoPrice / NULLIF(sl.Qty, 0) AS UnitP, ai.OldPrice, ai.NewPrice,
       CASE sl.LayerId WHEN 1 THEN 'GjirafaMall' ELSE 'GjirafaEcommerce' END AS Db
INTO #l
FROM #spans sp
JOIN PricingTool.ApprovalItems ai ON ai.ApprovalId = sp.ApprovalId AND ai.NewPrice > ai.OldPrice * 1.02
JOIN hk.SalesLines sl ON sl.Sku = ai.Sku AND sl.LayerId = sp.LayerId
 AND sl.DateAdded >= DATEADD(day, 1, sp.LiveFrom) AND sl.DateAdded < DATEADD(day, 1, sp.LiveTo)
WHERE sl.Qty > 0
  AND NOT EXISTS (SELECT 1 FROM hk.CampaignWindows cw WHERE sl.DateAdded >= cw.StartDate AND sl.DateAdded < DATEADD(day, 1, cw.EndDate));

IF OBJECT_ID('tempdb..#h') IS NOT NULL DROP TABLE #h;
SELECT l.*, h.Name AS HolderName, h.DiscountPlatformTypeId AS HolderPlatform,
       CASE WHEN h.DiscountId IS NULL THEN 'regular price not raised'
            WHEN EXISTS (SELECT 1 FROM PricingTool.ApprovalItems a2
                         WHERE a2.Sku = l.Sku AND a2.ApprovalId < l.ApprovalId AND ABS(a2.NewPrice - h.DiscountAmount) < 0.011)
              THEN 'earlier calculator push still live'
            ELSE 'team deal' END AS Holder
INTO #h
FROM #l l
OUTER APPLY (SELECT TOP 1 d.DiscountId, d.Name, d.DiscountAmount, d.DiscountPlatformTypeId
             FROM hk.DiscountHistoryRaisedKs d
             WHERE d.Db = l.Db AND d.ProductId = l.ProductId AND d.DiscountApplyTypeId = 2 AND d.UsePercentage = 0
               AND d.StartDateUtc <= l.DateAdded AND (d.EndDateUtc IS NULL OR d.EndDateUtc >= l.DateAdded)
               AND ABS(d.DiscountAmount - l.UnitP) <= 0.011 * l.UnitP
             ORDER BY d.DiscountAmount, d.StartDateUtc DESC) h
WHERE ABS(l.UnitP - l.OldPrice) <= 0.011 * l.OldPrice;

-- 1. Per approval: where the units of its raised SKUs sold, and the GP the old-price units forwent
SELECT l.ApprovalId, CASE l.LayerId WHEN 1 THEN 'GjirafaMall KS' ELSE 'Gjirafa50 KS' END AS Layer,
       MIN(l.LiveFrom) AS LiveFrom, MIN(l.LiveTo) AS LiveTo, DATEDIFF(day, MIN(l.LiveFrom), MIN(l.LiveTo)) AS Days,
       CAST(SUM(l.Qty) AS int) AS Units,
       CAST(SUM(CASE WHEN ABS(l.UnitP - l.NewPrice) <= 0.011 * l.NewPrice THEN l.Qty ELSE 0 END) AS int) AS AtNewPrice,
       CAST(SUM(CASE WHEN ABS(l.UnitP - l.OldPrice) <= 0.011 * l.OldPrice THEN l.Qty ELSE 0 END) AS int) AS AtOldPrice,
       CAST(100.0 * SUM(CASE WHEN ABS(l.UnitP - l.OldPrice) <= 0.011 * l.OldPrice THEN l.Qty ELSE 0 END) / SUM(l.Qty) AS decimal(5, 1)) AS PctAtOld,
       CAST(SUM(CASE WHEN ABS(l.UnitP - l.OldPrice) <= 0.011 * l.OldPrice THEN l.Qty * (l.NewPrice - l.UnitP) / l.Vat ELSE 0 END) AS int) AS GpForgoneConstUnits,
       CAST(SUM(CASE WHEN ABS(l.UnitP - l.OldPrice) <= 0.011 * l.OldPrice THEN l.Cogs * (1 - l.UnitP / l.NewPrice) ELSE 0 END) AS int) AS GpForgoneEm1
FROM #l l GROUP BY l.ApprovalId, l.LayerId ORDER BY l.ApprovalId;

-- 2. What held the old price
SELECT ApprovalId, Holder,
       CASE WHEN HolderName IS NULL THEN ''
            WHEN HolderName LIKE '%-0000%' THEN LEFT(HolderName, CHARINDEX('-0000', HolderName) - 1)
            ELSE LEFT(HolderName, LEN(HolderName) - CHARINDEX('-', REVERSE(HolderName))) END AS HolderFamily,
       CASE HolderPlatform WHEN 2 THEN 'app only' WHEN 0 THEN 'web and app' ELSE '' END AS Platform,
       COUNT(*) AS Lines, CAST(SUM(Qty) AS int) AS Units,
       CAST(SUM(Qty * (NewPrice - UnitP) / Vat) AS int) AS GpForgoneConstUnits,
       CAST(SUM(Cogs * (1 - UnitP / NewPrice)) AS int) AS GpForgoneEm1
FROM #h
GROUP BY ApprovalId, Holder,
         CASE WHEN HolderName IS NULL THEN ''
              WHEN HolderName LIKE '%-0000%' THEN LEFT(HolderName, CHARINDEX('-0000', HolderName) - 1)
              ELSE LEFT(HolderName, LEN(HolderName) - CHARINDEX('-', REVERSE(HolderName))) END,
         CASE HolderPlatform WHEN 2 THEN 'app only' WHEN 0 THEN 'web and app' ELSE '' END
ORDER BY ApprovalId, GpForgoneEm1 DESC;

-- 3. The calculator's era, Aug-Sep (approvals 12 and 25, 59 days), EUR a month at elasticity -1
SELECT CASE WHEN Holder = 'team deal' THEN 'team deal (v2 leaves it to a person)'
            ELSE 'earlier push still live or regular price not raised (v2''s publisher removes it)' END AS Part,
       CAST(SUM(Cogs * (1 - UnitP / NewPrice)) * 30.4 / 59 AS int) AS EurPerMonthEm1,
       CAST(SUM(Qty * (NewPrice - UnitP) / Vat) * 30.4 / 59 AS int) AS EurPerMonthConstUnits
FROM #h WHERE ApprovalId IN (12, 25)
GROUP BY CASE WHEN Holder = 'team deal' THEN 'team deal (v2 leaves it to a person)'
              ELSE 'earlier push still live or regular price not raised (v2''s publisher removes it)' END;
