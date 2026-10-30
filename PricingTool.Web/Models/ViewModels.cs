using PricingTool.Data.Entities;

namespace PricingTool.Web.Models;

// ---------------------------------------------------------------- Price Bands

/// <summary>Actual band-blended margins from the latest run, computed the same way the pricing table does:
/// (price − cost) / price. <c>PricingPct</c> uses the anchor (FinalPrice); <c>CurrentPct</c> uses the live
/// current price. Both are fractions (0..1); null when the band had no priced SKUs with a known cost.</summary>
public record BandActualMargins(decimal? PricingPct, decimal? CurrentPct);

/// <summary>Dead-stock cut of the latest run for one band: SKU counts by age stage (1 = "3–6 months",
/// 2 = "6–12 months", 3 = "12–36 months", 4 = "36 months+") and the blended margins over those SKUs —
/// Pricing (anchor), Current (live price) and Proposed (this run's proposal). <c>BlendedFloorOfCostPct</c>
/// is the cost-weighted average of the band's per-stage floors, as a fraction of cost (1.0 = at cost).
/// All fractions; null when the band had no dead-stock SKUs with a known cost.</summary>
public record BandDeadStockMargins(
    int SkuCount, int Stage1Count, int Stage2Count, int Stage3Count, int Stage4Count,
    decimal? PricingPct, decimal? CurrentPct, decimal? ProposedPct,
    decimal? BlendedFloorOfCostPct);

/// <summary>Price Bands page model: the band config plus per-band actual margins (Pricing % / Current %)
/// from the most recent completed run for the current layer, and the blended totals across all bands.</summary>
public class BandsIndexViewModel
{
    public List<PriceBand> Bands { get; set; } = new();
    public Dictionary<int, BandActualMargins> Margins { get; set; } = new();
    public long? RunId { get; set; }
    public DateTime? RunStartedUtc { get; set; }
    public decimal? TotalPricingPct { get; set; }
    public decimal? TotalCurrentPct { get; set; }
    /// <summary>Anchor-revenue-weighted blend of the per-band Target margin / Margin floor, so the totals
    /// row is on the same footing as the blended Pricing %. Fractions (0..1); null when no run data.</summary>
    public decimal? TotalTargetPct { get; set; }
    public decimal? TotalFloorPct { get; set; }

    /// <summary>Per-band dead-stock cut of the latest run (keyed by band id); empty for pre-stage runs.</summary>
    public Dictionary<int, BandDeadStockMargins> DeadStock { get; set; } = new();
    public int TotalDeadCount { get; set; }
    public int TotalDeadStage1 { get; set; }
    public int TotalDeadStage2 { get; set; }
    public int TotalDeadStage3 { get; set; }
    public int TotalDeadStage4 { get; set; }
    public decimal? TotalDeadPricingPct { get; set; }
    public decimal? TotalDeadCurrentPct { get; set; }
    public decimal? TotalDeadProposedPct { get; set; }
    public decimal? TotalDeadBlendedFloorOfCostPct { get; set; }

    /// <summary>The layer's discount-optimization table (inputs edited here, applied by runs when the
    /// layer has "Discount optimization" ticked on the Schedule page).</summary>
    public List<DiscountTrimRule> DiscountTrims { get; set; } = new();
    public bool DiscountOptimizationEnabled { get; set; }

    /// <summary>Last 30 days' sales per band, as they were and at each band's target margin (same units);
    /// null when there's no run/snapshot. The sales-weighted counterpart of the Target margin total.</summary>
    public PricingTool.Core.Services.TargetImpactResult? TargetImpact { get; set; }
    public DateTime? TargetImpactSnapshotDate { get; set; }

    /// <summary>Display name of the KS layer this (MK/AL) layer can copy its target margins and floors from,
    /// or null when the layer is itself KS or has no KS sibling — the "Match to KS" button shows only when set.</summary>
    public string? MatchSourceLayerName { get; set; }

    /// <summary>Default points added to each copied target margin, pre-filled in the Match-to-KS uplift box.</summary>
    public decimal MatchDefaultUpliftPct { get; set; }
}

