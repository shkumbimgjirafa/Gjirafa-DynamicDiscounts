using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PricingTool.Core.Abstractions;
using PricingTool.Core.Domain;
using PricingTool.Core.Options;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Web.Models;
using PricingTool.Web.Services;

namespace PricingTool.Web.Controllers;

/// <summary>Impact dashboard: KPI trends vs. target, attribution, health flags.</summary>
[Authorize(Roles = "Analyst,Manager")]
public class HomeController : Controller
{
    private readonly PricingToolDbContext _db;
    private readonly CurrentLayerService _layers;
    private readonly ISalesReportProfitReader _profit;
    private readonly PricingEngineOptions _options;
    private readonly ILogger<HomeController> _logger;

    public HomeController(
        PricingToolDbContext db, CurrentLayerService layers, ISalesReportProfitReader profit,
        IOptions<PricingEngineOptions> options, ILogger<HomeController> logger)
    {
        _db = db;
        _layers = layers;
        _profit = profit;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IActionResult> Index()
    {
        var layerId = await _layers.RequireCurrentIdAsync();
        var model = new DashboardViewModel();
        var vatK = 1m + await _db.Layers.Where(l => l.Id == layerId).Select(l => l.VatRatePct).FirstAsync() / 100m;

        // ---- Weekly profit trend (general): realized profit (SUM Margin) per ISO week from the sales
        // report, from the configured tracking-start week forward. Layer-wide, source-derived, NOT tied to
        // approvals. Best-effort — a source hiccup must not fail the whole dashboard.
        var srScope = await _db.Layers.Where(l => l.Id == layerId)
            .Select(l => new { l.SrPlatformId, l.SrCompanyId, l.OperationalDatabase }).FirstAsync();
        try
        {
            model.WeeklyProfit = (await _profit.GetWeeklyLayerProfitAsync(
                srScope.SrPlatformId, srScope.SrCompanyId, srScope.OperationalDatabase,
                _options.WeeklyProfitTrendStartUtc)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Weekly profit trend unavailable (source read failed) — chart shows empty.");
        }

        // ---- Daily KPI trend from the per-(layer, date) rollup (DailyLayerKpi), written once per run
        // from that day's snapshots. It mirrors the old GROUP BY over DailySnapshots but is never
        // retention-trimmed, so the earliest rows stay the true pre-tool baseline after old snapshots
        // are deleted. Margin = (gross revenue − all-in cost) / gross revenue; PPTCV is VAT-inclusive,
        // so the VAT-exclusive net sales revenue is grossed up (× vatK) before comparing to cost.
        var kpiRows = await _db.DailyLayerKpi
            .Where(k => k.LayerId == layerId)
            .OrderBy(k => k.Date)
            .Select(k => new
            {
                k.Date,
                k.Net7,
                k.Qty7,
                k.CostedNet7,
                k.Cost7,
            })
            .ToListAsync();

        model.Trend = kpiRows.Select(x =>
        {
            var grossCostedRev = x.CostedNet7 * vatK; // PPTCV is all-in (VAT-incl) → compare to gross revenue
            return new DailyKpi(
                x.Date,
                grossCostedRev > 0 ? Math.Round((grossCostedRev - x.Cost7) / grossCostedRev * 100m, 2) : 0,
                Math.Round(x.Net7 / 7m, 2),
                Math.Round(x.Qty7 / 7m, 1));
        }).ToList();

        // ---- Baseline (earliest runs = pre-tool period) vs the most recent runs. DailyLayerKpi is one row
        // per run, so this compares up to 7 early runs against 7 recent ones — a wider calendar span at the
        // 15-day cadence than it was at the old daily cadence, but the same pre-tool-vs-now comparison.
        if (model.Trend.Count >= 2)
        {
            var take = Math.Min(7, model.Trend.Count / 2);
            model.Baseline = Summarize(model.Trend.Take(take).ToList());
            model.Recent = Summarize(model.Trend.TakeLast(take).ToList());

            if (model.Baseline.MarginPct > 0)
                model.MarginLiftPct = Math.Round(
                    (model.Recent.MarginPct - model.Baseline.MarginPct) / model.Baseline.MarginPct * 100m, 1);
            if (model.Baseline.UnitsPerDay > 0)
                model.VolumeChangePct = Math.Round(
                    (model.Recent.UnitsPerDay - model.Baseline.UnitsPerDay) / model.Baseline.UnitsPerDay * 100m, 1);
        }

        // ---- Latest finished run: attribution + health flags.
        var lastRun = await _db.PricingRuns
            .Where(r => r.LayerId == layerId && r.Status != RunStatus.Running)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync();
        model.LastRun = lastRun;

        if (lastRun is not null)
        {
            // Attribution is aggregated in SQL — a full-catalog run has 100k+ changed rows, so we must
            // never materialize them into memory (doing so timed out the dashboard). ReasonCodes is
            // comma-joined, so it's split server-side via STRING_SPLIT.
            var algoRows = await _db.Database.SqlQuery<AlgoAttrRow>($@"
SELECT s.value AS Code, COUNT(*) AS ChangedProposals, CAST(AVG(ABS(p.ChangePct)) AS decimal(9,2)) AS AvgAbsChangePct
FROM [PricingTool].[ProposedPrices] p
CROSS APPLY STRING_SPLIT(p.ReasonCodes, ',') s
WHERE p.PricingRunId = {lastRun.Id} AND p.HasChange = 1 AND p.Status <> {(int)ProposalStatus.Skipped} AND s.value <> ''
GROUP BY s.value").ToListAsync();
            model.AlgorithmAttribution = algoRows
                .OrderByDescending(r => r.ChangedProposals)
                .Select(r => new AlgorithmAttribution(r.Code, r.ChangedProposals, Math.Round(r.AvgAbsChangePct, 2)))
                .ToList();

            var bandNames = await _db.PriceBands.Where(b => b.LayerId == layerId).ToDictionaryAsync(b => b.Id, b => b.Name);
            var bandRows = await _db.ProposedPrices
                .Where(p => p.PricingRunId == lastRun.Id && p.HasChange && p.Status != ProposalStatus.Skipped)
                .GroupBy(p => p.PriceBandId)
                .Select(g => new { BandId = g.Key, Count = g.Count(), AvgChange = g.Average(x => x.ChangePct) })
                .ToListAsync();
            model.BandAttribution = bandRows
                .Select(g => new BandAttribution(
                    g.BandId.HasValue && bandNames.TryGetValue(g.BandId.Value, out var name) ? name : "(no band)",
                    g.Count,
                    Math.Round(g.AvgChange, 2)))
                .OrderByDescending(b => b.ChangedProposals)
                .ToList();

            model.MissingCostSkus = await _db.ProposedPrices
                .CountAsync(p => p.PricingRunId == lastRun.Id && p.SkipReason == "MISSING_COST");
            model.MissingCostSampleSkus = await _db.ProposedPrices
                .Where(p => p.PricingRunId == lastRun.Id && p.SkipReason == "MISSING_COST")
                .OrderBy(p => p.Sku).Take(10).Select(p => p.Sku).ToListAsync();
            model.GuardrailClampedSkus = await _db.ProposedPrices
                .CountAsync(p => p.PricingRunId == lastRun.Id && p.HasChange && p.Status != ProposalStatus.Skipped && p.GuardrailFlags != "");
        }

        // Failed among the most recent N runs for this layer — a run-count window, not a calendar one, so
        // it stays meaningful at the 15-day cadence (a fixed 7-day window would usually hold zero runs).
        var recentRunStatuses = await _db.PricingRuns
            .Where(r => r.LayerId == layerId && r.Status != RunStatus.Running)
            .OrderByDescending(r => r.Id)
            .Take(DashboardViewModel.RecentRunsWindow)
            .Select(r => r.Status)
            .ToListAsync();
        model.RecentFailedRuns = recentRunStatuses.Count(s => s == RunStatus.Failed);

        // ---- "Did the bet pay off?" — per APPROVAL. Each approval is a batch that went live; its realized
        // profit (sales-report SUM Margin for its SKUs) over the 7/30/90 days before vs after the approval
        // is refreshed onto the row by the hourly worker. Most recent first.
        model.Approvals = await _db.Approvals
            .Where(a => a.LayerId == layerId)
            .OrderByDescending(a => a.ApprovedUtc).ThenByDescending(a => a.Id)
            .Take(20)
            .ToListAsync();

        // ---- Dead stock. Prefer an explicitly approved & pushed batch (the SKUs we ACTUALLY pushed,
        // recorded in ApprovedBatches because the push happens outside the tool) when one exists for the
        // layer; its clearance is tracked over a 14-day window from the push date, with sold-since-push
        // units/net refreshed from the live source onto ApprovedBatchItems. Otherwise fall back to the
        // latest run's auto-flagged dead-stock population (the old two-cohort batch/revival view).
        var approvedBatch = await _db.ApprovedBatches
            .Where(b => b.LayerId == layerId)
            .OrderByDescending(b => b.PushedUtc).ThenByDescending(b => b.Id)
            .FirstOrDefaultAsync();
        if (approvedBatch is not null)
        {
            var items = _db.ApprovedBatchItems.Where(i => i.ApprovedBatchId == approvedBatch.Id);
            var skus = await items.CountAsync();
            var avgDisc = await items.Where(i => i.OldPrice > 0)
                .AverageAsync(i => (decimal?)((i.OldPrice - i.PushedPrice) / i.OldPrice)) ?? 0m;
            model.DeadStock = new DeadStockPanel
            {
                IsApprovedBatch = true,
                BatchLabel = approvedBatch.Label,
                PushedUtc = approvedBatch.PushedUtc,
                SalesRefreshedUtc = approvedBatch.SalesRefreshedUtc,
                TrackingDayNumber = (int)(DateTime.UtcNow.Date - approvedBatch.PushedUtc.Date).TotalDays + 1,
                DeadSkus = skus,
                BatchTotal = skus,
                CohortSkus = skus,
                StockUnits = await items.SumAsync(i => (long)i.StockAtPush),
                StockCost = await items.SumAsync(i => (i.Pptcv ?? 0m) * i.StockAtPush),
                AvgDiscountFraction = avgDisc,
                MovingSkus = await items.CountAsync(i => i.SoldUnits > 0),
                MovingUnits30 = await items.SumAsync(i => (long)i.SoldUnits),
                MovingNet30 = await items.SumAsync(i => i.SoldNet),
            };
            var top = await items
                .OrderByDescending(i => (i.Pptcv ?? 0m) * i.StockAtPush).ThenByDescending(i => i.SoldUnits)
                .Take(25)
                .Select(i => new { i.Sku, i.ProductName, i.Brand, i.PrePushPrice, i.PushedPrice, i.OldPrice, i.SoldUnits, i.StockAtPush })
                .ToListAsync();
            model.DeadStockMarkdown = top.Select(r => new DeadStockMarkdownRow(
                r.Sku, r.ProductName, r.Brand, 1, r.PrePushPrice, r.PushedPrice,
                r.OldPrice > 0 ? Math.Round((r.OldPrice - r.PushedPrice) / r.OldPrice, 4) : 0m,
                0, r.SoldUnits, r.StockAtPush)).ToList();

            // Movers = batch SKUs that have actually sold since the push. Surfaced separately because
            // the capital-sorted table above buries them beneath the (still-unsold) big-ticket items.
            var movers = await items.Where(i => i.SoldUnits > 0)
                .OrderByDescending(i => i.SoldUnits).ThenByDescending(i => i.SoldNet)
                .Take(200)
                .Select(i => new { i.Sku, i.ProductName, i.Brand, i.PushedPrice, i.SoldUnits, i.SoldNet, i.StockAtPush })
                .ToListAsync();
            model.DeadStockMovers = movers.Select(r => new DeadStockMoverRow(
                r.Sku, r.ProductName, r.Brand, r.PushedPrice, r.SoldUnits, r.SoldNet, r.StockAtPush)).ToList();

            // Sold out = had local stock at push, now zero (fully cleared). The success list; a top slice is
            // shown, the full set is a CSV download. Needs the hourly refresh to have run (CurrentStock set).
            var soldOut = await items.Where(i => i.StockAtPush > 0 && i.CurrentStock == 0)
                .OrderByDescending(i => i.StockAtPush).ThenBy(i => i.Sku)
                .Select(i => new { i.Sku, i.ProductName, i.Brand, i.PushedPrice, i.StockAtPush, i.SoldUnits, i.SoldNet })
                .ToListAsync();
            model.DeadStock.SoldOutCount = soldOut.Count;
            model.DeadStock.SoldOut = soldOut.Take(50).Select(r => new DeadStockSoldOutRow(
                r.Sku, r.ProductName, r.Brand, r.PushedPrice, r.StockAtPush, r.SoldUnits, r.SoldNet)).ToList();
        }
        else
        {
        // ---- Two cohorts on purpose (see DeadStockPanel):
        //  • BATCH  = the latest run's dead-stock population (DeadStockStage > 0) — the SKUs we're
        //    clearing / approve. Drives "In the tunnel", capital, avg depth, and the table. Not gated on
        //    HasChange: a SKU held at its already-pushed markdown is still in the tunnel.
        //  • REVIVAL = dead stock marked down across the recent (retained) runs, matched to the latest
        //    snapshot. Drives "sold since markdown" + sell-through. Wider than the batch because a SKU
        //    that actually sells stops being dead and leaves the batch — so this is the ONLY cohort in
        //    which sales are visible. Both aggregated in SQL (never materialized); table is a TOP-25.
        var deadSnapDate = await _db.DailySnapshots
            .Where(s => s.LayerId == layerId)
            .MaxAsync(s => (DateTime?)s.SnapshotDate);
        var batchRunId = await _db.ProposedPrices
            .Where(p => p.LayerId == layerId && p.DeadStockStage != DeadStockStage.None)
            .MaxAsync(p => (long?)p.PricingRunId);
        if (deadSnapDate is not null && batchRunId is not null)
        {
            _db.Database.SetCommandTimeout(120); // dead-stock joins to the latest snapshot at full-catalog scale
            var batchAgg = (await _db.Database.SqlQuery<DeadStockBatchRow>($@"
SELECT
  COUNT(*)                                                                                                AS DeadSkus,
  ISNULL(SUM(CONVERT(bigint, ISNULL(s.LocalWarehouseStock, 0))), 0)                                       AS StockUnits,
  ISNULL(SUM(CASE WHEN p.Pptcv IS NOT NULL THEN p.Pptcv * ISNULL(s.LocalWarehouseStock, 0) ELSE 0 END),0) AS StockCost,
  ISNULL(AVG(CASE WHEN p.OldPrice > 0 THEN (p.OldPrice - p.ProposedPriceValue) / p.OldPrice END), 0)      AS AvgDiscountFraction
FROM [PricingTool].[ProposedPrices] p
LEFT JOIN [PricingTool].[DailySnapshots] s
  ON s.LayerId = {layerId} AND s.SnapshotDate = {deadSnapDate.Value} AND s.Sku = p.Sku
WHERE p.PricingRunId = {batchRunId.Value} AND p.DeadStockStage > 0")
                .ToListAsync()).First();

            var revival = (await _db.Database.SqlQuery<DeadStockRevivalRow>($@"
SELECT
  COUNT(*)                                                                                   AS CohortSkus,
  ISNULL(SUM(CASE WHEN s.Qty30 > 0 THEN 1 ELSE 0 END), 0)                                     AS MovingSkus,
  ISNULL(SUM(CASE WHEN s.Qty30 > 0 THEN CONVERT(bigint, s.Qty30) ELSE 0 END), 0)              AS MovingUnits30,
  ISNULL(SUM(CASE WHEN s.Qty30 > 0 THEN s.Net30 ELSE 0 END), 0)                               AS MovingNet30
FROM [PricingTool].[DailySnapshots] s
WHERE s.LayerId = {layerId} AND s.SnapshotDate = {deadSnapDate.Value}
  AND s.Sku IN (SELECT p.Sku FROM [PricingTool].[ProposedPrices] p WHERE p.LayerId = {layerId} AND p.DeadStockStage > 0)")
                .ToListAsync()).First();

            model.DeadStock = new DeadStockPanel
            {
                DeadSkus = batchAgg.DeadSkus,
                StockUnits = batchAgg.StockUnits,
                StockCost = batchAgg.StockCost,
                AvgDiscountFraction = batchAgg.AvgDiscountFraction,
                BatchRunId = batchRunId,
                BatchTotal = batchAgg.DeadSkus,
                CohortSkus = revival.CohortSkus,
                MovingSkus = revival.MovingSkus,
                MovingUnits30 = revival.MovingUnits30,
                MovingNet30 = revival.MovingNet30,
                SnapshotDate = deadSnapDate,
            };

            var batch = await _db.Database.SqlQuery<DeadStockMarkdownSqlRow>($@"
SELECT TOP 25 p.Sku AS Sku, p.ProductName AS ProductName, p.Brand AS Brand,
  CONVERT(tinyint, p.DeadStockStage) AS Stage,
  p.CurrentPrice AS CurrentPrice, p.ProposedPriceValue AS NewPrice, p.OldPrice AS OldPrice,
  ISNULL(s.Qty7, 0) AS Qty7, ISNULL(s.Qty30, 0) AS Qty30, ISNULL(s.LocalWarehouseStock, 0) AS Stock
FROM [PricingTool].[ProposedPrices] p
LEFT JOIN [PricingTool].[DailySnapshots] s
  ON s.LayerId = {layerId} AND s.SnapshotDate = {deadSnapDate.Value} AND s.Sku = p.Sku
WHERE p.PricingRunId = {batchRunId.Value} AND p.DeadStockStage > 0
ORDER BY ISNULL(s.LocalWarehouseStock, 0) * ISNULL(p.Pptcv, 0) DESC, p.AbsChangePct DESC").ToListAsync();

            model.DeadStockMarkdown = batch
                .Select(r => new DeadStockMarkdownRow(
                    r.Sku, r.ProductName, r.Brand, r.Stage, r.CurrentPrice, r.NewPrice,
                    r.OldPrice > 0 ? Math.Round((r.OldPrice - r.NewPrice) / r.OldPrice, 4) : 0m,
                    r.Qty7, r.Qty30, r.Stock))
                .ToList();
        }
        }

        return View(model);
    }

    /// <summary>
    /// Downloads the current layer's latest approved batch's SOLD-OUT SKUs (had local stock at push, now 0)
    /// as CSV — the discount-cleanup worklist. Only rows that still carry a LIVE discount priced at the pushed
    /// price are included (ActiveDiscountIds non-empty): those are the promos to delete now the SKU has cleared.
    /// A cleared SKU whose pushed discount is already gone / re-priced has nothing of ours left to delete, so it
    /// is excluded (this is a narrower set than the dashboard's "SKUs cleared" count, which stays all-cleared).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> DeadStockSoldOutCsv()
    {
        var layerId = await _layers.RequireCurrentIdAsync();
        var batch = await _db.ApprovedBatches
            .Where(b => b.LayerId == layerId)
            .OrderByDescending(b => b.PushedUtc).ThenByDescending(b => b.Id)
            .FirstOrDefaultAsync();
        if (batch is null)
        {
            TempData["Error"] = "No approved batch to export.";
            return RedirectToAction(nameof(Index));
        }

        // Exclude cleared SKUs with no live discount at the pushed price: their promo was already deleted or
        // re-priced, so there is nothing for the DB clean-up to remove — keeping them just clutters the list.
        var rows = await _db.ApprovedBatchItems
            .Where(i => i.ApprovedBatchId == batch.Id && i.StockAtPush > 0 && i.CurrentStock == 0
                        && i.ActiveDiscountIds != null && i.ActiveDiscountIds != "")
            .OrderByDescending(i => i.StockAtPush).ThenBy(i => i.Sku)
            .Select(i => new { i.Sku, i.ProductName, i.Brand, i.Pptcv, i.PrePushPrice, i.PushedPrice, i.StockAtPush, i.SoldUnits, i.CompletedSoldUnits, i.SoldNet, i.ActiveDiscountIds })
            .ToListAsync();

        var sb = new StringBuilder();
        // Pptcv = all-in cost at push (blank when unknown); PrePushPrice = the price the shopper saw
        // immediately BEFORE the push, so a buyer can read cost -> previous price -> pushed price.
        // CompletedSold = units in fully COMPLETED orders (status 30) — a subset of SoldUnits (20+30).
        // DiscountIds = the live public-facing discount id(s) carrying the pushed price — delete once cleared.
        sb.AppendLine("Sku,Product,Brand,Pptcv,PrePushPrice,PushedPrice,StockAtPush,SoldUnits,CompletedSold,SoldNetExclVat,DiscountIds");
        foreach (var r in rows)
            sb.AppendLine(string.Join(",",
                Csv(r.Sku), Csv(r.ProductName ?? ""), Csv(r.Brand ?? ""),
                r.Pptcv.HasValue ? r.Pptcv.Value.ToString("0.00", CultureInfo.InvariantCulture) : "",
                r.PrePushPrice.ToString("0.00", CultureInfo.InvariantCulture),
                r.PushedPrice.ToString("0.00", CultureInfo.InvariantCulture),
                r.StockAtPush.ToString(CultureInfo.InvariantCulture),
                r.SoldUnits.ToString(CultureInfo.InvariantCulture),
                r.CompletedSoldUnits.ToString(CultureInfo.InvariantCulture),
                r.SoldNet.ToString("0.00", CultureInfo.InvariantCulture),
                DiscountIds(r.ActiveDiscountIds)));

        var fileName = $"dead-stock-sold-out-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
        // UTF-8 BOM so Excel reads accented SKUs / product names correctly.
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(sb.ToString());
        return File(bytes, "text/csv; charset=utf-8", fileName);
    }

    /// <summary>RFC-4180 CSV field: quote if it contains comma/quote/newline; double embedded quotes.
    /// Also neutralises a leading =/+/-/@ so a spreadsheet doesn't treat the cell as a formula.</summary>
    private static string Csv(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var s = value;
        if (s[0] is '=' or '+' or '-' or '@') s = "'" + s;
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            s = "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    /// <summary>Discount-id list for CSV. Ids are stored comma-joined ("201,362"); a quoted comma field is
    /// auto-parsed by Excel as one number with thousands separators (201,362 -> 201362). Emit them
    /// space-separated so the spreadsheet keeps them as a plain text list (mirrors the Proposals export).</summary>
    private static string DiscountIds(string? value) =>
        string.IsNullOrEmpty(value) ? "" : value.Replace(",", " ");

    // Row shape for the SQL-side algorithm-attribution aggregation (EF "unmapped query type").
    private sealed class AlgoAttrRow
    {
        public string Code { get; set; } = "";
        public int ChangedProposals { get; set; }
        public decimal AvgAbsChangePct { get; set; }
    }

    // Row shapes for the dead-stock dashboard SQL (EF "unmapped query types").
    private sealed class DeadStockBatchRow
    {
        public int DeadSkus { get; set; }
        public long StockUnits { get; set; }
        public decimal StockCost { get; set; }
        public decimal AvgDiscountFraction { get; set; }
    }

    private sealed class DeadStockRevivalRow
    {
        public int CohortSkus { get; set; }
        public int MovingSkus { get; set; }
        public long MovingUnits30 { get; set; }
        public decimal MovingNet30 { get; set; }
    }

    private sealed class DeadStockMarkdownSqlRow
    {
        public string Sku { get; set; } = "";
        public string? ProductName { get; set; }
        public string? Brand { get; set; }
        public byte Stage { get; set; }
        public decimal CurrentPrice { get; set; }
        public decimal NewPrice { get; set; }
        public decimal OldPrice { get; set; }
        public int Qty7 { get; set; }
        public int Qty30 { get; set; }
        public int Stock { get; set; }
    }

    private static KpiSummary Summarize(IReadOnlyList<DailyKpi> days) => new(
        Math.Round(days.Average(d => d.MarginPct), 2),
        Math.Round(days.Average(d => d.NetRevenuePerDay), 2),
        Math.Round(days.Average(d => d.UnitsPerDay), 1));

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
