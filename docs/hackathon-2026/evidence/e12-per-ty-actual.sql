SET NOCOUNT ON;
-- E12: what Për ty Oct 2026 (14-18 Oct) made on GjirafaMall's own SKUs (not listed on Gjirafa50), from the booked sales lines;
-- it scores the hand plan's 15.1 % forecast. Provisional: cancellations post for about two weeks. Reads only
-- PricingToolHackathon (agent.SalesLines over hk.SalesLines); under a second.
SELECT CAST(SUM(GrossProfit) AS decimal(12, 0)) AS Gp, CAST(100.0 * SUM(GrossProfit) / SUM(NetRevenue) AS decimal(5, 1)) AS MarginPct
FROM agent.SalesLines
WHERE LayerId = 1 AND SaleDate >= '2026-10-14' AND SaleDate < '2026-10-19' AND ISNULL(IsGj50Listed, 0) = 0;