/// <summary>One edited row of the discount-optimization table, as posted from the Price Bands tab.</summary>
public class DiscountTrimInput
{
    public int Id { get; set; }
    public decimal TrimPct { get; set; }
}

// ---------------------------------------------------------------- Dashboard

public record DailyKpi(DateTime Date, decimal MarginPct, decimal NetRevenuePerDay, decimal UnitsPerDay);

public record KpiSummary(decimal MarginPct, decimal NetRevenuePerDay, decimal UnitsPerDay);

public record AlgorithmAttribution(string AlgorithmCode, int ChangedProposals, decimal AvgAbsChangePct);

public record BandAttribution(string BandName, int ChangedProposals, decimal AvgChangePct);

public record OutcomeSummary(
    ChangeIntent Intent, int Total, int Wins, int Neutrals, int Backfires,
    decimal WinRatePct, decimal AvgDeltaUnitsPerDay, decimal? AvgDeltaGrossProfitPerDay);

public record OutcomeRow(
    string Sku, ChangeIntent Intent, OutcomeVerdict Verdict,
    decimal DeltaUnitsPerDay, decimal DeltaGrossProfitPerDay);

/// <summary>Dead-stock clearance tiles for the dashboard: how much stock is stuck in the progressive-
/// markdown tunnel and how much of it has started to move. The cohort is every SKU the engine has
/// flagged dead (<c>DeadStockStage &gt; 0</c>) in a retained run for the layer, matched to its latest
/// snapshot; "moving" = that snapshot's trailing-30-day units &gt; 0 (a dead SKU has zero sales by
/// definition, so any 30-day sale means it has revived since being marked down).</summary>
public class DeadStockPanel
{
    // ---- Batch scope = the latest run's dead-stock population (the SKUs we're clearing / approve).
    /// <summary>SKUs in the latest run's dead-stock batch ("In the tunnel").</summary>
    public int DeadSkus { get; set; }
    public long StockUnits { get; set; }
    public decimal StockCost { get; set; }
    /// <summary>Average markdown depth across the batch — discount off shelf, a FRACTION (0.24 = 24%).</summary>
    public decimal AvgDiscountFraction { get; set; }
    /// <summary>The run whose dead-stock batch the tiles + table cover (latest that produced any); the
    /// table is a TOP-N of the batch (<see cref="BatchTotal"/> = <see cref="DeadSkus"/>).</summary>
    public long? BatchRunId { get; set; }
    public int BatchTotal { get; set; }

    // ---- Revival scope = dead stock marked down across the RECENT (retained) runs. Wider than the
    // batch on purpose: a SKU that actually sells stops being "dead", so it only shows here once it has
    // left the batch. This is the only cohort in which "are we selling" is answerable.
    /// <summary>Distinct dead-stock SKUs marked down across retained runs — the sell-through denominator.</summary>
    public int CohortSkus { get; set; }
    /// <summary>Of that cohort, how many recorded a sale in the trailing 30 days (revived).</summary>
    public int MovingSkus { get; set; }
    public long MovingUnits30 { get; set; }
    public decimal MovingNet30 { get; set; }

    public DateTime? SnapshotDate { get; set; }
    public bool HasData => DeadSkus > 0 || CohortSkus > 0;
    public decimal SellThroughPct => CohortSkus > 0 ? Math.Round((decimal)MovingSkus / CohortSkus * 100m, 1) : 0m;

    // ---- Approved-batch mode: when the layer has an explicitly pushed batch, the panel tracks THAT
    // set (what we actually approved & pushed) over a fixed window from the push date, instead of the
    // latest run's auto-flagged dead-stock population. Sold-since = sales since PushedUtc (live-sourced),
    // so DeadSkus/StockUnits/etc. describe the batch and MovingSkus/MovingUnits30/MovingNet30 are the
    // batch's sales since push.
    public bool IsApprovedBatch { get; set; }
    public string? BatchLabel { get; set; }
    public DateTime? PushedUtc { get; set; }
    public DateTime? SalesRefreshedUtc { get; set; }
    /// <summary>1-based day within the tracking window (day 1 = push day).</summary>
    public int TrackingDayNumber { get; set; }
    /// <summary>Length of the clearance tracking window, in days. Kept at/above the run cadence (15 days)
    /// so the window doesn't read as "complete" before the next scheduled run refreshes sell-through.</summary>
    public int TrackingWindowDays { get; set; } = 16;

