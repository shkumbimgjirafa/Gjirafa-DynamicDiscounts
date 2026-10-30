using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PricingTool.Core.Algorithms;
using PricingTool.Core.Domain;
using PricingTool.Core.Services;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;
using PricingTool.Web.Models;
using PricingTool.Web.Services;

namespace PricingTool.Web.Controllers;

[Authorize(Roles = "Analyst,Manager")]
public class BandsController : Controller
{
    private readonly PricingToolDbContext _db;
    private readonly AuditService _audit;
    private readonly CurrentLayerService _layers;
    private readonly BandFloorMirrorService _floorMirror;
    private readonly BandMatchService _bandMatch;
    private readonly DiscountOptimizationImpactService _impact;

    public BandsController(
        PricingToolDbContext db, AuditService audit, CurrentLayerService layers, BandFloorMirrorService floorMirror,
        BandMatchService bandMatch, DiscountOptimizationImpactService impact)
    {
        _db = db;
        _audit = audit;
        _layers = layers;
        _floorMirror = floorMirror;
        _bandMatch = bandMatch;
        _impact = impact;
    }

    public async Task<IActionResult> Index()
    {
        var layerId = await _layers.RequireCurrentIdAsync();
        var bands = await _db.PriceBands
            .Where(b => b.LayerId == layerId)
            .Include(b => b.AlgorithmSettings)
            .OrderBy(b => b.SortOrder)
            .ToListAsync();

        var vm = new BandsIndexViewModel { Bands = bands };

        // Actual band-blended margins from the most recent completed run for this layer. Pricing % uses the
        // anchor (FinalPrice) — the same basis the margin-target guardrail now trims on; Current % uses the
        // live price. Both aggregate over priced SKUs with a known cost (skipped/no-cost rows excluded).
        var run = await _db.PricingRuns
            .Where(r => r.LayerId == layerId && r.Status != RunStatus.Running)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync();
        if (run is not null)
        {
            vm.RunId = run.Id;
            vm.RunStartedUtc = run.StartedUtc;

            var agg = await _db.ProposedPrices
                .Where(p => p.PricingRunId == run.Id && p.PriceBandId != null
                            && p.Pptcv != null && p.Status != ProposalStatus.Skipped)
                .GroupBy(p => p.PriceBandId!.Value)
                .Select(g => new
                {
                    BandId = g.Key,
                    SumCost = g.Sum(x => x.Pptcv!.Value),
                    SumAnchor = g.Sum(x => x.AnchorPrice),
                    SumCurrent = g.Sum(x => x.CurrentPrice),
                })
                .ToListAsync();

            decimal totCost = 0m, totAnchor = 0m, totCurrent = 0m;
            foreach (var a in agg)
            {
                vm.Margins[a.BandId] = new BandActualMargins(
                    a.SumAnchor > 0m ? (a.SumAnchor - a.SumCost) / a.SumAnchor : null,
                    a.SumCurrent > 0m ? (a.SumCurrent - a.SumCost) / a.SumCurrent : null);
                totCost += a.SumCost;
                totAnchor += a.SumAnchor;
                totCurrent += a.SumCurrent;
            }
            vm.TotalPricingPct = totAnchor > 0m ? (totAnchor - totCost) / totAnchor : null;
            vm.TotalCurrentPct = totCurrent > 0m ? (totCurrent - totCost) / totCurrent : null;

            // Anchor-revenue-weighted blends of the per-band Target margin / Margin floor, so the totals row
            // sits on the same footing as the blended Pricing %. Stored as fractions (config values are percents).
            var anchorByBand = agg.ToDictionary(a => a.BandId, a => a.SumAnchor);
            decimal tWeighted = 0m, tWeight = 0m, fWeighted = 0m, fWeight = 0m;
            foreach (var band in bands)
            {
                if (!anchorByBand.TryGetValue(band.Id, out var w) || w <= 0m) continue;
                fWeighted += band.MarginFloorPct / 100m * w;
                fWeight += w;
                if (band.TargetMarginPct is decimal tgt)
                {
                    tWeighted += tgt / 100m * w;
                    tWeight += w;
                }
            }
            vm.TotalTargetPct = tWeight > 0m ? tWeighted / tWeight : null;
            vm.TotalFloorPct = fWeight > 0m ? fWeighted / fWeight : null;

            // Dead-stock cut of the same run: per-band SKU counts by age stage plus the blended
            // margins over those SKUs. The blended floor weights each stage's per-band floor pct by
            // that stage's cost mass, so the figure matches what the guardrail would actually allow.
            var deadAgg = await _db.ProposedPrices
                .Where(p => p.PricingRunId == run.Id && p.PriceBandId != null
                            && p.Pptcv != null && p.Status != ProposalStatus.Skipped
                            && p.DeadStockStage != DeadStockStage.None)
                .GroupBy(p => p.PriceBandId!.Value)
                .Select(g => new
                {
                    BandId = g.Key,
                    Count = g.Count(),
                    S1 = g.Count(x => x.DeadStockStage == DeadStockStage.Stage1),
                    S2 = g.Count(x => x.DeadStockStage == DeadStockStage.Stage2),
                    S3 = g.Count(x => x.DeadStockStage == DeadStockStage.Stage3),
                    S4 = g.Count(x => x.DeadStockStage == DeadStockStage.Stage4),
                    SumCost = g.Sum(x => x.Pptcv!.Value),
                    CostS1 = g.Sum(x => x.DeadStockStage == DeadStockStage.Stage1 ? x.Pptcv!.Value : 0m),
                    CostS2 = g.Sum(x => x.DeadStockStage == DeadStockStage.Stage2 ? x.Pptcv!.Value : 0m),
                    CostS3 = g.Sum(x => x.DeadStockStage == DeadStockStage.Stage3 ? x.Pptcv!.Value : 0m),
                    CostS4 = g.Sum(x => x.DeadStockStage == DeadStockStage.Stage4 ? x.Pptcv!.Value : 0m),
                    SumAnchor = g.Sum(x => x.AnchorPrice),
                    SumCurrent = g.Sum(x => x.CurrentPrice),
                    SumProposed = g.Sum(x => x.ProposedPriceValue),
                })
                .ToListAsync();

            var bandById = bands.ToDictionary(b => b.Id);
            decimal dCost = 0m, dAnchor = 0m, dCurrent = 0m, dProposed = 0m, dFloorWeighted = 0m;
            foreach (var d in deadAgg)
            {
                decimal? blendedFloor = null;
                if (d.SumCost > 0m && bandById.TryGetValue(d.BandId, out var b))
                {
                    var floorWeighted = d.CostS1 * b.DeadStockFloorCostPct
                                      + d.CostS2 * b.DeadStockStage2FloorCostPct
                                      + d.CostS3 * b.DeadStockStage3FloorCostPct
                                      + d.CostS4 * b.DeadStockStage4FloorCostPct;
                    blendedFloor = floorWeighted / (d.SumCost * 100m);
                    dFloorWeighted += floorWeighted;
                }
                vm.DeadStock[d.BandId] = new BandDeadStockMargins(
                    d.Count, d.S1, d.S2, d.S3, d.S4,
                    d.SumAnchor > 0m ? (d.SumAnchor - d.SumCost) / d.SumAnchor : null,
                    d.SumCurrent > 0m ? (d.SumCurrent - d.SumCost) / d.SumCurrent : null,
                    d.SumProposed > 0m ? (d.SumProposed - d.SumCost) / d.SumProposed : null,
                    blendedFloor);
                vm.TotalDeadCount += d.Count;
                vm.TotalDeadStage1 += d.S1;
                vm.TotalDeadStage2 += d.S2;
                vm.TotalDeadStage3 += d.S3;
                vm.TotalDeadStage4 += d.S4;
                dCost += d.SumCost;
                dAnchor += d.SumAnchor;
                dCurrent += d.SumCurrent;
                dProposed += d.SumProposed;
            }
            vm.TotalDeadPricingPct = dAnchor > 0m ? (dAnchor - dCost) / dAnchor : null;
            vm.TotalDeadCurrentPct = dCurrent > 0m ? (dCurrent - dCost) / dCurrent : null;
            vm.TotalDeadProposedPct = dProposed > 0m ? (dProposed - dCost) / dProposed : null;
            vm.TotalDeadBlendedFloorOfCostPct = dCost > 0m ? dFloorWeighted / (dCost * 100m) : null;

            // Target margin on what actually SOLD: the last 30 days' units from this run's snapshot, each band
            // priced to its target margin (see TargetMarginImpact). Each sold SKU is matched to its proposal in
            // this run so a held or adopted price (clearance pin, dead-stock hold, new product, synced) is known:
            // it counts in the 30d margin — which is what the sales report shows — but a target can't move it.
            // Both reads are bounded by their bulk-insert blocks (see BlockRanges); only SKUs that sold come back.
            var snapDate = await _db.DailySnapshots.AsNoTracking()
                .Where(s => s.LayerId == layerId && s.SnapshotDate <= run.StartedUtc)
                .MaxAsync(s => (DateTime?)s.SnapshotDate);
            if (snapDate is DateTime sd
                && await BlockRanges.SnapshotAsync(_db, layerId, sd) is { } snapIds
                && await BlockRanges.ProposalsAsync(_db, run.Id) is { } runIds)
            {
                var runRows = _db.ProposedPrices.AsNoTracking()
                    .Where(p => p.Id >= runIds.Min && p.Id <= runIds.Max && p.PricingRunId == run.Id);
                var soldRows = await (
                    from s in _db.DailySnapshots.AsNoTracking()
                    where s.Id >= snapIds.Min && s.Id <= snapIds.Max
                          && s.LayerId == layerId && s.SnapshotDate == sd
                          && s.Qty30 > 0 && s.Pptcv != null
                    join p in runRows on s.Sku equals p.Sku into pj
                    from p in pj.DefaultIfEmpty()
                    select new
                    {
                        Pptcv = s.Pptcv!.Value, s.Qty30, s.Net30,
                        Priced = p != null && p.Status != ProposalStatus.Skipped,
                        Stage = p == null ? DeadStockStage.None : p.DeadStockStage,
                        Flags = p == null ? null : p.GuardrailFlags,
                        Reasons = p == null ? null : p.ReasonCodes,
                    })
                    .ToListAsync();
                var sold = soldRows.Select(r => new SoldSku(r.Pptcv, r.Qty30, r.Net30,
                    CanReprice: r.Priced && !PriceHolds.IsHeld(r.Stage, r.Flags, r.Reasons)));
                var vatRatePct = await _db.Layers.AsNoTracking()
                    .Where(l => l.Id == layerId).Select(l => l.VatRatePct).FirstOrDefaultAsync();
                vm.TargetImpact = TargetMarginImpact.Compute(sold,
                    bands.Select(b => new TargetBand(b.Id, b.MinPrice, b.MaxPrice, b.TargetMarginPct)).ToList(),
                    vatRatePct);
                vm.TargetImpactSnapshotDate = sd;
            }
        }

        vm.DiscountTrims = await LoadTrimsAsync(layerId);
        vm.DiscountOptimizationEnabled = await _db.Layers.AsNoTracking()
            .Where(l => l.Id == layerId).Select(l => l.DiscountOptimizationEnabled).FirstOrDefaultAsync();

        // "Match to KS": on an MK/AL layer, offer to copy the brand's Kosovo target margins + floors.
        vm.MatchSourceLayerName = (await _bandMatch.GetSourceAsync(layerId))?.DisplayName;
        vm.MatchDefaultUpliftPct = BandMatchService.DefaultTargetMarginUpliftPct;

        return View(vm);
    }

