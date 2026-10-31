SET NOCOUNT ON;
-- E13: Kosovo gross profit a month, both KS layers, Aug-Sep 2026: booked sales lines, every day, tool scope (no outlet, Apple,
-- Starlink). The base the levers and the calculator's effect are measured against. Reads only PricingToolHackathon; under a second.
SELECT CAST(SUM(GrossProfit) / 2 AS decimal(12, 0)) AS KsGpPerMonth
FROM agent.SalesLines
WHERE LayerId IN (1, 4) AND SaleDate >= '2026-08-01' AND SaleDate < '2026-10-01';