    /// <summary>Approved-batch SKUs that had local stock at push and now have 0 — cleared / sold out.
    /// A top slice is shown on the page; the full list is downloadable as CSV.</summary>
    public List<DeadStockSoldOutRow> SoldOut { get; set; } = new();
    /// <summary>Total sold-out SKUs (the CSV has all of them; <see cref="SoldOut"/> may be a top slice).</summary>
    public int SoldOutCount { get; set; }
}

/// <summary>One row of the "dead stock we marked down" table — a changed dead-stock proposal from the
/// latest run, with units sold since (from the latest snapshot) so a revived SKU is visible. Discount is
/// a fraction off the shelf/list price (0.24 = 24%).</summary>
public record DeadStockMarkdownRow(
    string Sku, string? ProductName, string? Brand, byte Stage,
    decimal CurrentPrice, decimal NewPrice, decimal DiscountFraction,
    int Qty7, int Qty30, int Stock);

/// <summary>One approved-batch SKU that has sold since the push — the "is it moving?" list, shown
/// only in approved-batch mode (the capital-sorted table buries movers below the big-ticket items).</summary>
public record DeadStockMoverRow(
    string Sku, string? ProductName, string? Brand,
    decimal PushedPrice, int SoldUnits, decimal SoldNet, int StockAtPush);

/// <summary>One approved-batch SKU that has SOLD OUT since the push — it had local stock at push and now
/// has none (fully cleared). The success list; downloadable as CSV from the dead-stock dashboard.</summary>
public record DeadStockSoldOutRow(
    string Sku, string? ProductName, string? Brand,
    decimal PushedPrice, int StockAtPush, int SoldUnits, decimal SoldNet);

public class DashboardViewModel
{
    public List<DailyKpi> Trend { get; set; } = new();
    /// <summary>Weekly realized profit (SUM Margin from the sales report) for the general profit-trend bar
    /// chart, from the configured tracking-start week forward. Empty in demo mode or before any sales.</summary>
    public List<PricingTool.Core.Domain.WeeklyProfitPoint> WeeklyProfit { get; set; } = new();
    public KpiSummary? Baseline { get; set; }
    public KpiSummary? Recent { get; set; }
    public decimal? MarginLiftPct { get; set; }
    public decimal? VolumeChangePct { get; set; }
    public decimal TargetMarginLiftPct { get; set; } = 25m;

    public List<AlgorithmAttribution> AlgorithmAttribution { get; set; } = new();
    public List<BandAttribution> BandAttribution { get; set; } = new();

    /// <summary>Recent approvals (most recent first) with their before/after realized-profit windows —
    /// the per-approval "did the bet pay off?" table. Each approval is a batch that went live; profit is
    /// the sales-report gross profit for its SKUs, 7/30/90d before the approval vs after.</summary>
    public List<Approval> Approvals { get; set; } = new();

    public PricingRun? LastRun { get; set; }
    public int MissingCostSkus { get; set; }
    public int GuardrailClampedSkus { get; set; }
    /// <summary>How many of the most recent <see cref="RecentRunsWindow"/> runs for this layer failed.
    /// A run-count window (not a fixed calendar window) so it stays meaningful at any cadence — at the
    /// 15-day cadence a 7-calendar-day window would almost always contain zero runs.</summary>
    public int RecentFailedRuns { get; set; }
    public const int RecentRunsWindow = 5;
    public List<string> MissingCostSampleSkus { get; set; } = new();

    public DeadStockPanel DeadStock { get; set; } = new();
    public List<DeadStockMarkdownRow> DeadStockMarkdown { get; set; } = new();
    public List<DeadStockMoverRow> DeadStockMovers { get; set; } = new();
}

