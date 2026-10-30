using PricingTool.Core.Domain;

namespace PricingTool.Data.Entities;

public enum ProposalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Pushed = 3,

    /// <summary>SKU was excluded by policy (e.g. NULL cost) — flagged, never priced.</summary>
    Skipped = 4,
}

/// <summary>
/// The engine's output. THIS TABLE IS THE ONLY PLACE THE ENGINE WRITES PRICES — live platform
/// prices are untouched until a Manager explicitly approves and pushes (architecture rule 1).
/// </summary>
public class ProposedPrice
{
    public long Id { get; set; }
    public long PricingRunId { get; set; }
    public PricingRun PricingRun { get; set; } = null!;

    /// <summary>The layer this proposal belongs to (denormalised from the run for direct filtering).</summary>
    public int LayerId { get; set; }

    public string Sku { get; set; } = "";

    /// <summary>The operational Product.Id behind this SKU (nopCommerce Product.Id). Null on rows
    /// created before this was captured; populated for all new runs.</summary>
    public int? ProductId { get; set; }

    /// <summary>Product.Name at run time (display only). Denormalised like the other inputs so past
    /// runs stay readable after the day's snapshot is retention-trimmed. Null on rows created before
    /// this was captured.</summary>
    public string? ProductName { get; set; }

    /// <summary>The product's EAN/GTIN at run time, raw from the catalog. Denormalised like the other
    /// inputs so a past run stays auditable (and groupable by barcode) after the snapshot is retention-trimmed.
    /// Null when the product carries none (or on rows written before this was captured). Not every value here
    /// is a real barcode: the catalog also stores sentinels ('PARENT', '&lt;NO-GTIN').</summary>
    public string? Gtin { get; set; }

    public int? PriceBandId { get; set; }

    /// <summary>Supplier (store) at run time — GjirafaTranslations.ZStores.Id via ProductStoreMapping.
    /// Denormalised like the other inputs so past runs stay auditable (and filterable) even after the
    /// day's snapshot is retention-trimmed. Null on rows created before this was captured.</summary>
    public int? SupplierId { get; set; }
    /// <summary>ZStores.Store — the supplier's display name at run time. Null when unmapped.</summary>
    public string? SupplierName { get; set; }

    /// <summary>Brand (manufacturer) at run time — the primary Manufacturer.Id via
    /// Product_Manufacturer_Mapping. Denormalised like the other inputs so past runs stay auditable
    /// (and filterable) even after the day's snapshot is retention-trimmed. Null when unmapped.</summary>
    public int? BrandId { get; set; }
    /// <summary>Manufacturer.Name — the brand's display name at run time. Null when the product has
    /// no manufacturer.</summary>
    public string? Brand { get; set; }

    /// <summary>Display-only shelf price (TierPrice.OldPrice).</summary>
    public decimal OldPrice { get; set; }
    /// <summary>The anchor price (ProductPricing.FinalPrice) that drove the discount math; the price ceiling is the higher of this anchor and the current price.</summary>
    public decimal AnchorPrice { get; set; }
    public decimal CurrentPrice { get; set; }

    /// <summary>Comma-separated ids of the active customer-facing campaign discounts on this SKU at run
    /// time — the same eligibility that built <see cref="CurrentPrice"/> (DiscountApplyTypeId = 2, no
    /// coupon, currently live), best deal first (the first id is the one setting the current price).
    /// Display/audit only — it never feeds the pricing math. Null when the SKU has no such discount (or
    /// on rows created before this was captured).</summary>
    public string? ActiveDiscountIds { get; set; }

    /// <summary>PPTCV (cost) at run time — the value the price band was selected on. Null when unknown.</summary>
    public decimal? Pptcv { get; set; }

    /// <summary>Weighted-average of votes before guardrails/rounding; null when no algorithm voted.</summary>
    public decimal? RawWeightedPrice { get; set; }

    /// <summary>
    /// The fitted log-log price elasticity the ELASTICITY vote acted on for this SKU at run time (and
    /// its standard error). Persisted here — rather than re-read from SkuElasticity, which is overwritten
    /// every fit — so Proposals Review can re-project expected volume from the SAME coefficient
    /// that drove the run. Null when the elastic algorithm did not vote on this SKU.
    /// </summary>
    public decimal? Elasticity { get; set; }
    public decimal? ElasticityStdError { get; set; }

    public decimal ProposedPriceValue { get; set; }
    public decimal ChangePct { get; set; }

    /// <summary>
    /// DB-computed |ChangePct|. Lets the proposals listing sort/filter by change magnitude via an
    /// index (full-catalog runs can have 500k+ changed rows; sorting them live times out).
    /// </summary>
    public decimal AbsChangePct { get; private set; }

    public bool HasChange { get; set; }

    /// <summary>
    /// Set when a Manager manually overrides the proposed price on the Proposals Review screen.
    /// The override is written into <see cref="ProposedPriceValue"/> (so every downstream reader —
    /// Push, Export, margins, KPIs, Movers — uses it verbatim), and the engine's ORIGINAL proposal
    /// is preserved here so the manual edit stays auditable. Null = never overridden.
    /// </summary>
    public decimal? EngineProposedPrice { get; set; }
    public string? ManualOverrideBy { get; set; }
    public DateTime? ManualOverrideUtc { get; set; }

    /// <summary>Comma-separated winning reason codes, strongest first.</summary>
    public string ReasonCodes { get; set; } = "";

    /// <summary>Comma-separated guardrail flags applied during clamping/rounding.</summary>
    public string GuardrailFlags { get; set; } = "";

    public ProposalStatus Status { get; set; }
    public string? SkipReason { get; set; }

    /// <summary>
    /// True when the SKU was platform-new (inside the MarkAsNew window) at run time — the TRUE source
    /// signal, stamped independently of whether the layer held it. When the layer ignores new products
    /// (<c>Layer.IgnoreNewProducts</c>) the row also carries the NEW_PRODUCT_PROTECTED reason and is held;
    /// when it opts to price them the row is priced normally but this flag is still set, so "new products"
    /// stays filterable either way. Historical rows written before this column existed read false.
    /// </summary>
    public bool IsNewProduct { get; set; }

    /// <summary>
    /// The SKU's dead-stock age tier at run time (None = not dead — or a row persisted before stages
    /// existed). Stamped by the orchestrator because it is not reconstructible later: the WMS
    /// oldest-unit age it derives from is never persisted. Stamped on every priced row — including
    /// FloorAndRoundingOnly layers, where the dead-stock algorithm never votes — so dead-stock
    /// stages and margins stay queryable per run.
    /// </summary>
    public DeadStockStage DeadStockStage { get; set; }

    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedUtc { get; set; }
    public DateTime? PushedUtc { get; set; }

    public List<AlgorithmVoteRecord> Votes { get; set; } = new();
}