    // ---------------------------------------------------------------- Match to KS

    /// <summary>
    /// Copies the brand's Kosovo band TARGET MARGINS (each plus <paramref name="targetMarginUpliftPct"/>
    /// points) and MARGIN FLOORS onto the current (MK/AL) layer's bands, matched by position. The band
    /// price ranges, rounding convention and dead-stock rules are left as they are — those legitimately
    /// differ per currency/country. Managers only; takes effect on the next run. A no-op (with a notice)
    /// on a KS layer, which has nothing to match to.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MatchToKs(decimal targetMarginUpliftPct = BandMatchService.DefaultTargetMarginUpliftPct)
    {
        var layerId = await _layers.RequireCurrentIdAsync();

        if (targetMarginUpliftPct is < -50m or > 50m)
        {
            TempData["Error"] = "The target-margin uplift must be between −50 and +50 percentage points.";
            return RedirectToAction(nameof(Index));
        }

        var result = await _bandMatch.MatchToPrimaryAsync(layerId, targetMarginUpliftPct);
        if (result is null)
        {
            TempData["Error"] = "This layer has no Kosovo sibling to match — Match to KS is only available on MK and AL layers.";
            return RedirectToAction(nameof(Index));
        }

        if (!result.AnyChange)
        {
            TempData["Message"] = $"Bands already match {result.SourceLayerName} (target margin +{FormatPct(result.TargetMarginUpliftPct)}) — nothing changed.";
            return RedirectToAction(nameof(Index));
        }

        await _db.SaveChangesAsync();
        var user = User.Identity?.Name ?? "unknown";
        foreach (var c in result.Changes)
        {
            await _audit.LogAsync(user, AuditCategories.Config,
                $"Matched band '{c.BandName}' to {result.SourceLayerName} (target margin +{FormatPct(result.TargetMarginUpliftPct)})",
                nameof(PriceBand), c.BandId.ToString(),
                oldValue: $"target {FormatTarget(c.OldTargetMarginPct)}, floor {FormatPct(c.OldMarginFloorPct)}",
                newValue: $"target {FormatTarget(c.NewTargetMarginPct)}, floor {FormatPct(c.NewMarginFloorPct)}",
                layerId: layerId);
        }

        TempData["Message"] = $"Matched {result.Changes.Count} band(s) to {result.SourceLayerName}: target margins set to "
            + $"{result.SourceLayerName}'s + {FormatPct(result.TargetMarginUpliftPct)}, margin floors copied. Takes effect on the next run.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Formats a nullable target margin — "off" when the band has no target goal set.</summary>
    private static string FormatTarget(decimal? pct) => pct is decimal p ? FormatPct(p) : "off";

    // ---------------------------------------------------------------- Discount optimization

    /// <summary>
    /// Saves the layer's discount-optimization inputs (the Trim % per range). The ranges themselves are
    /// fixed; only the trim moves. Inputs only — runs apply them when the layer has "Discount optimization"
    /// ticked on the Schedule page.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveDiscountTrims(List<DiscountTrimInput> trims)
    {
        var layerId = await _layers.RequireCurrentIdAsync();
        if (trims.Any(x => x.TrimPct is < 0m or > 100m))
        {
            TempData["Error"] = "Each trim must be between 0% and 100%.";
            return RedirectToAction(nameof(Index), null, "discount-optimization");
        }

        var rules = await _db.DiscountTrimRules.Where(r => r.LayerId == layerId).OrderBy(r => r.SortOrder).ToListAsync();
        var before = Describe(rules);
        foreach (var input in trims)
        {
            var rule = rules.FirstOrDefault(r => r.Id == input.Id);
            if (rule is not null) rule.TrimPct = input.TrimPct;
        }
        await _db.SaveChangesAsync();

        var after = Describe(rules);
        if (after != before)
        {
            await _audit.LogAsync(User.Identity?.Name ?? "unknown", AuditCategories.Config,
                "Changed discount optimization inputs", nameof(Layer), layerId.ToString(),
                oldValue: before, newValue: after, layerId: layerId);
        }

        var enabled = await _db.Layers.AsNoTracking()
            .Where(l => l.Id == layerId).Select(l => l.DiscountOptimizationEnabled).FirstOrDefaultAsync();
        TempData["Message"] = enabled
            ? "Discount optimization inputs saved — they apply from the next run."
            : "Discount optimization inputs saved. Runs apply them once Discount optimization is ticked on the Schedule page.";
        return RedirectToAction(nameof(Index), null, "discount-optimization");
    }

    /// <summary>
    /// The impact view (a partial the Price Bands page fetches): what the trim table — as typed, saved or
    /// not — would do to today's prices and profit. <paramref name="trim"/> maps a rule id to its Trim %;
    /// rules it doesn't mention keep their saved value. <paramref name="soldOnly"/> (the default) limits it to
    /// SKUs that sold in the last 30 days — the same SKUs the Target impact column prices.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> DiscountImpact([FromQuery] Dictionary<int, decimal>? trim,
        ImpactUnitsBasis basis = ImpactUnitsBasis.Sold30Days, bool soldOnly = true)
    {
        var layerId = await _layers.RequireCurrentIdAsync();
        var ranges = ToRanges(await LoadTrimsAsync(layerId), trim);
        var report = await _impact.ComputeAsync(layerId, ranges, basis, soldOnly, HttpContext.RequestAborted);
        return PartialView("_DiscountImpact", report);
    }