// ---------------------------------------------------------------- Proposals

public class ProposalsFilter
{
    public long? RunId { get; set; }
    public int? BandId { get; set; }
    public string? Algorithm { get; set; }

    /// <summary>Filter to one supplier (ZStores.Id captured on the proposal at run time).</summary>
    public int? SupplierId { get; set; }

    /// <summary>Free-text brand (manufacturer name) search — substring match, scoped within the run.</summary>
    public string? Brand { get; set; }

    /// <summary>Free-text product-name search — substring match, scoped within the run.</summary>
    public string? ProductName { get; set; }

    /// <summary>Free-text guardrail-flag search — substring match on the comma-joined flags.</summary>
    public string? Guardrails { get; set; }

    /// <summary>Dead-stock age-stage filter: null = all rows, −1 = any stage (1–4), 0 = not dead,
    /// 1–4 = that exact stage (values of <see cref="PricingTool.Core.Domain.DeadStockStage"/>).</summary>
    public int? DeadStockStage { get; set; }

    /// <summary>Free-text SKU search (substring match), scoped within the selected run.</summary>
    public string? Sku { get; set; }

    /// <summary>Signed Δ% range for the on-screen Δ% column filter — the ACTUAL change, not magnitude.
    /// Negative = price drop, positive = rise (e.g. Max = −20 keeps only drops of 20%+; Min = 0 keeps only
    /// rises). Both inclusive; either bound optional.</summary>
    public decimal? MinChangePct { get; set; }
    public decimal? MaxChangePct { get; set; }

    /// <summary>Upper bound on |Δ%| — the magnitude cap behind the "normal changes only" export. Not a
    /// visible column filter; set by the Export-normal button via a hidden field.</summary>
    public decimal? MaxAbsChangePct { get; set; }

    // ---- Per-column numeric ranges (the "Excel" column filters). All inclusive; either bound optional.
    public decimal? CostMin { get; set; }
    public decimal? CostMax { get; set; }
    public decimal? OldMin { get; set; }
    public decimal? OldMax { get; set; }
    public decimal? AnchorMin { get; set; }
    public decimal? AnchorMax { get; set; }
    public decimal? CurrentMin { get; set; }
    public decimal? CurrentMax { get; set; }
    public decimal? ProposedMin { get; set; }
    public decimal? ProposedMax { get; set; }

    /// <summary>Prev-margin bounds, in PERCENT (e.g. 0 = break-even). Prev margin = (Current − Cost)/Current.
    /// Only cost-known rows with a positive current price can match.</summary>
    public decimal? PrevMarginMin { get; set; }
    public decimal? PrevMarginMax { get; set; }

    /// <summary>New-margin bounds, in PERCENT. New margin = (Proposed − Cost)/Proposed.</summary>
    public decimal? NewMarginMin { get; set; }
    public decimal? NewMarginMax { get; set; }

    // ---- Snapshot-backed column ranges (local stock, 7d/30d unit sales). Unlike the ranges above these
    //      live in DailySnapshots, not on the proposal row, so they're applied via a semi-join to the run's
    //      snapshot day (see Services.ProposalQuery.ApplySnapshotFilters) rather than in Apply. Integer
    //      unit counts; inclusive bounds. A SKU with no snapshot row can't match a bound (value unknown).
    public int? LocalStockMin { get; set; }
    public int? LocalStockMax { get; set; }
    public int? Qty7Min { get; set; }
    public int? Qty7Max { get; set; }
    public int? Qty30Min { get; set; }
    public int? Qty30Max { get; set; }

    /// <summary>Active-discount-id filter: "blank" = SKUs with no active discount ids, "nonblank" = only
    /// those that have some; null/empty = no filter.</summary>
    public string? DiscountIds { get; set; }

