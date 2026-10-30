using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PricingTool.Core.Abstractions;
using PricingTool.Core.Domain;
using PricingTool.Core.Services;
using PricingTool.Data;
using PricingTool.Web.Models;
using PricingTool.Web.Services;

namespace PricingTool.Web.Controllers;

/// <summary>Per-SKU drill-down: price history, every run's votes and reasons, sales response.</summary>
[Authorize(Roles = "Analyst,Manager")]
public class SkuController : Controller
{
    private readonly PricingToolDbContext _db;
    private readonly CurrentLayerService _layers;
    private readonly ISkuSalesHistoryReader _sales;
    private readonly ILogger<SkuController> _logger;

    public SkuController(
        PricingToolDbContext db, CurrentLayerService layers,
        ISkuSalesHistoryReader sales, ILogger<SkuController> logger)
    {
        _db = db;
        _layers = layers;
        _sales = sales;
        _logger = logger;
    }

    public async Task<IActionResult> Details(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return NotFound();
        var sku = id.Trim();
        var layerId = await _layers.RequireCurrentIdAsync();

        var snapshots = await _db.DailySnapshots.AsNoTracking()
            .Where(s => s.LayerId == layerId && s.Sku == sku)
            .OrderBy(s => s.SnapshotDate)
            .ToListAsync();

        var proposals = await _db.ProposedPrices.AsNoTracking()
            .Where(p => p.LayerId == layerId && p.Sku == sku)
            .Include(p => p.Votes)
            .Include(p => p.PricingRun)
            .OrderByDescending(p => p.PricingRunId)
            .Take(30)
            .ToListAsync();

        if (snapshots.Count == 0 && proposals.Count == 0) return NotFound();

        var model = new SkuDetailsViewModel
        {
            Sku = sku,
            Latest = snapshots.LastOrDefault(),
            Proposals = proposals.Select(p => new SkuProposalHistory
            {
                Run = p.PricingRun,
                Proposal = p,
            }).ToList(),
        };
        return View(model);
    }

    /// <summary>
    /// On-demand sales history for the SKU details charts, queried live from the SR_ProductsData source
    /// (same window/scope as the elasticity fit) and never stored:
    ///  • <c>pricePoints</c> — weekly buckets for the price→gross-profit scatter, Y = the ACTUAL VAT-net
    ///    gross profit earned that week (SUM(Margin) = SUM(NetoPrice − Cogs)), using the real per-order
    ///    landed cost — not today's PPTCV. No PPTCV needed, so it renders whenever there's sales history.
    ///  • <c>monthlyNetSales</c> — monthly totals of net sales (VAT-exclusive NetoPrice) + units.
    /// Always returns JSON (never 500s the page): per-chart messages cover no-history / error.
    /// </summary>
    public async Task<IActionResult> SalesHistoryData(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return BadRequest();
        var sku = id.Trim();
        var layerId = await _layers.RequireCurrentIdAsync();

        var layer = await _db.Layers.AsNoTracking().FirstOrDefaultAsync(l => l.Id == layerId);
        if (layer is null)
            return Json(new { pricePoints = Array.Empty<object>(), monthlyNetSales = Array.Empty<object>(), priceMessage = "No layer selected.", monthlyMessage = "No layer selected." });

        // Current all-in cost + price from the SKU's latest snapshot (Pptcv = the per-country PPTCV).
        var snap = await _db.DailySnapshots.AsNoTracking()
            .Where(s => s.LayerId == layerId && s.Sku == sku)
            .OrderByDescending(s => s.SnapshotDate)
            .Select(s => new { s.Pptcv, s.CurrentPrice })
            .FirstOrDefaultAsync();

        IReadOnlyList<SkuPriceBucket> buckets;
        IReadOnlyList<SkuMonthlyNetSales> months;
        try
        {
            buckets = await _sales.GetWeeklyBucketsAsync(layer.SrPlatformId, layer.SrCompanyId, sku, windowDays: 365);
            months = await _sales.GetMonthlyNetSalesAsync(layer.SrPlatformId, layer.SrCompanyId, sku, monthsBack: 24);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sales history query failed for SKU {Sku}.", sku);
            return Json(new { pricePoints = Array.Empty<object>(), monthlyNetSales = Array.Empty<object>(), priceMessage = "Sales history is unavailable right now.", monthlyMessage = "Sales history is unavailable right now." });
        }

        // Scatter: ACTUAL VAT-net gross profit per weekly bucket, from SR_ProductsData.Margin (= NetoPrice −
        // Cogs) using the real landed cost recorded on each order line — not today's PPTCV.
        var pricePoints = buckets.Select(b => (object)new
        {
            x = b.UnitPrice,
            y = Math.Round(b.RealizedProfit, 2),
            units = b.Units,
        }).ToArray();
        string? priceMessage = pricePoints.Length == 0 ? "No sales history to plot." : null;

        var monthlyNetSales = months.Select(m => new
        {
            label = $"{m.Year:0000}-{m.Month:00}",
            netSales = Math.Round(m.NetSales, 2),
            units = m.Units,
        }).ToArray();

        return Json(new
        {
            pricePoints,
            currentPrice = snap?.CurrentPrice,
            priceMessage,
            monthlyNetSales,
            monthlyMessage = monthlyNetSales.Length == 0 ? "No sales history." : null,
        });
    }
}