    /// <summary>Every SKU the trim table would move, as CSV — same inputs as <see cref="DiscountImpact"/>.</summary>
    [HttpGet]
    public async Task<IActionResult> DiscountImpactCsv([FromQuery] Dictionary<int, decimal>? trim,
        ImpactUnitsBasis basis = ImpactUnitsBasis.Sold30Days, bool soldOnly = true)
    {
        var layerId = await _layers.RequireCurrentIdAsync();
        var ranges = ToRanges(await LoadTrimsAsync(layerId), trim);
        var report = await _impact.ComputeAsync(layerId, ranges, basis, soldOnly, HttpContext.RequestAborted);
        if (report is null) return NotFound();

        var scope = soldOnly ? "-sold30d" : "";
        var fileName = $"discount-optimization-run{report.RunId}-{basis}{scope}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
        Response.Headers["Content-Disposition"] = $"attachment; filename=\"{fileName}\"";
        Response.ContentType = "text/csv; charset=utf-8";

        // UTF-8 BOM so Excel reads accented names correctly.
        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync("Sku,ProductName,DiscountRange,OldPrice,CurrentPrice,NewPrice,DiscountNowPct,DiscountAfterPct,PriceChangePct,Cost,Units,CurrentProfit,NewProfit,ExtraProfit,CappedAtAnchor,ActiveDiscountIds");
        foreach (var s in report.Result.Skus)
        {
            await writer.WriteLineAsync(string.Join(",",
                Csv(s.Sku), Csv(s.ProductName ?? ""), Csv(s.Range),
                Num(s.OldPrice), Num(s.CurrentPrice), Num(s.NewPrice),
                Num(s.DiscountBeforePct), Num(s.DiscountAfterPct), Num(s.PriceChangePct),
                Num(s.Cost), s.Units.ToString(CultureInfo.InvariantCulture),
                Num(s.CurrentProfit), Num(s.NewProfit), Num(s.ExtraProfit),
                s.CappedAtAnchor ? "1" : "0",
                DiscountIds(s.ActiveDiscountIds)));
        }
        await writer.FlushAsync();
        return new EmptyResult();
    }

