using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PricingTool.Core.Services;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Web.Models;
using PricingTool.Web.Services;

namespace PricingTool.Web.Controllers;

/// <summary>
/// "Movers" — two quick reads of the latest finished run's CHANGED proposals, joined to that run's
/// snapshot (for sales + stock): the top sellers being re-priced, and zero-sale items we hold locally
/// (KS stock) that are being re-priced. Both capped at 50.
/// </summary>
[Authorize(Roles = "Analyst,Manager")]
public class MoversController : Controller
{
    private const int TopN = 50;

    private readonly PricingToolDbContext _db;
    private readonly CurrentLayerService _layers;

    public MoversController(PricingToolDbContext db, CurrentLayerService layers)
    {
        _db = db;
        _layers = layers;
    }

    public async Task<IActionResult> Index(int? supplierId)
    {
        _db.Database.SetCommandTimeout(120); // joins the run's changed rows to the day's snapshot
        var layerId = await _layers.RequireCurrentIdAsync();
        var model = new MoversViewModel { SupplierId = supplierId };

        var (run, passedOver) = await RunPicker.LatestWithRowsAsync(_db, layerId);
        ViewData["RunNote"] = RunPicker.Note(passedOver, run);
        model.Run = run;
        if (run is null) return View(model);

        // The latest snapshot date == the latest run's snapshot (runs save a snapshot at pull time).
        var snapDate = await _db.DailySnapshots
            .Where(s => s.LayerId == layerId)
            .MaxAsync(s => (DateTime?)s.SnapshotDate);
        if (snapDate is null) return View(model);

        var bandNames = await _db.PriceBands
            .Where(b => b.LayerId == layerId)
            .ToDictionaryAsync(b => b.Id, b => b.Name);

        model.VatRatePct = await _db.Layers.Where(l => l.Id == layerId).Select(l => l.VatRatePct).FirstAsync();

        var changed =
            from p in _db.ProposedPrices
            where p.PricingRunId == run.Id && p.HasChange && p.Status != ProposalStatus.Skipped
            join s in _db.DailySnapshots.Where(d => d.LayerId == layerId && d.SnapshotDate == snapDate)
                on p.Sku equals s.Sku
            select new { p, s };

        // Supplier filter dropdown: distinct suppliers among the (unfiltered) changed movers.
        // Proposal-captured supplier for new runs; snapshot fallback covers the transition window.
        var supplierPairs = await changed
            .Select(x => new { Id = x.p.SupplierId ?? x.s.SupplierId, Name = x.p.SupplierName ?? x.s.SupplierName })
            .Where(x => x.Id != null)
            .Distinct()
            .ToListAsync();
        model.Suppliers = supplierPairs
            .Select(x => new SupplierOption(x.Id!.Value, x.Name ?? $"#{x.Id}"))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Scope BEFORE the Top-50 cut, so the lists are the top movers OF that supplier
        // (not the supplier's rows within the overall top 50).
        if (supplierId.HasValue)
            changed = changed.Where(x => (x.p.SupplierId ?? x.s.SupplierId) == supplierId.Value);

        var changedWithSales = changed
            .Select(x => new MoverRow
            {
                Sku = x.p.Sku,
                PriceBandId = x.p.PriceBandId,
                SupplierId = x.p.SupplierId ?? x.s.SupplierId,
                Supplier = x.p.SupplierName ?? x.s.SupplierName,
                OldPrice = x.p.OldPrice,
                AnchorPrice = x.p.AnchorPrice,
                CurrentPrice = x.p.CurrentPrice,
                ProposedPrice = x.p.ProposedPriceValue,
                Pptcv = x.p.Pptcv,
                ChangePct = x.p.ChangePct,
                Qty7 = x.s.Qty7,
                Qty30 = x.s.Qty30,
                Qty90 = x.s.Qty90,
                Net7 = x.s.Net7,
                Net30 = x.s.Net30,
                Net90 = x.s.Net90,
                KsStock = x.s.LocalWarehouseStock,
                SupplierStock = x.s.SupplierWarehouseStock,
                ReasonCodes = x.p.ReasonCodes,
                GuardrailFlags = x.p.GuardrailFlags,
            });

        // Rank sellers on the 30-day window (recent demand predicts better than the 90-day tail);
        // ties fall to the even fresher 7-day count.
        model.TopSellers = await changedWithSales
            .OrderByDescending(x => x.Qty30).ThenByDescending(x => x.Qty7)
            .Take(TopN).ToListAsync();

        model.DeadInStock = await changedWithSales
            .Where(x => x.Qty90 == 0 && x.KsStock > 0)
            .OrderByDescending(x => x.KsStock)
            .Take(TopN).ToListAsync();

        foreach (var r in model.TopSellers.Concat(model.DeadInStock))
            r.Band = r.PriceBandId.HasValue && bandNames.TryGetValue(r.PriceBandId.Value, out var n) ? n : "–";

        // KPI cards: profit/margin impact across the movers shown (dedup the two lists by SKU).
        var shown = model.TopSellers.Concat(model.DeadInStock).GroupBy(r => r.Sku).Select(g => g.First());
        model.Kpis = BuildWindowKpis(shown, model.VatRatePct);

        return View(model);
    }

    /// <summary>Sum each window's (currentPrice·qty, proposedPrice·qty, cost·qty) over cost-known rows
    /// (all VAT-inclusive), then defer to <see cref="KpiMath.FromSums"/> (shared with the Proposals SQL aggregate).</summary>
    private static List<WindowProfit> BuildWindowKpis(IEnumerable<MoverRow> rows, decimal vat)
    {
        decimal c7 = 0, p7 = 0, k7 = 0, c30 = 0, p30 = 0, k30 = 0, c90 = 0, p90 = 0, k90 = 0;
        foreach (var r in rows)
        {
            if (r.Pptcv is not decimal cost) continue;
            c7 += r.CurrentPrice * r.Qty7; p7 += r.ProposedPrice * r.Qty7; k7 += cost * r.Qty7;
            c30 += r.CurrentPrice * r.Qty30; p30 += r.ProposedPrice * r.Qty30; k30 += cost * r.Qty30;
            c90 += r.CurrentPrice * r.Qty90; p90 += r.ProposedPrice * r.Qty90; k90 += cost * r.Qty90;
        }
        return new List<WindowProfit>
        {
            KpiMath.FromSums(7, c7, p7, k7, vat),
            KpiMath.FromSums(30, c30, p30, k30, vat),
            KpiMath.FromSums(90, c90, p90, k90, vat),
        };
    }
}
