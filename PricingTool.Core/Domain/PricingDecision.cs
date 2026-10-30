namespace PricingTool.Core.Domain;

/// <summary>One algorithm vote enriched with the band weight that was applied to it.</summary>
public record WeightedVote(
    string AlgorithmCode,
    decimal SuggestedPrice,
    decimal Confidence,
    int BandWeight,
    decimal EffectiveWeight,
    string ReasonCode,
    string ReasonText);

/// <summary>Guardrail / pipeline flags recorded on a proposal.</summary>
public static class GuardrailFlags
{
    public const string MarginFloorClamped = "MARGIN_FLOOR_CLAMPED";
    /// <summary>The price was capped at the no-raise ceiling — the higher of the anchor (FinalPrice) and the current price. (Flag code kept as CAPPED_AT_ANCHOR for historical continuity.)</summary>
    public const string CappedAtAnchor = "CAPPED_AT_ANCHOR";
    /// <summary>The margin floor sits above the no-raise ceiling (the higher of anchor and current price) —
    /// the SKU is fundamentally mispriced (selling at/under cost). The price is HELD at the ceiling rather
    /// than raised to the floor (the tool never prices above the anchor), and this flags it for a human.</summary>
    public const string MarginFloorAboveAnchor = "MARGIN_FLOOR_ABOVE_ANCHOR";
    /// <summary>FinalPrice was missing/zero so the anchor fell back to the shelf OldPrice — the cap may be based on an inflated reference.</summary>
    public const string AnchorFallbackToShelf = "ANCHOR_FALLBACK_TO_SHELF";
    public const string RoundingSkippedOutOfBounds = "ROUNDING_SKIPPED_OUT_OF_BOUNDS";
    /// <summary>The proposed discount off the shelf (Old) price came out below the engine's minimum
    /// (<see cref="Options.PricingEngineOptions.MinDisplayedDiscountPct"/>) — a token markdown not worth
    /// showing — so the price was held at the full Old price (no discount displayed), or at the no-raise
    /// ceiling when the shelf price sits above it (an inflated shelf price must not carry the SKU further
    /// ahead of its anchor).</summary>
    public const string MinDiscountSuppressed = "MIN_DISCOUNT_SUPPRESSED";
    /// <summary>Stock sits only in supplier warehouses and isn't selling — a proposed markdown was blocked; we don't discount stock we don't hold locally.</summary>
    public const string SupplierOnlyNoMarkdown = "SUPPLIER_ONLY_NO_MARKDOWN";
    /// <summary>Inside the platform MarkAsNew window — price held as-is (no discount, no change).</summary>
    public const string NewProductProtected = "NEW_PRODUCT_PROTECTED";
    /// <summary>Layer is in floor + rounding-only mode — algorithms were skipped; only the margin floor and rounding could move the price.</summary>
    public const string AlgorithmsDisabled = "ALGORITHMS_DISABLED";
    /// <summary>The SKU is carried by the same-country Gjirafa50 layer, so this GjirafaMall proposal
    /// ADOPTS Gjirafa50's latest proposed price verbatim (the product is primarily a Gjirafa50 product;
    /// shared warehouse, externally-synced prices) — Mall's own algorithms, guardrails and rounding are
    /// all skipped for it.</summary>
    public const string SyncedToGj50 = "SYNCED_TO_GJ50";
    /// <summary>Locally-held dead stock (no 90-day sales): the markdown was allowed below the margin floor — down to the dead-stock cost-fraction floor — to clear it at a loss.</summary>
    public const string DeadStockFloorRelaxed = "DEAD_STOCK_FLOOR_RELAXED";
    /// <summary>A below-floor dead-stock price started selling again — held at the current price (kept in the markdown "tunnel", never raised back).</summary>
    public const string DeadStockTunnelHeld = "DEAD_STOCK_TUNNEL_HELD";
    /// <summary>A dead SKU already sits below its CURRENT age stage's floor — held at the current price (not raised back to the stage floor); the markdown resumes only once it ages into a deeper stage whose floor drops to/below this price.</summary>
    public const string DeadStockBelowStageHeld = "DEAD_STOCK_BELOW_STAGE_HELD";
    /// <summary>A dead-stock price sat below the absolute deepest (stage-4) cost floor and was pulled back up to it — nothing may ever sell that far below cost.</summary>
    public const string DeadStockHardFloorClamped = "DEAD_STOCK_HARD_FLOOR_CLAMPED";
    /// <summary>The SKU is under an active approved clearance-batch lock: its price is pinned at the approved pushed price — held there (never raised back toward the anchor, never deepened past it) until its local stock clears or a Manager ends the batch.</summary>
    public const string ClearanceLockHeld = "CLEARANCE_LOCK_HELD";
    /// <summary>The per-band margin-target guardrail moved this algorithm-silent SKU to help its band reach its blended margin goal, within the floor/anchor bounds.</summary>
    public const string MarginTargetAdjusted = "MARGIN_TARGET_ADJUSTED";
    /// <summary>Reason code: the margin-target guardrail trimmed (discounted) this SKU toward its band's goal.</summary>
    public const string MarginTargetTrim = "MARGIN_TARGET_TRIM";
    /// <summary>Reason code: the margin-target guardrail raised this SKU toward its band's goal.</summary>
    public const string MarginTargetLift = "MARGIN_TARGET_LIFT";
    /// <summary>A band's margin goal could not be reached within the free SKUs' floor/anchor headroom — the guardrail moved as far as it could (diagnostic; logged per run, not persisted per SKU).</summary>
    public const string MarginTargetUnreachable = "MARGIN_TARGET_UNREACHABLE";
    /// <summary>Reason code: no algorithm voted, but the SKU's CURRENT selling price sat below the band's
    /// margin floor (a campaign discount cutting past cost, a cost rise, a stale anchor), so it was lifted
    /// back to the floor. Marks a floor REPAIR of a live under-water price, as opposed to
    /// <see cref="MarginFloorClamped"/>, which records the same clamp applied to a price an algorithm proposed.</summary>
    public const string CurrentBelowFloorLifted = "CURRENT_BELOW_FLOOR_LIFTED";
    /// <summary>RETIRED — the GTIN price-parity feature was removed; nothing produces this flag any more. Kept
    /// only so proposals saved while it ran still display a friendly label (see ReasonCodeText). It meant: the
    /// same physical product (same EAN/GTIN) was listed under another SKU at a lower proposed price, so this
    /// listing was brought DOWN to it (never past the SKU's own margin floor).</summary>
    public const string GtinParityMatched = "GTIN_PARITY_MATCHED";
    /// <summary>RETIRED — see <see cref="GtinParityMatched"/>; kept for historical rows only. It meant: a
    /// GTIN-parity match stopped at the SKU's own margin floor because that floor sat above its cheaper twin's
    /// price, so the two listings still differed.</summary>
    public const string GtinParityFloorBlocked = "GTIN_PARITY_FLOOR_BLOCKED";
    /// <summary>Discount optimization (per-layer, opt-in on the Schedule page): the price would have shown a
    /// displayed discount deep enough to fall in one of the layer's trim ranges, so that discount was trimmed
    /// by the range's share — the price was raised, never past the no-raise ceiling (the higher of anchor and
    /// current). Recorded as a flag on any row it moved, and also as the reason code when no algorithm voted
    /// (the trim was applied to the live price, like <see cref="CurrentBelowFloorLifted"/>).</summary>
    public const string DiscountOptimized = "DISCOUNT_OPTIMIZED";
}

