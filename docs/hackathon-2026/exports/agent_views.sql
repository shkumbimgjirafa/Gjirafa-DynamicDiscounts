-- The curated views the agents query through query_data (plan section 5). They carry the data
-- definitions, so an agent's own SQL can't get them wrong:
--   * money is in EUR, gross profit = booked margin (SR Cogs), revenue is VAT-excl unless named gross
--   * outlet, Apple/Starlink and non-tool vendors are already out (hk.SalesLines is loaded that way)
--   * layers: 1 = GjirafaMall KS, 4 = Gjirafa50 KS
-- Run against PricingToolHackathon. Re-runnable.

IF SCHEMA_ID('agent') IS NULL EXEC('CREATE SCHEMA agent');
GO

CREATE OR ALTER VIEW agent.Layers AS
SELECT Id AS LayerId, DisplayName AS LayerName, Brand, CountryCode, Currency, StoreId
FROM PricingTool.Layers
WHERE Id IN (1, 4);
GO

CREATE OR ALTER VIEW agent.Products AS
SELECT p.LayerId, p.ProductId, p.Sku, p.Name, p.Brand, p.CategoryL1, p.CategoryL2, p.MainGroup,
       p.IsGj50Listed, p.IsOutlet, p.IsExcludedBrand
FROM hk.Products p;
GO

-- One row per order line. Qty and money are line totals. GrossProfit = NetRevenue - Cogs (booked).
-- BandId: the engine bands a SKU by its cost incl. VAT (PPTCV); here the booked unit cost x VAT
-- stands in for it, using today's band limits.
CREATE OR ALTER VIEW agent.SalesLines AS
SELECT s.LayerId, CAST(s.DateAdded AS date) AS SaleDate, s.DateAdded AS SaleTime, s.Sku, s.ProductId,
       s.Qty, s.NetoPrice AS NetRevenue, s.BrutoPrice AS GrossRevenue, s.Cogs, s.Margin AS GrossProfit,
       s.ProductDiscount AS DiscountGiven, s.SalesNoDiscount AS ListValue, s.StockClass,
       CAST(s.Cogs / NULLIF(s.Qty, 0) * ISNULL(s.Vat, 1.18) AS decimal(12,2)) AS UnitCostInclVat,
       b.Id AS BandId, b.Name AS BandName,
       p.Brand, ISNULL(p.CategoryL1, N'Unknown') AS CategoryL1, p.CategoryL2, ISNULL(p.MainGroup, N'Others') AS MainGroup,
       ISNULL(p.IsGj50Listed, 0) AS IsGj50Listed
FROM hk.SalesLines s
LEFT JOIN hk.Products p ON p.LayerId = s.LayerId AND p.ProductId = s.ProductId
OUTER APPLY (SELECT TOP 1 pb.Id, pb.Name FROM PricingTool.PriceBands pb
             WHERE pb.LayerId = s.LayerId
               AND s.Cogs / NULLIF(s.Qty, 0) * ISNULL(s.Vat, 1.18) >= pb.MinPrice
               AND s.Cogs / NULLIF(s.Qty, 0) * ISNULL(s.Vat, 1.18) < pb.MaxPrice
             ORDER BY pb.SortOrder) b;
GO

-- Daily totals per layer and main group: the cheap starting point for trend questions.
CREATE OR ALTER VIEW agent.DailySales AS
SELECT LayerId, SaleDate, MainGroup, CategoryL1, BandId,
       COUNT(*) AS Lines, SUM(Qty) AS Units, SUM(NetRevenue) AS NetRevenue, SUM(GrossProfit) AS GrossProfit,
       CAST(SUM(GrossProfit) / NULLIF(SUM(NetRevenue), 0) AS decimal(9,4)) AS MarginPct,
       SUM(DiscountGiven) AS DiscountGiven
FROM agent.SalesLines
GROUP BY LayerId, SaleDate, MainGroup, CategoryL1, BandId;
GO

