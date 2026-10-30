using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PricingTool.Core.Abstractions;
using PricingTool.Core.Algorithms;
using PricingTool.Core.Domain;
using PricingTool.Core.Options;
using PricingTool.Core.Services;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;
using PricingTool.Web.Models;
using PricingTool.Web.Services;

namespace PricingTool.Web.Controllers;

[Authorize(Roles = "Analyst,Manager")]
public class ProposalsController : Controller
{
    private readonly PricingToolDbContext _db;
    private readonly AuditService _audit;
    private readonly IPricePushService _pushService;
    private readonly PricingEngineOptions _options;
    private readonly CurrentLayerService _layers;

    public ProposalsController(
        PricingToolDbContext db, AuditService audit, IPricePushService pushService,
        IOptions<PricingEngineOptions> options, CurrentLayerService layers)
    {
        _db = db;
        _audit = audit;
        _pushService = pushService;
        _options = options.Value;
        _layers = layers;
    }

    /// <summary>Max number of brand suggestions rendered into the Brand filter's dropdown. Bounds page
    /// weight on runs with a long tail of near-unique brand strings; the filter is a substring match, so
    /// brands beyond the cap are still fully filterable by typing.</summary>
    private const int BrandSuggestionCap = 500;

    public async Task<IActionResult> Index([FromQuery] ProposalsFilter filter)
    {
        // Full-catalog runs are large; the default magnitude sort is index-backed, but the
        // alternate sorts (sku / price) still scan, so give the listing more headroom than 30s.
        _db.Database.SetCommandTimeout(120);
        var layer = await _layers.GetCurrentAsync();
        var layerId = await _layers.RequireCurrentIdAsync();
        var model = new ProposalsViewModel
        {
            Filter = filter,
            ConfirmationThresholdPct = _options.ChangeConfirmationThresholdPct,
            // Only a GjirafaMall run can carry Gj50-synced rows (see PricingRunOrchestrator's sync-to-Gj50).
            ShowGj50SyncFilter = string.Equals(layer?.Brand, "GjirafaMall", StringComparison.OrdinalIgnoreCase),
            Bands = await _db.PriceBands.Where(b => b.LayerId == layerId).OrderBy(b => b.SortOrder).ToListAsync(),
            AlgorithmCodes = AlgorithmCodes.All.Select(a => a.Code).ToList(),
            RecentRuns = await _db.PricingRuns.Where(r => r.LayerId == layerId).OrderByDescending(r => r.Id).Take(15).ToListAsync(),
        };

        // Only ever resolve a run within the current layer (scoped lookup, not a global Find).
        var run = filter.RunId.HasValue
            ? await _db.PricingRuns.FirstOrDefaultAsync(r => r.Id == filter.RunId.Value && r.LayerId == layerId)
            : await _db.PricingRuns.Where(r => r.LayerId == layerId && r.Status != RunStatus.Running)
                .OrderByDescending(r => r.Id).FirstOrDefaultAsync();
        if (run is null) return View(model);
        model.Run = run;
        model.Filter.RunId = run.Id;

        // Distinct suppliers captured on this run's proposals (index-backed on (PricingRunId, SupplierId)).
        var supplierRows = await _db.ProposedPrices
            .Where(p => p.PricingRunId == run.Id && p.SupplierId != null)
            .Select(p => new { p.SupplierId, p.SupplierName })
            .Distinct()
            .ToListAsync();
        model.Suppliers = supplierRows
            .Select(s => new SupplierOption(s.SupplierId!.Value, s.SupplierName ?? $"#{s.SupplierId}"))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The Brand filter's dropdown suggestions: the run's most common brands (by proposal count), capped
        // so a long tail of near-unique brand strings doesn't bloat the page with thousands of <option>s.
        // The filter itself is a substring match, so ANY brand — listed or not — can still be typed. The
        // capped set is then sorted alphabetically for the list.
        model.Brands = (await _db.ProposedPrices
            .Where(p => p.PricingRunId == run.Id && p.Brand != null && p.Brand != "")
            .GroupBy(p => p.Brand!)
            .Select(g => new { Brand = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(BrandSuggestionCap)
            .Select(x => x.Brand)
            .ToListAsync())
            .OrderBy(b => b, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The run's snapshot day, resolved up front: it backs the snapshot-backed column FILTERS (local
        // stock, 7d/30d sales) folded into the shared query, the snapshot-column SORT, and the on-screen
        // sales lookup below.
        var (snapDate, snapshotRows) = await SnapshotDayAsync(layerId);

        var query = BuildFilteredQuery(model.Filter, snapshotRows);

        model.TotalCount = await query.CountAsync();
        model.ApprovedCount = await _db.ProposedPrices
            .CountAsync(p => p.PricingRunId == run.Id && p.Status == ProposalStatus.Approved);

        // Default sort (|Δ%| desc) uses the (PricingRunId, Status, AbsChangePct) index. Snapshot-column
        // sorts need a join to the snapshot day; every other column sorts on the proposal row directly.
        if (snapDate is not null && ProposalQuery.IsSnapshotSort(filter.Sort))
            query = SortBySnapshot(query, snapshotRows, filter.Sort);
        else
            query = ProposalQuery.ApplySort(query, filter.Sort);

        model.Proposals = await query.Take(500).ToListAsync();

        // Trailing sales (7d/30d) for the SKUs on screen, from the run's snapshot day — same source
        // the Movers page and the window KPIs read. Scoped to the shown SKUs so it's one small lookup.
        if (snapDate is not null && model.Proposals.Count > 0)
        {
            var shownSkus = model.Proposals.Select(p => p.Sku).Distinct().ToList();
            model.Sales = (await _db.DailySnapshots
                .Where(s => s.LayerId == layerId && s.SnapshotDate == snapDate && shownSkus.Contains(s.Sku))
                .Select(s => new { s.Sku, s.Qty7, s.Qty30, s.LocalWarehouseStock })
                .ToListAsync())
                .GroupBy(s => s.Sku)
                .ToDictionary(g => g.Key, g => (g.First().Qty7, g.First().Qty30, g.First().LocalWarehouseStock));
        }

        model.Kpis = await BuildWindowKpisAsync(model.Filter, layerId);
        return View(model);
    }

    /// <summary>
    /// CSV export of the CURRENT filtered result set (the full set, not the 500-row screen cap). Streams
    /// directly to the response so a large run doesn't buffer in memory. Read-only; carries the same
    /// <see cref="ProposalsFilter"/> as the listing so the export matches what's on screen.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Export([FromQuery] ProposalsFilter filter)
    {
        _db.Database.SetCommandTimeout(300);
        var layerId = await _layers.RequireCurrentIdAsync();

        var run = filter.RunId.HasValue
            ? await _db.PricingRuns.FirstOrDefaultAsync(r => r.Id == filter.RunId.Value && r.LayerId == layerId)
            : await _db.PricingRuns.Where(r => r.LayerId == layerId && r.Status != RunStatus.Running)
                .OrderByDescending(r => r.Id).FirstOrDefaultAsync();
        if (run is null)
        {
            TempData["Error"] = "No run to export.";
            return RedirectToAction(nameof(Index), filter);
        }
        filter.RunId = run.Id;

        var bandNames = await _db.PriceBands.Where(b => b.LayerId == layerId)
            .ToDictionaryAsync(b => b.Id, b => b.Name);
        // Per-band margin floor % — for the computed "current price below the band's floor" column.
        var bandFloors = await _db.PriceBands.Where(b => b.LayerId == layerId)
            .ToDictionaryAsync(b => b.Id, b => b.MarginFloorPct);

        // Trailing 7d/30d unit sales from the run's snapshot day (unique per (Layer, Date, Sku)), left-joined
        // so a proposal with no snapshot row still exports (blank sales). Mirrors the on-screen columns.
        var snapDate = await _db.DailySnapshots
            .Where(s => s.LayerId == layerId)
            .MaxAsync(s => (DateTime?)s.SnapshotDate);
        var snapshot = _db.DailySnapshots.Where(s => s.LayerId == layerId && s.SnapshotDate == snapDate);
        var vat = await VatFactorAsync(layerId);

        var rows = (from p in BuildFilteredQuery(filter, snapshot)
                    join s in snapshot on p.Sku equals s.Sku into sj
                    from s in sj.DefaultIfEmpty()
                    orderby p.Sku
                    select new
                    {
                        p.ProductId, p.Sku, p.SupplierName, p.PriceBandId, p.OldPrice, p.CurrentPrice, p.AnchorPrice, p.Pptcv,
                        p.ProposedPriceValue, p.ChangePct, p.HasChange, p.Status,
                        p.ReasonCodes, p.GuardrailFlags, p.SkipReason, p.ReviewedBy, p.DeadStockStage,
                        Qty7 = (int?)s.Qty7, Qty30 = (int?)s.Qty30, LocalStock = (int?)s.LocalWarehouseStock,
                        p.Brand, p.ProductName, p.ActiveDiscountIds, p.Gtin,
                        Net30 = (decimal?)s.Net30,
                    }).AsNoTracking().AsAsyncEnumerable();

        // Tag the "normal changes only" download so it doesn't collide with the full export in Downloads.
        var capSuffix = filter.MaxAbsChangePct.HasValue
            ? $"-le{filter.MaxAbsChangePct.Value.ToString("0.##", CultureInfo.InvariantCulture)}pct"
            : "";
        var fileName = $"proposals-run{run.Id}{capSuffix}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
        Response.Headers["Content-Disposition"] = $"attachment; filename=\"{fileName}\"";
        Response.ContentType = "text/csv; charset=utf-8";

        // UTF-8 BOM so Excel reads accented SKUs/names correctly.
        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        // New columns are appended LAST so existing spreadsheet consumers keep their column positions.
        await writer.WriteLineAsync("ProductId,Sku,Supplier,Band,OldPrice,CurrentPrice,AnchorPrice,Pptcv,ProposedPrice,PrevMargin,NewMargin,ChangePct,HasChange,Status,ReasonCodes,GuardrailFlags,SkipReason,ReviewedBy,DeadStockStage,Sales7d,Sales30d,LocalStock,Brand,ProductName,ActiveDiscountIds,CurrentBelowFloor,Gtin,Revenue30d");
        await foreach (var p in rows)
        {
            var band = p.PriceBandId is int id && bandNames.TryGetValue(id, out var n) ? n : "";
            // Margins are cost-over-price: blank when cost or the price is unknown/zero.
            var prevMargin = p.Pptcv.HasValue && p.CurrentPrice > 0 ? Pct((p.CurrentPrice - p.Pptcv.Value) / p.CurrentPrice) : "";
            var newMargin = p.Pptcv.HasValue && p.ProposedPriceValue > 0 ? Pct((p.ProposedPriceValue - p.Pptcv.Value) / p.ProposedPriceValue) : "";
            var belowFloor = ReasonCodeText.IsCurrentBelowFloor(p.Pptcv, p.CurrentPrice,
                p.PriceBandId is int bid && bandFloors.TryGetValue(bid, out var fl) ? fl : (decimal?)null,
                p.GuardrailFlags, p.ReasonCodes) ? "Yes" : "";
            await writer.WriteLineAsync(string.Join(",",
                p.ProductId?.ToString(CultureInfo.InvariantCulture) ?? "", Csv(p.Sku), Csv(p.SupplierName ?? ""), Csv(band), Num(p.OldPrice), Num(p.CurrentPrice), Num(p.AnchorPrice),
                p.Pptcv.HasValue ? Num(p.Pptcv.Value) : "", Num(p.ProposedPriceValue), prevMargin, newMargin,
                p.ChangePct.ToString("0.##", CultureInfo.InvariantCulture), p.HasChange ? "1" : "0",
                p.Status.ToString(), Csv(p.ReasonCodes), Csv(p.GuardrailFlags), Csv(p.SkipReason ?? ""), Csv(p.ReviewedBy ?? ""),
                Csv(DeadStockStageText.Label(p.DeadStockStage)),
                p.Qty7?.ToString(CultureInfo.InvariantCulture) ?? "",
                p.Qty30?.ToString(CultureInfo.InvariantCulture) ?? "",
                p.LocalStock?.ToString(CultureInfo.InvariantCulture) ?? "",
                Csv(p.Brand ?? ""), Csv(p.ProductName ?? ""), DiscountIds(p.ActiveDiscountIds), belowFloor,
                // Lets a reviewer who filtered to GTIN_PARITY_MATCHED sort by barcode and see each
                // matched pair side by side.
                Csv(p.Gtin ?? ""),
                Revenue30d(p.Net30, vat)));
        }
        await writer.FlushAsync();
        return new EmptyResult();
    }

    /// <summary>
    /// "Export all" — the same full filtered result set as <see cref="Export"/>, but shaped for the
    /// planning spreadsheet: columns reordered (sales/stock beside the SKU, prices grouped), a computed
    /// "Discount Displayed" column (the storefront strike-through off OldPrice), and a value-weighted
    /// TOTALS row written ABOVE the header. Percentages carry a % sign so the file renders like the
    /// reference sheet when opened / re-imported. Deliberately a SEPARATE action from <see cref="Export"/>
    /// so consumers that read the original column layout keep working.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ExportAll([FromQuery] ProposalsFilter filter)
    {
        _db.Database.SetCommandTimeout(300);
        var layerId = await _layers.RequireCurrentIdAsync();

        var run = filter.RunId.HasValue
            ? await _db.PricingRuns.FirstOrDefaultAsync(r => r.Id == filter.RunId.Value && r.LayerId == layerId)
            : await _db.PricingRuns.Where(r => r.LayerId == layerId && r.Status != RunStatus.Running)
                .OrderByDescending(r => r.Id).FirstOrDefaultAsync();
        if (run is null)
        {
            TempData["Error"] = "No run to export.";
            return RedirectToAction(nameof(Index), filter);
        }
        filter.RunId = run.Id;

        var bandNames = await _db.PriceBands.Where(b => b.LayerId == layerId)
            .ToDictionaryAsync(b => b.Id, b => b.Name);
        // Per-band margin floor % — for the computed "current price below the band's floor" column.
        var bandFloors = await _db.PriceBands.Where(b => b.LayerId == layerId)
            .ToDictionaryAsync(b => b.Id, b => b.MarginFloorPct);

        var snapDate = await _db.DailySnapshots
            .Where(s => s.LayerId == layerId)
            .MaxAsync(s => (DateTime?)s.SnapshotDate);
        var snapshot = _db.DailySnapshots.Where(s => s.LayerId == layerId && s.SnapshotDate == snapDate);
        var vat = await VatFactorAsync(layerId);

        // Value-weighted TOTALS, computed up front (two aggregate round-trips) so the summary row can be
        // written ABOVE the data without buffering the whole result set in memory. Money sums come off the
        // proposals; the trailing-sales / stock sums come off the same snapshot join used per row.
        var money = await BuildFilteredQuery(filter, snapshot).GroupBy(_ => 1).Select(g => new
        {
            OldPrice = g.Sum(p => p.OldPrice),
            AnchorPrice = g.Sum(p => p.AnchorPrice),
            Pptcv = g.Sum(p => p.Pptcv),                 // decimal? — SUM skips cost-unknown rows
            CurrentPrice = g.Sum(p => p.CurrentPrice),
            ProposedPrice = g.Sum(p => p.ProposedPriceValue),
        }).FirstOrDefaultAsync();

        var sales = await (from p in BuildFilteredQuery(filter, snapshot)
                           join s in snapshot on p.Sku equals s.Sku
                           group s by 1 into g
                           select new
                           {
                               Sales7 = g.Sum(x => x.Qty7),
                               Sales30 = g.Sum(x => x.Qty30),
                               LocalStock = g.Sum(x => x.LocalWarehouseStock),
                               Net30 = g.Sum(x => x.Net30),
                           }).FirstOrDefaultAsync();

        var rows = (from p in BuildFilteredQuery(filter, snapshot)
                    join s in snapshot on p.Sku equals s.Sku into sj
                    from s in sj.DefaultIfEmpty()
                    orderby p.Sku
                    select new
                    {
                        p.ProductId, p.Sku, p.SupplierName, p.PriceBandId,
                        p.OldPrice, p.AnchorPrice, p.Pptcv, p.CurrentPrice, p.ProposedPriceValue,
                        p.ChangePct, p.ReasonCodes, p.GuardrailFlags,
                        Qty7 = (int?)s.Qty7, Qty30 = (int?)s.Qty30, LocalStock = (int?)s.LocalWarehouseStock,
                        p.Brand, p.ProductName, p.ActiveDiscountIds,
                        Net30 = (decimal?)s.Net30,
                    }).AsNoTracking().AsAsyncEnumerable();

        var fileName = $"proposals-all-run{run.Id}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
        Response.Headers["Content-Disposition"] = $"attachment; filename=\"{fileName}\"";
        Response.ContentType = "text/csv; charset=utf-8";

        // UTF-8 BOM so Excel reads accented SKUs/names correctly.
        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        decimal? sumPptcv = money?.Pptcv;
        decimal sumOld = money?.OldPrice ?? 0m, sumAnchor = money?.AnchorPrice ?? 0m;
        decimal sumCur = money?.CurrentPrice ?? 0m, sumProp = money?.ProposedPrice ?? 0m;

        // Totals row: blank identity columns, SUMs for qty/money, value-weighted ratios for the percentages.
        await writer.WriteLineAsync(string.Join(",",
            "", "", "", "",
            (sales?.Sales7 ?? 0).ToString(CultureInfo.InvariantCulture),
            (sales?.Sales30 ?? 0).ToString(CultureInfo.InvariantCulture),
            (sales?.LocalStock ?? 0).ToString(CultureInfo.InvariantCulture),
            Whole(sumOld), Whole(sumAnchor), sumPptcv.HasValue ? Whole(sumPptcv.Value) : "",
            Whole(sumCur), Whole(sumProp),
            sumPptcv.HasValue && sumCur > 0 ? Pct((sumCur - sumPptcv.Value) / sumCur) + "%" : "",
            sumPptcv.HasValue && sumProp > 0 ? Pct((sumProp - sumPptcv.Value) / sumProp) + "%" : "",
            sumCur > 0 ? Pct((sumProp - sumCur) / sumCur) + "%" : "",
            sumOld > 0 ? Pct((sumOld - sumProp) / sumOld) + "%" : "",
            "", "", "", "", "",
            Whole((sales?.Net30 ?? 0m) * vat)));

        await writer.WriteLineAsync("ProductId,Sku,Supplier,Band,Sales7d,Sales30d,LocalStock,OldPrice,AnchorPrice,Pptcv,CurrentPrice,ProposedPrice,PrevMargin,NewMargin,ChangePct,Discount Displayed,ReasonCodes,Brand,ProductName,ActiveDiscountIds,CurrentBelowFloor,Revenue30d");
        await foreach (var p in rows)
        {
            var band = p.PriceBandId is int id && bandNames.TryGetValue(id, out var n) ? n : "";
            // Margins are cost-over-price: blank when cost or the price is unknown/zero.
            var prevMargin = p.Pptcv.HasValue && p.CurrentPrice > 0 ? Pct((p.CurrentPrice - p.Pptcv.Value) / p.CurrentPrice) + "%" : "";
            var newMargin = p.Pptcv.HasValue && p.ProposedPriceValue > 0 ? Pct((p.ProposedPriceValue - p.Pptcv.Value) / p.ProposedPriceValue) + "%" : "";
            var belowFloor = ReasonCodeText.IsCurrentBelowFloor(p.Pptcv, p.CurrentPrice,
                p.PriceBandId is int bid && bandFloors.TryGetValue(bid, out var fl) ? fl : (decimal?)null,
                p.GuardrailFlags, p.ReasonCodes) ? "Yes" : "";
            // Storefront discount = strike-through off the display/shelf price (OldPrice), whole percent.
            var discount = p.OldPrice > 0
                ? Math.Round((p.OldPrice - p.ProposedPriceValue) / p.OldPrice * 100m, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%"
                : "";
            await writer.WriteLineAsync(string.Join(",",
                p.ProductId?.ToString(CultureInfo.InvariantCulture) ?? "", Csv(p.Sku), Csv(p.SupplierName ?? ""), Csv(band),
                p.Qty7?.ToString(CultureInfo.InvariantCulture) ?? "",
                p.Qty30?.ToString(CultureInfo.InvariantCulture) ?? "",
                p.LocalStock?.ToString(CultureInfo.InvariantCulture) ?? "",
                Num(p.OldPrice), Num(p.AnchorPrice), p.Pptcv.HasValue ? Num(p.Pptcv.Value) : "",
                Num(p.CurrentPrice), Num(p.ProposedPriceValue),
                prevMargin, newMargin,
                p.ChangePct.ToString("0.00", CultureInfo.InvariantCulture) + "%",
                discount,
                Csv(p.ReasonCodes),
                Csv(p.Brand ?? ""), Csv(p.ProductName ?? ""), DiscountIds(p.ActiveDiscountIds), belowFloor,
                Revenue30d(p.Net30, vat)));
        }
        await writer.FlushAsync();
        return new EmptyResult();
    }

    private static string Num(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>1 + the layer's VAT rate, to put the snapshot's VAT-exclusive sales revenue on the same
    /// VAT-inclusive footing as every other money column in the exports.</summary>
    private async Task<decimal> VatFactorAsync(int layerId) =>
        1m + await _db.Layers.Where(l => l.Id == layerId).Select(l => l.VatRatePct).FirstOrDefaultAsync() / 100m;

    /// <summary>What the last 30 days' units actually sold for (the snapshot's Net30, both storefronts of the
    /// country), VAT-inclusive like the price and Pptcv columns — so (ΣRevenue30d − ΣSales30d × Pptcv) ÷
    /// ΣRevenue30d is the realized margin the sales report and the Price Bands "30d margin" show, where
    /// Sales30d × CurrentPrice would price last month's units at today's price. Blank with no snapshot row.</summary>
    private static string Revenue30d(decimal? net30, decimal vat) => net30.HasValue ? Num(net30.Value * vat) : "";

    /// <summary>Totals-row money cell: rounded to whole units, no thousands separators (a comma would
    /// break the CSV field — the spreadsheet applies its own grouping on import).</summary>
    private static string Whole(decimal value) => Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>Margin fraction (0..1) as a one-decimal percentage, e.g. 0.227 → "22.7".</summary>
    private static string Pct(decimal fraction) => (fraction * 100).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Discount-id list for CSV. The ids are stored comma-joined ("201,362,12"), but a quoted
    /// comma field is auto-parsed by Excel as a single number with thousands separators (→ 20136212).
    /// Emit them space-separated so the spreadsheet keeps them as a plain text list (spaces are never a
    /// field delimiter or a number-grouping character in any locale, unlike ',' or ';').</summary>
    private static string DiscountIds(string? value) =>
        string.IsNullOrEmpty(value) ? "" : value.Replace(",", " ");

    /// <summary>RFC-4180 CSV field: quote if it contains comma/quote/newline; double embedded quotes.
    /// Also neutralises a leading =/+/-/@ so spreadsheets don't treat the cell as a formula.</summary>
    private static string Csv(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var s = value;
        if (s.Length > 0 && (s[0] is '=' or '+' or '-' or '@')) s = "'" + s;
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            s = "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    // Constant-elasticity demand extrapolation is only locally valid; a steep coefficient on a deep cut
    // (e.g. E=−3 on −30% ⇒ ~2.9× units) is not real. Clamp the per-SKU demand multiplier to a sane band.
    private const double MinDemandMultiplier = 0.2;  // at most an 80% volume drop on a price rise
    private const double MaxDemandMultiplier = 5.0;  // at most a 5× volume lift on a price cut

    /// <summary>
    /// Profit/margin impact (now→proposed) over 7d/30d/90d for the CURRENT filtered view. The naive
    /// baseline holds units constant and is aggregated in one SQL round-trip (cost-known SKUs only), then
    /// deferred to <see cref="KpiMath.FromSums"/> (shared with Movers). On top of that, for confidently-
    /// elastic, non-dead-stock SKUs we layer an elasticity-aware projection that scales the proposed-side
    /// units by the fitted demand response q·(P_proposed/P_current)^E — the volume the markdown was meant
    /// to buy. The elastic subset is materialised (the demand-response math and the confidence gate don't
    /// translate to set-based SQL), but it's only the confidently-elastic rows, not the whole catalogue.
    /// </summary>
    private async Task<List<ProposalWindowKpi>> BuildWindowKpisAsync(ProposalsFilter filter, int layerId)
    {
        var snapDate = await _db.DailySnapshots
            .Where(s => s.LayerId == layerId)
            .MaxAsync(s => (DateTime?)s.SnapshotDate);
        if (snapDate is null) return new();

        var vat = await _db.Layers.Where(l => l.Id == layerId).Select(l => l.VatRatePct).FirstAsync();

        var snapshot = _db.DailySnapshots.Where(d => d.LayerId == layerId && d.SnapshotDate == snapDate);

        var costed =
            from p in BuildFilteredQuery(filter, snapshot)
            where p.Pptcv != null
            join s in snapshot on p.Sku equals s.Sku
            select new { p.CurrentPrice, Prop = p.ProposedPriceValue, Cost = p.Pptcv!.Value, s.Qty7, s.Qty30, s.Qty90 };

        var agg = await costed.GroupBy(_ => 1).Select(g => new
        {
            C7 = g.Sum(x => x.CurrentPrice * x.Qty7), P7 = g.Sum(x => x.Prop * x.Qty7), K7 = g.Sum(x => x.Cost * x.Qty7),
            C30 = g.Sum(x => x.CurrentPrice * x.Qty30), P30 = g.Sum(x => x.Prop * x.Qty30), K30 = g.Sum(x => x.Cost * x.Qty30),
            C90 = g.Sum(x => x.CurrentPrice * x.Qty90), P90 = g.Sum(x => x.Prop * x.Qty90), K90 = g.Sum(x => x.Cost * x.Qty90),
        }).FirstOrDefaultAsync();

        if (agg is null) return new();

        var naive = new[]
        {
            KpiMath.FromSums(7, agg.C7, agg.P7, agg.K7, vat),
            KpiMath.FromSums(30, agg.C30, agg.P30, agg.K30, vat),
            KpiMath.FromSums(90, agg.C90, agg.P90, agg.K90, vat),
        };

        // Confidently-elastic, non-dead-stock subset, read from the elasticity persisted ON the proposal
        // (the exact value the run's elastic vote acted on — SkuElasticity is overwritten every fit).
        var elasticRows = await
            (from p in BuildFilteredQuery(filter, snapshot)
             where p.Pptcv != null && p.Elasticity != null
                   && !p.ReasonCodes.Contains("DEAD_STOCK") && !p.GuardrailFlags.Contains("DEAD_STOCK")
             join s in snapshot on p.Sku equals s.Sku
             select new
             {
                 Cur = p.CurrentPrice, Prop = p.ProposedPriceValue, Cost = p.Pptcv!.Value,
                 E = p.Elasticity!.Value, Se = p.ElasticityStdError, s.Qty7, s.Qty30, s.Qty90,
             }).AsNoTracking().ToListAsync();

        // Fold the demand response per window (index 0=7d, 1=30d, 2=90d). All sums VAT-inclusive (raw).
        var proposedDelta = new decimal[3];
        var costDelta = new decimal[3];
        var qtySum = new decimal[3];
        var qtyAdj = new decimal[3];
        var curMargin = new decimal[3];
        var propMargin = new decimal[3];
        var elasticCount = 0;

        foreach (var r in elasticRows)
        {
            if (r.Cur <= 0 || r.Prop <= 0) continue;
            var se = r.Se is decimal s ? (double)s : double.NaN;
            // Defensive re-check of the confidence gate, independent of the run-time persistence policy.
            if (!ElasticityGate.IsConfidentlyElastic((double)r.E, se)) continue;

            elasticCount++;
            // Demand response to the actual price move: q' = q·(P_proposed / P_current)^E.
            var mult = (decimal)Math.Clamp(
                Math.Pow((double)(r.Prop / r.Cur), (double)r.E),
                MinDemandMultiplier, MaxDemandMultiplier);

            var qtys = new decimal[] { r.Qty7, r.Qty30, r.Qty90 };
            for (var i = 0; i < 3; i++)
            {
                var q = qtys[i];
                proposedDelta[i] += r.Prop * q * (mult - 1m);
                costDelta[i] += r.Cost * q * (mult - 1m);
                qtySum[i] += q;
                qtyAdj[i] += q * mult;
                curMargin[i] += (r.Cur - r.Cost) * q;
                propMargin[i] += (r.Prop - r.Cost) * q;
            }
        }

        var days = new[] { 7, 30, 90 };
        var result = new List<ProposalWindowKpi>(3);
        for (var i = 0; i < 3; i++)
        {
            var elastic = KpiMath.ElasticProjectionFromSums(
                days[i], naive[i], vat,
                proposedDelta[i], costDelta[i], qtySum[i], qtyAdj[i],
                curMargin[i], propMargin[i], elasticCount);
            result.Add(new ProposalWindowKpi(naive[i], elastic));
        }
        return result;
    }

    // Row-level filtering lives in ProposalQuery.Apply (pure + unit-tested); the snapshot-backed column
    // ranges (local stock, 7d/30d sales) ride ProposalQuery.ApplySnapshotFilters over the run's snapshot
    // day. Shared by the listing, both CSV exports, "Approve all (filtered)", and the window KPIs, so every
    // one of them honors the exact same column filters. Callers pass the snapshot day from SnapshotDayAsync.
    private IQueryable<ProposedPrice> BuildFilteredQuery(ProposalsFilter filter, IQueryable<DailySnapshot> snapshot) =>
        ProposalQuery.ApplySnapshotFilters(ProposalQuery.Apply(_db.ProposedPrices, filter), snapshot, filter);

    /// <summary>The current layer's latest snapshot-day rows, scoped by (Layer, Date) — the source for both
    /// the snapshot-backed column filters (folded into <see cref="BuildFilteredQuery"/>) and the snapshot
    /// sort/join. Returns the resolved date too (null when the layer has no snapshots yet, in which case the
    /// rows queryable is empty and any snapshot bound simply matches nothing).</summary>
    private async Task<(DateTime? Date, IQueryable<DailySnapshot> Rows)> SnapshotDayAsync(int layerId)
    {
        var date = await _db.DailySnapshots.Where(s => s.LayerId == layerId).MaxAsync(s => (DateTime?)s.SnapshotDate);
        return (date, _db.DailySnapshots.Where(s => s.LayerId == layerId && s.SnapshotDate == date));
    }

    /// <summary>Sorts by a DailySnapshots-backed column (7d/30d sales, local stock) via a left join to
    /// the run's snapshot day. Rows with no snapshot row sort as null (last on desc). Listing-only —
    /// the shared query never joins the snapshot, so Export/ApproveAll stay on the fast row-level path.</summary>
    private static IQueryable<ProposedPrice> SortBySnapshot(
        IQueryable<ProposedPrice> query, IQueryable<DailySnapshot> snapshot, string? sort)
    {
        var (col, desc) = ProposalQuery.ParseSort(sort);
        var joined = from p in query
                     join s in snapshot on p.Sku equals s.Sku into sj
                     from s in sj.DefaultIfEmpty()
                     select new { p, s };
        var ordered = col switch
        {
            "qty7" => desc ? joined.OrderByDescending(x => (int?)x.s.Qty7) : joined.OrderBy(x => (int?)x.s.Qty7),
            "qty30" => desc ? joined.OrderByDescending(x => (int?)x.s.Qty30) : joined.OrderBy(x => (int?)x.s.Qty30),
            _ => desc ? joined.OrderByDescending(x => (int?)x.s.LocalWarehouseStock)
                      : joined.OrderBy(x => (int?)x.s.LocalWarehouseStock),
        };
        return ordered.Select(x => x.p);
    }

    // ---- Review actions (Manager only) ------------------------------------

    /// <summary>Guards write actions so a crafted runId from another layer can't be mutated here.</summary>
    private async Task<bool> RunBelongsToCurrentLayerAsync(long runId)
    {
        var layerId = await _layers.RequireCurrentIdAsync();
        return await _db.PricingRuns.AnyAsync(r => r.Id == runId && r.LayerId == layerId);
    }

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(long runId, long[] ids, bool confirmLarge, [FromForm] ProposalsFilter filter)
    {
        if (!await RunBelongsToCurrentLayerAsync(runId)) return NotFound();
        if (ids.Length == 0)
        {
            TempData["Error"] = "No proposals selected.";
            return RedirectToFiltered(runId, filter);
        }

        var target = _db.ProposedPrices
            .Where(p => p.PricingRunId == runId && ids.Contains(p.Id) && p.Status == ProposalStatus.Pending);
        return await ApproveCore(target, runId, confirmLarge, filter);
    }

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveAll(long runId, bool confirmLarge, [FromForm] ProposalsFilter filter)
    {
        if (!await RunBelongsToCurrentLayerAsync(runId)) return NotFound();
        filter.RunId = runId;
        filter.Status = "Pending";
        // Honors the snapshot-backed column filters (local stock, 7d/30d sales) exactly as the listing does.
        var (_, snapshot) = await SnapshotDayAsync(await _layers.RequireCurrentIdAsync());
        var target = BuildFilteredQuery(filter, snapshot);
        return await ApproveCore(target, runId, confirmLarge, filter);
    }

    /// <summary>
    /// Approves the <paramref name="target"/> set (which already restricts to this run's Pending proposals),
    /// SET-BASED: one <c>ExecuteUpdate</c> flips the statuses, then the Approval + its items are recorded from
    /// the rows just claimed via a server-side <c>INSERT…SELECT</c>. This keeps even a full-catalog (100k+)
    /// approve to a few seconds — so it never trips the ~100s gateway timeout the row-by-row EF version did —
    /// and the atomic claim (only still-Pending rows flip; a concurrent/retried request claims 0) means a run
    /// can't pick up a duplicate Approval from a double-click. The stamped <c>ReviewedUtc == now</c> marks
    /// exactly the rows THIS request approved, so the recording never has to round-trip them to the app.
    /// </summary>
    private async Task<IActionResult> ApproveCore(
        IQueryable<ProposedPrice> target, long runId, bool confirmLarge, ProposalsFilter filter)
    {
        var threshold = _options.ChangeConfirmationThresholdPct;

        // Large-change gate (default ±20%), checked BEFORE the claim so a refusal writes nothing.
        if (!confirmLarge)
        {
            var largeCount = await target.CountAsync(p => p.AbsChangePct > threshold);
            if (largeCount > 0)
            {
                TempData["Error"] =
                    $"{largeCount} proposal(s) exceed ±{threshold:0.#}% — tick “confirm large changes” to approve them.";
                return RedirectToFiltered(runId, filter);
            }
        }

        var user = User.Identity?.Name ?? "unknown";
        var layerId = await _layers.RequireCurrentIdAsync();
        _db.Database.SetCommandTimeout(300); // headroom for a full-catalog set-based claim/insert

        // The context uses EnableRetryOnFailure, so a manual transaction must run INSIDE the execution
        // strategy (which owns the retry) — the whole claim+record is one retriable, atomic unit.
        var strategy = _db.Database.CreateExecutionStrategy();
        var (claimed, sample) = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear(); // drop any Approval added by a prior rolled-back retry attempt
            var now = DateTime.UtcNow;

            await using var tx = await _db.Database.BeginTransactionAsync();

            // Atomically CLAIM the pending rows: only rows still Pending flip, so a duplicate/retry request
            // racing this one claims nothing. One set-based UPDATE — no per-row EF round-trips.
            var n = await target.ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, ProposalStatus.Approved)
                .SetProperty(p => p.ReviewedBy, user)
                .SetProperty(p => p.ReviewedUtc, now));

            if (n == 0)
            {
                await tx.RollbackAsync();
                return (0, (List<string>?)null);
            }

            // The rows just claimed, identified by our unique (run, reviewer, timestamp) marker.
            var mine = _db.ProposedPrices.Where(p =>
                p.PricingRunId == runId && p.ReviewedBy == user && p.ReviewedUtc == now);

            var counts = await mine.GroupBy(_ => 1).Select(g => new
            {
                Up = g.Count(p => p.ProposedPriceValue > p.CurrentPrice),
                Down = g.Count(p => p.ProposedPriceValue < p.CurrentPrice),
            }).FirstAsync();

            // Record the approval EVENT (the tool's record of what went live; prices are applied out-of-band),
            // so the dashboard can track realized profit before vs after. Profit fills on the hourly refresh.
            var approval = new Approval
            {
                LayerId = layerId,
                ApprovedUtc = now,
                SourceRunId = runId,
                ApprovedBy = user,
                SkuCount = n,
                IncreaseCount = counts.Up,
                DecreaseCount = counts.Down,
                CreatedUtc = now,
            };
            _db.Approvals.Add(approval);
            await _db.SaveChangesAsync();

            // Items via a server-side INSERT…SELECT from the claimed rows — never round-trips 100k rows.
            // Tables are in the [PricingTool] default schema (PricingToolDbContext.HasDefaultSchema).
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO [PricingTool].[ApprovalItems] (ApprovalId, Sku, OldPrice, NewPrice)
SELECT {approval.Id}, p.Sku, p.CurrentPrice, p.ProposedPriceValue
FROM [PricingTool].[ProposedPrices] p
WHERE p.PricingRunId = {runId} AND p.ReviewedBy = {user} AND p.ReviewedUtc = {now}");

            var skus = await mine.OrderBy(p => p.Sku).Take(50).Select(p => p.Sku).ToListAsync();
            await tx.CommitAsync();
            return (n, (List<string>?)skus);
        });

        if (claimed == 0)
        {
            TempData["Error"] = "Nothing to approve in the current selection.";
            return RedirectToFiltered(runId, filter);
        }

        await _audit.LogAsync(user, AuditCategories.Review,
            $"Approved {claimed} proposal(s)", nameof(PricingRun), runId.ToString(),
            newValue: string.Join(",", sample!),
            layerId: layerId);

        TempData["Message"] = $"Approved {claimed} proposal(s).";
        return RedirectToFiltered(runId, filter);
    }

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(long runId, long[] ids, [FromForm] ProposalsFilter filter)
    {
        if (!await RunBelongsToCurrentLayerAsync(runId)) return NotFound();
        var user = User.Identity?.Name ?? "unknown";
        var proposals = await _db.ProposedPrices
            .Where(p => p.PricingRunId == runId && ids.Contains(p.Id) &&
                        (p.Status == ProposalStatus.Pending || p.Status == ProposalStatus.Approved))
            .ToListAsync();

        foreach (var p in proposals)
        {
            p.Status = ProposalStatus.Rejected;
            p.ReviewedBy = user;
            p.ReviewedUtc = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();

        await _audit.LogAsync(user, AuditCategories.Review,
            $"Rejected {proposals.Count} proposal(s)", nameof(PricingRun), runId.ToString(),
            newValue: string.Join(",", proposals.Take(50).Select(p => p.Sku)),
            layerId: await _layers.RequireCurrentIdAsync());

        TempData["Message"] = $"Rejected {proposals.Count} proposal(s).";
        return RedirectToFiltered(runId, filter);
    }

    /// <summary>
    /// The explicit, human-triggered push step: hands every Approved proposal of the run to the
    /// IPricePushService integration point (v1: CSV export) and marks them Pushed. This is the
    /// ONLY path that leaves the tool — the engine itself never touches live prices.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Push(long runId, [FromForm] ProposalsFilter filter)
    {
        if (!await RunBelongsToCurrentLayerAsync(runId)) return NotFound();
        var user = User.Identity?.Name ?? "unknown";
        var approved = await _db.ProposedPrices
            .Where(p => p.PricingRunId == runId && p.Status == ProposalStatus.Approved)
            .ToListAsync();

        if (approved.Count == 0)
        {
            TempData["Error"] = "No approved proposals to push.";
            return RedirectToFiltered(runId, filter);
        }

        var payload = approved
            .Select(p => new ApprovedPrice(p.Id, p.Sku, p.OldPrice, p.CurrentPrice, p.ProposedPriceValue, runId, p.ReviewedBy ?? user))
            .ToList();

        var result = await _pushService.PushAsync(payload);
        if (!result.Success)
        {
            TempData["Error"] = $"Push failed: {result.Detail}";
            return RedirectToFiltered(runId, filter);
        }

        foreach (var p in approved)
        {
            p.Status = ProposalStatus.Pushed;
            p.PushedUtc = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();

        var pushLayerId = await _layers.RequireCurrentIdAsync();
        foreach (var p in approved)
        {
            await _audit.LogAsync(user, AuditCategories.Push,
                $"Pushed price for {p.Sku}", nameof(ProposedPrice), p.Id.ToString(),
                oldValue: p.CurrentPrice.ToString("0.00"),
                newValue: p.ProposedPriceValue.ToString("0.00"),
                layerId: pushLayerId);
        }

        TempData["Message"] = $"Pushed {approved.Count} price(s). {result.Detail}";
        return RedirectToFiltered(runId, filter);
    }

    /// <summary>
    /// Manual price override from the Proposals grid: writes the manager's value into
    /// <see cref="ProposedPrice.ProposedPriceValue"/> (so Push/Export/margins/KPIs all use it),
    /// preserving the engine's original in <see cref="ProposedPrice.EngineProposedPrice"/>, and recomputes
    /// Δ%/HasChange so the ±20% approval gate and the "changed only" filters still hold. An already-approved
    /// row drops back to Pending — the amount changed, so it must be re-approved before it can be pushed.
    /// Returns JSON so the grid can refresh the row in place (no reload).
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OverridePrice(long runId, long id, decimal price)
    {
        if (!await RunBelongsToCurrentLayerAsync(runId)) return NotFound();
        if (price <= 0) return BadRequest(new { error = "Price must be greater than zero." });

        var p = await _db.ProposedPrices.FirstOrDefaultAsync(x => x.Id == id && x.PricingRunId == runId);
        if (p is null) return NotFound();
        if (p.Status is not (ProposalStatus.Pending or ProposalStatus.Approved))
            return BadRequest(new { error = $"Can't override a {p.Status} proposal." });

        var (proposed, changePct, hasChange) = ProposalOverride.Recompute(p.CurrentPrice, price);
        var oldValue = p.ProposedPriceValue;

        p.EngineProposedPrice ??= p.ProposedPriceValue;   // preserve the engine's original once
        p.ProposedPriceValue = proposed;
        p.ChangePct = changePct;
        p.HasChange = hasChange;

        var user = User.Identity?.Name ?? "unknown";
        p.ManualOverrideBy = user;
        p.ManualOverrideUtc = DateTime.UtcNow;
        if (p.Status == ProposalStatus.Approved)
        {
            p.Status = ProposalStatus.Pending;
            p.ReviewedBy = null;
            p.ReviewedUtc = null;
        }

        await _db.SaveChangesAsync();

        var layerId = await _layers.RequireCurrentIdAsync();
        await _audit.LogAsync(user, AuditCategories.Review,
            $"Overrode proposed price for {p.Sku}", nameof(ProposedPrice), p.Id.ToString(),
            oldValue: oldValue.ToString("0.00", CultureInfo.InvariantCulture),
            newValue: proposed.ToString("0.00", CultureInfo.InvariantCulture),
            layerId: layerId);

        decimal? newMargin = p.Pptcv.HasValue && proposed > 0 ? (proposed - p.Pptcv.Value) / proposed : null;
        return Json(new
        {
            proposedPrice = proposed,
            changePct,
            newMarginPct = newMargin.HasValue ? (decimal?)Math.Round(newMargin.Value * 100m, 1) : null,
            belowCost = p.Pptcv.HasValue && proposed < p.Pptcv.Value,
            enginePrice = p.EngineProposedPrice,
            status = p.Status.ToString(),
        });
    }

    private IActionResult RedirectToFiltered(long runId, ProposalsFilter filter)
    {
        filter.RunId = runId;
        return RedirectToAction(nameof(Index), filter.ToRouteValues());
    }
}