/// <summary>Reasons a SKU was excluded from pricing, recorded on a skipped proposal row.</summary>
public static class SkipReasons
{
    public const string MissingCost = "MISSING_COST";
    public const string MissingPrice = "MISSING_PRICE";
    public const string NoBand = "NO_BAND";
}

/// <summary>The full outcome of pricing one SKU in one run.</summary>
public class PricingDecision
{
    public required string Sku { get; init; }

    /// <summary>The anchor price (ProductPricing.FinalPrice) that drove the discount math and the cap.</summary>
    public decimal AnchorPrice { get; init; }

    /// <summary>Display-only shelf price (TierPrice.OldPrice).</summary>
    public decimal OldPrice { get; init; }
    public decimal CurrentPrice { get; init; }

    /// <summary>Weighted average of votes before guardrails/rounding. Null when no algorithm voted.</summary>
    public decimal? RawWeightedPrice { get; init; }

    /// <summary>Price after guardrail clamping, before rounding.</summary>
    public decimal? ClampedPrice { get; init; }

    /// <summary>The proposed shelf price (always set; equals CurrentPrice when nothing voted).</summary>
    public decimal FinalPrice { get; init; }

    public bool Changed { get; init; }
    public IReadOnlyList<WeightedVote> Votes { get; init; } = Array.Empty<WeightedVote>();
    public IReadOnlyList<string> GuardrailFlagsApplied { get; init; } = Array.Empty<string>();

    /// <summary>Reason codes of the participating votes, strongest effective weight first.</summary>
    public IReadOnlyList<string> ReasonCodes { get; init; } = Array.Empty<string>();

    public decimal ChangePct =>
        CurrentPrice > 0 ? (FinalPrice - CurrentPrice) / CurrentPrice * 100m : 0;
}