    private Task<List<DiscountTrimRule>> LoadTrimsAsync(int layerId) =>
        _db.DiscountTrimRules.AsNoTracking()
            .Where(r => r.LayerId == layerId)
            .OrderBy(r => r.SortOrder)
            .ToListAsync();

    /// <summary>The saved ranges, with any typed-in Trim % (clamped to 0–100) in place of the saved value.</summary>
    private static List<DiscountTrimRange> ToRanges(List<DiscountTrimRule> rules, Dictionary<int, decimal>? typed) =>
        rules.Select(r => new DiscountTrimRange(r.MinDiscountPct, r.MaxDiscountPct,
                typed is not null && typed.TryGetValue(r.Id, out var pct) ? Math.Clamp(pct, 0m, 100m) : r.TrimPct))
            .ToList();

    private static string Describe(IEnumerable<DiscountTrimRule> rules) =>
        string.Join(", ", rules.Select(r => $"{r.ToRange().Label} {r.TrimPct.ToString("0.##", CultureInfo.InvariantCulture)}%"));

    private static string Num(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Discount ids are stored comma-joined; emit them space-separated so Excel keeps them as text
    /// (a quoted comma list is read as one number with thousands separators).</summary>
    private static string DiscountIds(string? value) =>
        string.IsNullOrEmpty(value) ? "" : value.Replace(",", " ");

    /// <summary>RFC-4180 CSV field; neutralises a leading =/+/-/@ so spreadsheets don't run it as a formula.</summary>
    private static string Csv(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var s = value;
        if (s[0] is '=' or '+' or '-' or '@') s = "'" + s;
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    public async Task<IActionResult> Edit(int id)
    {
        var layerId = await _layers.RequireCurrentIdAsync();
        var band = await _db.PriceBands.Include(b => b.AlgorithmSettings)
            .FirstOrDefaultAsync(b => b.Id == id && b.LayerId == layerId);
        if (band is null) return NotFound();
        return View(await BuildEditModelAsync(band));
    }

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(BandEditModel model)
    {
        var layerId = await _layers.RequireCurrentIdAsync();
        var band = await _db.PriceBands.Include(b => b.AlgorithmSettings)
            .FirstOrDefaultAsync(b => b.Id == model.Id && b.LayerId == layerId);
        if (band is null) return NotFound();

        if (model.MaxPrice <= model.MinPrice)
            ModelState.AddModelError(nameof(model.MaxPrice), "Max must be greater than min.");
        if (model.MarginFloorPct is < 0 or >= 100)
            ModelState.AddModelError(nameof(model.MarginFloorPct), "Margin floor must be in [0, 100).");
        if (model.TargetMarginPct is decimal target && (target <= 0m || target >= 100m))
            ModelState.AddModelError(nameof(model.TargetMarginPct), "Target margin must be between 0 and 100% (exclusive), or blank to disable.");
        if (model.DeadStockNoSaleWindowDays is not (7 or 14 or 30 or 60 or 90))
            ModelState.AddModelError(nameof(model.DeadStockNoSaleWindowDays), "No-sale window must be 7, 14, 30, 60 or 90 days (the available sales windows).");
        if (model.DeadStockMinStockAgeDays < 0)
            ModelState.AddModelError(nameof(model.DeadStockMinStockAgeDays), "Min stock age cannot be negative.");
        if (model.DeadStockStartDiscountPct is < 0 or >= 100)
            ModelState.AddModelError(nameof(model.DeadStockStartDiscountPct), "Start discount must be in [0, 100).");
        if (model.DeadStockStepDiscountPct is < 0 or >= 100)
            ModelState.AddModelError(nameof(model.DeadStockStepDiscountPct), "Step discount must be in [0, 100).");
        if (model.DeadStockPeriodDays < 1)
            ModelState.AddModelError(nameof(model.DeadStockPeriodDays), "Period must be at least 1 day.");
        if (model.DeadStockFloorCostPct is <= 0 or > 100)
            ModelState.AddModelError(nameof(model.DeadStockFloorCostPct), "Dead-stock floor must be in (0, 100] % of cost.");
        if (model.DeadStockStage2FloorCostPct is <= 0 or > 100)
            ModelState.AddModelError(nameof(model.DeadStockStage2FloorCostPct), "Stage 2 floor must be in (0, 100] % of cost.");
        if (model.DeadStockStage3FloorCostPct is <= 0 or > 100)
            ModelState.AddModelError(nameof(model.DeadStockStage3FloorCostPct), "Stage 3 floor must be in (0, 100] % of cost.");
        if (model.DeadStockStage4FloorCostPct is <= 0 or > 100)
            ModelState.AddModelError(nameof(model.DeadStockStage4FloorCostPct), "Stage 4 floor must be in (0, 100] % of cost.");
        // Per-stage step % and period, for every stage.
        if (model.DeadStockStage2StepDiscountPct is < 0 or >= 100)
            ModelState.AddModelError(nameof(model.DeadStockStage2StepDiscountPct), "Stage 2 step discount must be in [0, 100).");
        if (model.DeadStockStage3StepDiscountPct is < 0 or >= 100)
            ModelState.AddModelError(nameof(model.DeadStockStage3StepDiscountPct), "Stage 3 step discount must be in [0, 100).");
        if (model.DeadStockStage4StepDiscountPct is < 0 or >= 100)
            ModelState.AddModelError(nameof(model.DeadStockStage4StepDiscountPct), "Stage 4 step discount must be in [0, 100).");
        if (model.DeadStockStage2PeriodDays < 1)
            ModelState.AddModelError(nameof(model.DeadStockStage2PeriodDays), "Stage 2 period must be at least 1 day.");
        if (model.DeadStockStage3PeriodDays < 1)
            ModelState.AddModelError(nameof(model.DeadStockStage3PeriodDays), "Stage 3 period must be at least 1 day.");
        if (model.DeadStockStage4PeriodDays < 1)
            ModelState.AddModelError(nameof(model.DeadStockStage4PeriodDays), "Stage 4 period must be at least 1 day.");
        // The age ladder must strictly increase and the floors must not: older stages clear deeper.
        if (model.DeadStockStage2AgeDays <= model.DeadStockMinStockAgeDays)
            ModelState.AddModelError(nameof(model.DeadStockStage2AgeDays), "Stage 2 age must be greater than the min stock age (stage 1).");
        if (model.DeadStockStage3AgeDays <= model.DeadStockStage2AgeDays)
            ModelState.AddModelError(nameof(model.DeadStockStage3AgeDays), "Stage 3 age must be greater than the stage 2 age.");
        if (model.DeadStockStage4AgeDays <= model.DeadStockStage3AgeDays)
            ModelState.AddModelError(nameof(model.DeadStockStage4AgeDays), "Stage 4 age must be greater than the stage 3 age.");
        if (model.DeadStockStage2FloorCostPct > model.DeadStockFloorCostPct)
            ModelState.AddModelError(nameof(model.DeadStockStage2FloorCostPct), "Floors must not increase with age (stage 1 ≥ stage 2 ≥ stage 3 ≥ stage 4).");
        if (model.DeadStockStage3FloorCostPct > model.DeadStockStage2FloorCostPct)
            ModelState.AddModelError(nameof(model.DeadStockStage3FloorCostPct), "Floors must not increase with age (stage 1 ≥ stage 2 ≥ stage 3 ≥ stage 4).");
        if (model.DeadStockStage4FloorCostPct > model.DeadStockStage3FloorCostPct)
            ModelState.AddModelError(nameof(model.DeadStockStage4FloorCostPct), "Floors must not increase with age (stage 1 ≥ stage 2 ≥ stage 3 ≥ stage 4).");
        foreach (var algorithm in model.Algorithms)
        {
            if (algorithm.Weight is < 0 or > 100)
                ModelState.AddModelError("", $"{algorithm.Code}: weight must be 0–100.");
        }
        if (!ModelState.IsValid) return View(await BuildEditModelAsync(band));

        var before = SerializeBand(band);

        band.Name = model.Name;
        band.MinPrice = model.MinPrice;
        band.MaxPrice = model.MaxPrice;
        band.MarginFloorPct = model.MarginFloorPct;
        band.TargetMarginPct = model.TargetMarginPct;
        band.DeadStockNoSaleWindowDays = model.DeadStockNoSaleWindowDays;
        band.DeadStockMinStockAgeDays = model.DeadStockMinStockAgeDays;
        band.DeadStockStartDiscountPct = model.DeadStockStartDiscountPct;
        band.DeadStockStepDiscountPct = model.DeadStockStepDiscountPct;
        band.DeadStockPeriodDays = model.DeadStockPeriodDays;
        band.DeadStockFloorCostPct = model.DeadStockFloorCostPct;
        band.DeadStockStage2AgeDays = model.DeadStockStage2AgeDays;
        band.DeadStockStage2FloorCostPct = model.DeadStockStage2FloorCostPct;
        band.DeadStockStage2StepDiscountPct = model.DeadStockStage2StepDiscountPct;
        band.DeadStockStage2PeriodDays = model.DeadStockStage2PeriodDays;
        band.DeadStockStage3AgeDays = model.DeadStockStage3AgeDays;
        band.DeadStockStage3FloorCostPct = model.DeadStockStage3FloorCostPct;
        band.DeadStockStage3StepDiscountPct = model.DeadStockStage3StepDiscountPct;
        band.DeadStockStage3PeriodDays = model.DeadStockStage3PeriodDays;
        band.DeadStockStage4AgeDays = model.DeadStockStage4AgeDays;
        band.DeadStockStage4FloorCostPct = model.DeadStockStage4FloorCostPct;
        band.DeadStockStage4StepDiscountPct = model.DeadStockStage4StepDiscountPct;
        band.DeadStockStage4PeriodDays = model.DeadStockStage4PeriodDays;
        band.RoundingConvention = model.RoundingConvention;
        band.RoundingEnabled = model.RoundingEnabled;

        foreach (var algorithmModel in model.Algorithms)
        {
            var setting = band.AlgorithmSettings.FirstOrDefault(s => s.AlgorithmCode == algorithmModel.Code);
            if (setting is null)
            {
                setting = new BandAlgorithmSetting { AlgorithmCode = algorithmModel.Code };
                band.AlgorithmSettings.Add(setting);
            }
            setting.Enabled = algorithmModel.Enabled;
            setting.Weight = algorithmModel.Weight;
        }

        // The margin floor is a currency-agnostic percentage and is kept identical across a brand's three
        // countries, so a save on the Kosovo layer carries it to the MK/AL bands in the same position.
        // Applied before the single SaveChanges below so the layers never commit half-mirrored.
        var mirrored = await _floorMirror.MirrorMarginFloorAsync(band);

        await _db.SaveChangesAsync();
        var user = User.Identity?.Name ?? "unknown";
        await _audit.LogAsync(user, AuditCategories.Config,
            $"Edited band '{band.Name}'", nameof(PriceBand), band.Id.ToString(),
            oldValue: before, newValue: SerializeBand(band), layerId: layerId);
        foreach (var m in mirrored)
        {
            await _audit.LogAsync(user, AuditCategories.Config,
                $"Mirrored margin floor from '{band.Name}' onto '{m.BandName}' ({m.LayerDisplayName})",
                nameof(PriceBand), m.BandId.ToString(),
                oldValue: FormatPct(m.OldFloorPct), newValue: FormatPct(m.NewFloorPct), layerId: m.LayerId);
        }

        TempData["Message"] = mirrored.Count == 0
            ? $"Band '{band.Name}' saved."
            : $"Band '{band.Name}' saved. Margin floor {FormatPct(band.MarginFloorPct)} also applied to "
              + string.Join(" and ", mirrored.Select(m => m.LayerDisplayName)) + ".";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Formats a stored percent for humans — "8%" rather than the column's "8.0000".</summary>
    private static string FormatPct(decimal pct) => pct.ToString("0.##") + "%";

    /// <summary>The edit model plus the margin-floor mirroring context, so the form can say which layers a
    /// save propagates to (on Kosovo) or which layer this band's floor is driven by (on MK/AL).</summary>
    private async Task<BandEditModel> BuildEditModelAsync(PriceBand band)
    {
        var model = ToEditModel(band);
        model.FloorMirrorsToLayers = (await _floorMirror.GetMirrorTargetsAsync(band.LayerId))
            .Select(l => l.DisplayName).ToList();
        model.FloorMirroredFromLayer = (await _floorMirror.GetMirrorSourceAsync(band.LayerId))?.DisplayName;
        return model;
    }

    private static BandEditModel ToEditModel(PriceBand band)
    {
        var names = AlgorithmCodes.All.ToDictionary(a => a.Code, a => a.DisplayName);
        return new BandEditModel
        {
            Id = band.Id,
            Name = band.Name,
            MinPrice = band.MinPrice,
            MaxPrice = band.MaxPrice,
            MarginFloorPct = band.MarginFloorPct,
            TargetMarginPct = band.TargetMarginPct,
            DeadStockNoSaleWindowDays = band.DeadStockNoSaleWindowDays,
            DeadStockMinStockAgeDays = band.DeadStockMinStockAgeDays,
            DeadStockStartDiscountPct = band.DeadStockStartDiscountPct,
            DeadStockStepDiscountPct = band.DeadStockStepDiscountPct,
            DeadStockPeriodDays = band.DeadStockPeriodDays,
            DeadStockFloorCostPct = band.DeadStockFloorCostPct,
            DeadStockStage2AgeDays = band.DeadStockStage2AgeDays,
            DeadStockStage2FloorCostPct = band.DeadStockStage2FloorCostPct,
            DeadStockStage2StepDiscountPct = band.DeadStockStage2StepDiscountPct,
            DeadStockStage2PeriodDays = band.DeadStockStage2PeriodDays,
            DeadStockStage3AgeDays = band.DeadStockStage3AgeDays,
            DeadStockStage3FloorCostPct = band.DeadStockStage3FloorCostPct,
            DeadStockStage3StepDiscountPct = band.DeadStockStage3StepDiscountPct,
            DeadStockStage3PeriodDays = band.DeadStockStage3PeriodDays,
            DeadStockStage4AgeDays = band.DeadStockStage4AgeDays,
            DeadStockStage4FloorCostPct = band.DeadStockStage4FloorCostPct,
            DeadStockStage4StepDiscountPct = band.DeadStockStage4StepDiscountPct,
            DeadStockStage4PeriodDays = band.DeadStockStage4PeriodDays,
            RoundingConvention = band.RoundingConvention,
            RoundingEnabled = band.RoundingEnabled,
            Algorithms = AlgorithmCodes.All.Select(a =>
            {
                var setting = band.AlgorithmSettings.FirstOrDefault(s => s.AlgorithmCode == a.Code);
                return new BandAlgorithmEditModel
                {
                    Code = a.Code,
                    DisplayName = names[a.Code],
                    Enabled = setting?.Enabled ?? false,
                    Weight = setting?.Weight ?? a.DefaultWeight,
                };
            }).ToList(),
        };
    }

    private static string SerializeBand(PriceBand band) => JsonSerializer.Serialize(new
    {
        band.Name,
        band.MinPrice,
        band.MaxPrice,
        band.MarginFloorPct,
        band.TargetMarginPct,
        band.DeadStockNoSaleWindowDays,
        band.DeadStockMinStockAgeDays,
        band.DeadStockStartDiscountPct,
        band.DeadStockStepDiscountPct,
        band.DeadStockPeriodDays,
        band.DeadStockFloorCostPct,
        band.DeadStockStage2AgeDays,
        band.DeadStockStage2FloorCostPct,
        band.DeadStockStage2StepDiscountPct,
        band.DeadStockStage2PeriodDays,
        band.DeadStockStage3AgeDays,
        band.DeadStockStage3FloorCostPct,
        band.DeadStockStage3StepDiscountPct,
        band.DeadStockStage3PeriodDays,
        band.DeadStockStage4AgeDays,
        band.DeadStockStage4FloorCostPct,
        band.DeadStockStage4StepDiscountPct,
        band.DeadStockStage4PeriodDays,
        band.RoundingConvention,
        band.RoundingEnabled,
        Algorithms = band.AlgorithmSettings
            .OrderBy(s => s.AlgorithmCode)
            .Select(s => new { s.AlgorithmCode, s.Enabled, s.Weight }),
    });
}