    /// <summary>Gjirafa50-sync filter for a GjirafaMall run: "exclude" drops the rows that adopted
    /// Gjirafa50's price (reason <c>SYNCED_TO_GJ50</c>) — i.e. the Gjirafa50 products in the Mall's
    /// catalog, which Mall never prices itself — leaving only the SKUs Mall priced on its own;
    /// "only" keeps just the synced rows; null/empty = no filter.</summary>
    public string? Gj50Sync { get; set; }

    /// <summary>Platform-new-product filter: "only" keeps just the MarkAsNew-window products
    /// (<c>ProposedPrice.IsNewProduct</c>), "exclude" hides them; null/empty = no filter. Based on the true
    /// source signal, so it works whether the layer held the new products or priced them.</summary>
    public string? NewProduct { get; set; }

    public string Status { get; set; } = "Pending";
    public bool ChangedOnly { get; set; } = true;

    /// <summary>Sort token, "<c>column_dir</c>" (e.g. <c>proposed_desc</c>, <c>qty7_asc</c>,
    /// <c>prevmargin_asc</c>). The four legacy values (<c>change_desc/asc</c>, <c>sku</c>,
    /// <c>price_desc</c>) are still accepted and mapped by <see cref="Services.ProposalQuery"/>.</summary>
    public string Sort { get; set; } = "change_desc";

    /// <summary>Every filter field as route values, for redirect-after-POST and for building sort/export
    /// links that preserve the active filter. Null-valued keys are dropped from the resulting query string.</summary>
    public Microsoft.AspNetCore.Routing.RouteValueDictionary ToRouteValues() => new()
    {
        [nameof(RunId)] = RunId,
        [nameof(BandId)] = BandId,
        [nameof(Algorithm)] = Algorithm,
        [nameof(SupplierId)] = SupplierId,
        [nameof(Brand)] = Brand,
        [nameof(ProductName)] = ProductName,
        [nameof(Guardrails)] = Guardrails,
        [nameof(DeadStockStage)] = DeadStockStage,
        [nameof(Sku)] = Sku,
        [nameof(MinChangePct)] = MinChangePct,
        [nameof(MaxChangePct)] = MaxChangePct,
        [nameof(MaxAbsChangePct)] = MaxAbsChangePct,
        [nameof(CostMin)] = CostMin,
        [nameof(CostMax)] = CostMax,
        [nameof(OldMin)] = OldMin,
        [nameof(OldMax)] = OldMax,
        [nameof(AnchorMin)] = AnchorMin,
        [nameof(AnchorMax)] = AnchorMax,
        [nameof(CurrentMin)] = CurrentMin,
        [nameof(CurrentMax)] = CurrentMax,
        [nameof(ProposedMin)] = ProposedMin,
        [nameof(ProposedMax)] = ProposedMax,
        [nameof(PrevMarginMin)] = PrevMarginMin,
        [nameof(PrevMarginMax)] = PrevMarginMax,
        [nameof(NewMarginMin)] = NewMarginMin,
        [nameof(NewMarginMax)] = NewMarginMax,
        [nameof(LocalStockMin)] = LocalStockMin,
        [nameof(LocalStockMax)] = LocalStockMax,
        [nameof(Qty7Min)] = Qty7Min,
        [nameof(Qty7Max)] = Qty7Max,
        [nameof(Qty30Min)] = Qty30Min,
        [nameof(Qty30Max)] = Qty30Max,
        [nameof(DiscountIds)] = DiscountIds,
        [nameof(Gj50Sync)] = Gj50Sync,
        [nameof(NewProduct)] = NewProduct,
        [nameof(Status)] = Status,
        [nameof(ChangedOnly)] = ChangedOnly,
        [nameof(Sort)] = Sort,
    };
}

/// <summary>One supplier present in the selected run, for the Proposals filter dropdown.</summary>
public record SupplierOption(int Id, string Name);

