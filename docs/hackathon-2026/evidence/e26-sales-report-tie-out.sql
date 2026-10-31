SET NOCOUNT ON;
-- E26: the Sales Report tie-out (plan section 4; exports/README.md, 30 Oct): the gross profit of each approval's SKUs in the 30 days
-- before it, from hk.SalesLines (the Sales Report's lines as exported read-only), against the deployed tool's own
-- Approvals.Profit30Before (which the tool computes from the Sales Report). The same layer, the exact 30 days before the approval's
-- time. Reads only PricingToolHackathon; about 2 s.
SELECT a.Id AS ApprovalId, a.LayerId, CAST(a.ApprovedUtc AS date) AS Approved, CAST(a.Profit30Before AS decimal(12, 0)) AS ToolProfit30Before,
       CAST(x.Gp AS decimal(12, 0)) AS FromSalesLines, CAST(100.0 * (x.Gp - a.Profit30Before) / NULLIF(a.Profit30Before, 0) AS decimal(6, 1)) AS DiffPct
FROM PricingTool.Approvals a
CROSS APPLY (SELECT SUM(sl.Margin) AS Gp
             FROM hk.SalesLines sl
             WHERE sl.LayerId = a.LayerId AND sl.DateAdded >= DATEADD(day, -30, a.ApprovedUtc) AND sl.DateAdded < a.ApprovedUtc
               AND sl.Sku IN (SELECT i.Sku FROM PricingTool.ApprovalItems i WHERE i.ApprovalId = a.Id)) x
WHERE a.Id IN (2, 12, 25, 30, 35, 1, 32, 37)
ORDER BY a.LayerId DESC, a.ApprovedUtc;