CREATE OR ALTER VIEW agent.CampaignHistory AS
SELECT Id AS CampaignId, LayerId, Name, StartDate, EndDate, CampaignType, Orders,
       EffectiveMarginPct, NormalMarginPct, ProfitPerDayVsNormal, Label, Notes
FROM hk.CampaignWindows;
GO

-- Booked unit cost, last 30 days vs the 60 days before, per SKU with sales in both windows.
-- A proxy for cost moves until the engine's own daily snapshots carry enough PPTCV history.
CREATE OR ALTER VIEW agent.CostChanges AS
WITH w AS (
    SELECT LayerId, Sku,
           SUM(CASE WHEN SaleDate >= DATEADD(day, -30, CAST(GETDATE() AS date)) THEN Cogs END) /
           NULLIF(SUM(CASE WHEN SaleDate >= DATEADD(day, -30, CAST(GETDATE() AS date)) THEN Qty END), 0) AS UnitCostLast30,
           SUM(CASE WHEN SaleDate < DATEADD(day, -30, CAST(GETDATE() AS date)) AND SaleDate >= DATEADD(day, -90, CAST(GETDATE() AS date)) THEN Cogs END) /
           NULLIF(SUM(CASE WHEN SaleDate < DATEADD(day, -30, CAST(GETDATE() AS date)) AND SaleDate >= DATEADD(day, -90, CAST(GETDATE() AS date)) THEN Qty END), 0) AS UnitCostPrior60
    FROM agent.SalesLines
    WHERE SaleDate >= DATEADD(day, -90, CAST(GETDATE() AS date)) AND Qty > 0
    GROUP BY LayerId, Sku
)
SELECT LayerId, Sku, CAST(UnitCostPrior60 AS decimal(12,2)) AS UnitCostPrior60, CAST(UnitCostLast30 AS decimal(12,2)) AS UnitCostLast30,
       CAST((UnitCostLast30 - UnitCostPrior60) / NULLIF(UnitCostPrior60, 0) AS decimal(9,4)) AS ChangePct
FROM w
WHERE UnitCostLast30 IS NOT NULL AND UnitCostPrior60 IS NOT NULL;
GO

-- Today's hand-set floors and targets per band (the agents' starting point).
CREATE OR ALTER VIEW agent.BandConfig AS
SELECT b.LayerId, b.Id AS BandId, b.Name AS BandName, b.MinPrice, b.MaxPrice, b.MarginFloorPct, b.TargetMarginPct, b.SortOrder
FROM PricingTool.PriceBands b
WHERE b.LayerId IN (1, 4);
GO

-- Band floor/target history per engine run (from 2026-10-21).
CREATE OR ALTER VIEW agent.BandTargetHistory AS
SELECT * FROM PricingTool.PricingRunBandTargets WHERE LayerId IN (1, 4);
GO

-- The test phase: batches of calculated prices that went live by hand, with the tool's own
-- before/after profit windows (30-day, booked COGS).
CREATE OR ALTER VIEW agent.TestPhaseBatches AS
SELECT a.Id AS ApprovalId, a.LayerId, a.ApprovedUtc, a.SkuCount, a.IncreaseCount, a.DecreaseCount,
       a.Profit30Before, a.Profit30After, a.Profit90Before, a.Profit90After, a.ProfitRefreshedUtc
FROM PricingTool.Approvals a;
GO

CREATE OR ALTER VIEW agent.TestPhasePriceChanges AS
SELECT i.ApprovalId, a.LayerId, a.ApprovedUtc, i.Sku, i.OldPrice, i.NewPrice,
       CAST((i.NewPrice - i.OldPrice) / NULLIF(i.OldPrice, 0) AS decimal(9,4)) AS ChangePct
FROM PricingTool.ApprovalItems i
JOIN PricingTool.Approvals a ON a.Id = i.ApprovalId;
GO