public class ProposalsViewModel
{
    public ProposalsFilter Filter { get; set; } = new();
    public PricingRun? Run { get; set; }
    public List<PricingRun> RecentRuns { get; set; } = new();
    public List<ProposedPrice> Proposals { get; set; } = new();
    /// <summary>Trailing-window unit sales (7d, 30d) and current local-warehouse stock for the SKUs on
    /// screen, keyed by SKU, from the run's snapshot day. Missing key = no snapshot row for that SKU
    /// (rendered as "–").</summary>
    public Dictionary<string, (int Qty7, int Qty30, int LocalStock)> Sales { get; set; } = new();
    public List<PriceBand> Bands { get; set; } = new();
    public List<string> AlgorithmCodes { get; set; } = new();
    /// <summary>Distinct suppliers captured on the selected run's proposals (empty for pre-supplier runs).</summary>
    public List<SupplierOption> Suppliers { get; set; } = new();
    /// <summary>Distinct brands (manufacturer names) on the selected run's proposals, for the Brand filter's
    /// searchable dropdown (suggestions only — the filter itself stays a substring match).</summary>
    public List<string> Brands { get; set; } = new();
    public decimal ConfirmationThresholdPct { get; set; }
    /// <summary>True on a GjirafaMall layer — the only place where rows can adopt Gjirafa50's price — so
    /// the Reasons column renders the Gj50-sync filter. Hidden elsewhere: a Gjirafa50 layer never syncs
    /// to itself, so the control would be meaningless there.</summary>
    public bool ShowGj50SyncFilter { get; set; }
    public int TotalCount { get; set; }
    public int ApprovedCount { get; set; }
    /// <summary>Profit/margin impact (now→proposed) over 7d/30d/90d for the current filtered view:
    /// the naive same-units baseline plus the elasticity-aware projection for elastic SKUs.</summary>
    public List<PricingTool.Core.Services.ProposalWindowKpi> Kpis { get; set; } = new();
}

// ---------------------------------------------------------------- Bands

public class BandEditModel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal MinPrice { get; set; }
    public decimal MaxPrice { get; set; }
    public decimal MarginFloorPct { get; set; }

    /// <summary>Optional per-band blended margin goal (percent); null/blank = the margin-target guardrail is off.</summary>
    public decimal? TargetMarginPct { get; set; }

    // Dead-stock progressive markdown (per band). The floor deepens — and the step/period may change —
    // with the stock's age stage: stage 1 "3–6 months" (MinStockAgeDays–Stage2AgeDays), stage 2
    // "6–12 months", stage 3 "12–36 months", stage 4 "36 months+". Stage 1's step/period/floor are the
    // DeadStockStep/Period/FloorCostPct fields; stages 2–4 carry their own.
    public int DeadStockNoSaleWindowDays { get; set; }
    public int DeadStockMinStockAgeDays { get; set; }
    public decimal DeadStockStartDiscountPct { get; set; }
    public decimal DeadStockStepDiscountPct { get; set; }
    public int DeadStockPeriodDays { get; set; }
    public decimal DeadStockFloorCostPct { get; set; }
    public int DeadStockStage2AgeDays { get; set; }
    public decimal DeadStockStage2FloorCostPct { get; set; }
    public decimal DeadStockStage2StepDiscountPct { get; set; }
    public int DeadStockStage2PeriodDays { get; set; }
    public int DeadStockStage3AgeDays { get; set; }
    public decimal DeadStockStage3FloorCostPct { get; set; }
    public decimal DeadStockStage3StepDiscountPct { get; set; }
    public int DeadStockStage3PeriodDays { get; set; }
    public int DeadStockStage4AgeDays { get; set; }
    public decimal DeadStockStage4FloorCostPct { get; set; }
    public decimal DeadStockStage4StepDiscountPct { get; set; }
    public int DeadStockStage4PeriodDays { get; set; }

    public int RoundingConvention { get; set; }
    public bool RoundingEnabled { get; set; }
    public List<BandAlgorithmEditModel> Algorithms { get; set; } = new();

    /// <summary>Layers this band's margin floor is copied to on save — the brand's other countries, and
    /// only when this band sits on the Kosovo layer. Display-only; not posted back.</summary>
    public List<string> FloorMirrorsToLayers { get; set; } = new();

    /// <summary>The Kosovo layer this band's margin floor is driven by, when this band is NOT on it.
    /// Display-only; a floor edited here is overwritten the next time that layer's band is saved.</summary>
    public string? FloorMirroredFromLayer { get; set; }
}

