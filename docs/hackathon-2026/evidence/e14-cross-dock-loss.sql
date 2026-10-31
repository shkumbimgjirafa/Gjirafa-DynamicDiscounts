SET NOCOUNT ON;
-- E14: cross-dock lines sold under their booked cost, a month: the negative gross profit of cross-dock lines (the WMS check-in on
-- or after the order date) over the 12 months ending 16 Oct 2026 (the newest two weeks left out: late cancellations), both KS
-- layers. Lever 1's base. Reads only PricingToolHackathon; under a second.
SELECT CAST(-SUM(GrossProfit) / 12 AS decimal(12, 0)) AS CrossDockLossPerMonth
FROM agent.SalesLines
WHERE LayerId IN (1, 4) AND StockClass = 'CrossDock' AND GrossProfit < 0
  AND SaleDate >= DATEADD(day, -365, '2026-10-16') AND SaleDate < '2026-10-16';
