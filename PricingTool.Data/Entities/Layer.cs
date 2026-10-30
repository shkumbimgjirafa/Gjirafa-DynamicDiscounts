namespace PricingTool.Data.Entities;

/// <summary>
/// A pricing "layer" — one Brand + Country combination (e.g. GjirafaMall / KS). A layer owns
/// everything needed to pull its source data (which operational DB, store/country filter ids,
/// local-warehouse id, per-brand query toggles) plus its own schedule, price bands and proposals.
/// Every scoped table carries a <c>LayerId</c> FK to one of these rows.
/// </summary>
public class Layer
{
    public int Id { get; set; }

    /// <summary>"GjirafaMall" or "Gjirafa50".</summary>
    public string Brand { get; set; } = "";

    /// <summary>"KS" / "MK" / "AL".</summary>
    public string CountryCode { get; set; } = "";

    /// <summary>Human-friendly label shown in the UI, e.g. "GjirafaMall — Kosovo".</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>NOP operational database name substituted into the source query: GjirafaMall / GjirafaEcommerce.</summary>
    public string OperationalDatabase { get; set; } = "GjirafaMall";

    /// <summary>Store id used to filter orders, tier prices and discounts.</summary>
    public int StoreId { get; set; }

    /// <summary>CountryId used to filter product pricing in GjirafaTranslations.</summary>
    public int TranslationCountryId { get; set; }

    /// <summary>Warehouse store id used for the IsLocalToStoreIds local-stock split.</summary>
    public int WarehouseStoreId { get; set; }

    /// <summary>
    /// WarehouseManagmentSystem.ProductCheckIns.WarehouseId for this layer's country (KS=1, AL=5, MK=6).
    /// Scopes the per-SKU "oldest on-hand unit age" lookup that gates dead-stock (a freshly received
    /// pre-order isn't dead, it just arrived). Brand-independent — both brands in a country share it.
    /// </summary>
    public int WmsWarehouseId { get; set; }

    /// <summary>SR_ProductsData PlatformId for elasticity scoping (paired with SrCompanyId).</summary>
    public int SrPlatformId { get; set; }

    /// <summary>SR_ProductsData CompanyId for elasticity scoping (paired with SrPlatformId).</summary>
    public int SrCompanyId { get; set; }

    /// <summary>Display currency for this layer (EUR / MKD / ALL). Bands are denominated in it.</summary>
    public string Currency { get; set; } = "EUR";

    /// <summary>This layer's VAT rate percent (18 for KS/MK, 20 for AL). VAT-incl prices ↔ VAT-excl cost.</summary>
    public decimal VatRatePct { get; set; } = 18m;

    /// <summary>True = restrict to the GjirafaMall vendor set; false = all vendors (Gjirafa50).</summary>
    public bool FilterVendors { get; set; } = true;

    /// <summary>True = exclude products not published in this store (UnpublishedStoreids NOT LIKE '%StoreId%').</summary>
    public bool ExcludeUnpublished { get; set; } = true;

    /// <summary>
    /// When true, the AI algorithms are switched off for this layer: a run skips every algorithm and
    /// uses each SKU's current price as the baseline, so only the margin-floor clamp and psychological
    /// rounding may change a price. Use it to run a layer on guardrails alone (no demand/inventory
    /// repricing). Default false — the full algorithm roster runs.
    /// </summary>
    public bool FloorAndRoundingOnly { get; set; }

    /// <summary>
    /// When true (the default, and the historical behaviour), platform-new products — those inside the
    /// MarkAsNew window (see the source query's <c>IsNewProduct</c>) — are IGNORED by a run: the engine
    /// holds them at their current price and never discounts or reprices them. Set it false to price new
    /// products like any other SKU — the run drops the new-product hold, so they flow through the full
    /// algorithm/guardrail pipeline. Applies to scheduled AND on-demand runs. Takes effect on the next run.
    /// </summary>
    public bool IgnoreNewProducts { get; set; } = true;

    /// <summary>
    /// When true, runs apply this layer's discount-optimization table (<see cref="DiscountTrimRule"/>, edited
    /// on the Price Bands tab): a price whose displayed discount off the shelf price falls in a range is raised
    /// so that discount is trimmed by the range's share — never past the no-raise ceiling, never on dead stock
    /// or a pinned/held price, flagged DISCOUNT_OPTIMIZED. Default false: switched on per layer from the
    /// Schedule page. Applies to scheduled AND on-demand runs; takes effect on the next run.
    /// </summary>
    public bool DiscountOptimizationEnabled { get; set; }

    // ---- Per-layer schedule (moved off the global ToolSettings) -------------------------------

    /// <summary>Daily run time in UTC, "HH:mm".</summary>
    public string RunTimeUtc { get; set; } = "03:00";

    /// <summary>Hours between scheduled runs (24 = daily).</summary>
    public int CadenceHours { get; set; } = 24;

    /// <summary>Set by the scheduler after each scheduled run for this layer.</summary>
    public DateTime? LastScheduledRunUtc { get; set; }

    /// <summary>Set after each elasticity fit for this layer (fit cadence = PricingEngineOptions.ElasticityFitCadenceDays).</summary>
    public DateTime? LastElasticityFitUtc { get; set; }

    /// <summary>
    /// Set after each slow-lane tick (a slow-lane-DUE scheduled/on-demand run). Drives the 15-day
    /// freeze for the dead-stock lane: the run computes slowLaneDue = null-or-older-than
    /// PricingEngineOptions.SlowLaneCadenceDays, and freezes the dead-stock pacer on the daily runs in
    /// between. NULL = due (a never-ticked layer engages on its first run).
    /// </summary>
    public DateTime? LastSlowLaneRunUtc { get; set; }

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}