public class BandAlgorithmEditModel
{
    public string Code { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Enabled { get; set; }
    public int Weight { get; set; }
}

// ---------------------------------------------------------------- Schedule

public class ScheduleViewModel
{
    public string RunTimeUtc { get; set; } = "03:00";
    public int CadenceHours { get; set; } = 24;
    public DateTime? LastScheduledRunUtc { get; set; }
    public DateTime NextRunUtc { get; set; }
    public bool RunInProgress { get; set; }

    /// <summary>When true, runs for this layer skip the algorithms — only the margin floor and rounding move prices.</summary>
    public bool FloorAndRoundingOnly { get; set; }

    /// <summary>When true (the default), runs ignore platform-new products (held at current price). Untick to price them.</summary>
    public bool IgnoreNewProducts { get; set; } = true;

    /// <summary>When true, runs apply the layer's discount-optimization table (set on the Price Bands tab). Default off.</summary>
    public bool DiscountOptimization { get; set; }

    public List<PricingRun> RecentRuns { get; set; } = new();
}

// ---------------------------------------------------------------- Audit

public class AuditViewModel
{
    public string? Search { get; set; }
    public string? Category { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public List<AuditLogEntry> Entries { get; set; } = new();
}

// ---------------------------------------------------------------- SKU drill-down

public class SkuProposalHistory
{
    public PricingRun Run { get; set; } = null!;
    public ProposedPrice Proposal { get; set; } = null!;
}

public class SkuDetailsViewModel
{
    public string Sku { get; set; } = "";
    public List<SkuProposalHistory> Proposals { get; set; } = new();
    public DailySnapshot? Latest { get; set; }
}

public class ErrorViewModel
{
    public string? RequestId { get; set; }
    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);
}

// ---------------------------------------------------------------- Movers

/// <summary>A changed proposal joined to its run-day snapshot (sales + stock) for the Movers lists.</summary>
public class MoverRow
{
    public string Sku { get; set; } = "";
    public int? PriceBandId { get; set; }
    public string Band { get; set; } = "–";
    /// <summary>Supplier (store) id + name; null on rows from before suppliers were captured.</summary>
    public int? SupplierId { get; set; }
    public string? Supplier { get; set; }
    public decimal OldPrice { get; set; }
    public decimal AnchorPrice { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal ProposedPrice { get; set; }
    public decimal? Pptcv { get; set; }
    public decimal ChangePct { get; set; }
    public int Qty7 { get; set; }
    public int Qty30 { get; set; }
    public int Qty90 { get; set; }
    /// <summary>VAT-exclusive net revenue per trailing window (for the average-selling-price columns).</summary>
    public decimal Net7 { get; set; }
    public decimal Net30 { get; set; }
    public decimal Net90 { get; set; }
    public int KsStock { get; set; }
    public int SupplierStock { get; set; }
    public string ReasonCodes { get; set; } = "";
    public string GuardrailFlags { get; set; } = "";
}

public class MoversViewModel
{
    public PricingRun? Run { get; set; }
    public List<MoverRow> TopSellers { get; set; } = new();
    public List<MoverRow> DeadInStock { get; set; } = new();
    /// <summary>Distinct suppliers among the run's changed movers, for the filter dropdown.</summary>
    public List<SupplierOption> Suppliers { get; set; } = new();
    /// <summary>Selected supplier filter; both Top-50 lists (and the KPIs) are scoped to it.</summary>
    public int? SupplierId { get; set; }
    /// <summary>Layer VAT rate, used to gross up net revenue into average selling prices.</summary>
    public decimal VatRatePct { get; set; } = 18m;
    /// <summary>Profit/margin impact (now→proposed) over 7d/30d/90d across the movers shown.</summary>
    public List<PricingTool.Core.Services.WindowProfit> Kpis { get; set; } = new();
}
