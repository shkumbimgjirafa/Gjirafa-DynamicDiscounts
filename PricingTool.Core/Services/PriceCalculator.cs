using PricingTool.Core.Domain;

namespace PricingTool.Core.Services;

/// <summary>
/// The per-SKU pricing pipeline: run enabled algorithms → weighted scoring →
/// guardrail clamp (always last before rounding) → psychological rounding.
/// </summary>
public class PriceCalculator
{
    private readonly WeightedScoringService _scoring;
    private readonly GuardrailService _guardrails;
    private readonly RoundingService _rounding;

    public PriceCalculator(WeightedScoringService scoring, GuardrailService guardrails, RoundingService rounding)
    {
        _scoring = scoring;
        _guardrails = guardrails;
        _rounding = rounding;
    }

    public PricingDecision Decide(SkuContext ctx, IEnumerable<IPricingAlgorithm> algorithms)
    {
        // New-product protection (hard rule): inside the platform MarkAsNew window the price is held
        // exactly as-is — no discount, no change — overriding every algorithm and guardrail.
        if (ctx.IsNewProduct)
        {
            return new PricingDecision
            {
                Sku = ctx.Sku,
                AnchorPrice = ctx.AnchorPrice,
                OldPrice = ctx.OldPrice,
                CurrentPrice = ctx.CurrentPrice,
                RawWeightedPrice = null,
                ClampedPrice = null,
                FinalPrice = ctx.CurrentPrice,
                Changed = false,
                GuardrailFlagsApplied = new[] { GuardrailFlags.NewProductProtected },
                ReasonCodes = new[] { GuardrailFlags.NewProductProtected },
            };
        }

        // Floor + rounding-only mode (per-layer): algorithms are switched off. The baseline is the
        // current price; only the margin-floor clamp and psychological rounding may move it.
        if (ctx.FloorAndRoundingOnly)
            return Finalize(ctx, ctx.CurrentPrice, rawWeighted: null,
                votes: Array.Empty<WeightedVote>(), reasonCodes: new[] { GuardrailFlags.AlgorithmsDisabled });

        var votes = new List<(IPricingAlgorithm, AlgorithmVote)>();
        foreach (var algorithm in algorithms)
        {
            if (!ctx.Band.GetAlgorithm(algorithm.Code).Enabled) continue;
            var vote = algorithm.Evaluate(ctx);
            if (vote is not null) votes.Add((algorithm, vote));
        }

        var scored = _scoring.Combine(ctx, votes);

        if (scored.RawPrice is null)
        {
            // Nothing voted, so there is no algorithm conclusion to clamp. The guardrails still have to be
            // CONSULTED, though: they constrain what we SELL at, not merely what the algorithms propose, and
            // their verdict on the live price is the only account of this row we can give. Called once — it
            // answers both questions below.
            var verdict = _guardrails.Clamp(ctx, ctx.CurrentPrice);

            // A current price under the floor (a campaign discount cutting past cost, a cost rise, a stale
            // anchor) would otherwise never reach the clamp at all — so when the clamp comes back HIGHER,
            // put the live price through the SAME clamp + rounding tail to lift it back to the floor.
            // Clearance-locked SKUs are exempt: that pin is an approved, deliberately below-cost clearance
            // price, and moving a silent SKU up to it is the clearance batch's business, not the floor's.
            if (ctx.Pptcv is > 0m && ctx.CurrentPrice > 0m && ctx.ClearanceLockPrice is null
                && verdict.Price > ctx.CurrentPrice)
            {
                // Recorded as BOTH a reason code (why this row moved, for the badge and the Algorithm
                // filter) and a guardrail flag (so the Proposals Guardrails filter — whose options are
                // reflected from GuardrailFlags — can actually select this set).
                var audit = new[] { GuardrailFlags.CurrentBelowFloorLifted };
                return Finalize(ctx, ctx.CurrentPrice, rawWeighted: null, votes: scored.Votes,
                    reasonCodes: audit, extraFlags: audit);
            }

            // Discount optimization, the same audit shape as the floor's: when the layer has it on and the
            // LIVE price's discount sits in one of its trim ranges, put the live price through the tail (which
            // applies the trim). Nothing else would ever reach these rows — no vote means no Finalize — and
            // they are where nearly every deep campaign discount lives. When rounding absorbs a small trim
            // nothing moved, so the row is left exactly as it was rather than re-rounded for no reason.
            if (OptimizedDiscountPrice(ctx, _guardrails.GetBounds(ctx), ctx.CurrentPrice) is not null)
            {
                var optimized = Finalize(ctx, ctx.CurrentPrice, rawWeighted: null, votes: scored.Votes,
                    reasonCodes: new[] { GuardrailFlags.DiscountOptimized });
                if (optimized.GuardrailFlagsApplied.Contains(GuardrailFlags.DiscountOptimized)
                    && optimized.FinalPrice > ctx.CurrentPrice)
                    return optimized;
            }

            return new PricingDecision
            {
                Sku = ctx.Sku,
                AnchorPrice = ctx.AnchorPrice,
                OldPrice = ctx.OldPrice,
                CurrentPrice = ctx.CurrentPrice,
                RawWeightedPrice = null,
                ClampedPrice = null,
                FinalPrice = ctx.CurrentPrice,
                Changed = false,
                Votes = scored.Votes,
                // Nothing moved — but say WHY when a guardrail is the reason nothing moved (see HoldFlags).
                GuardrailFlagsApplied = HoldFlags(verdict),
                ReasonCodes = Array.Empty<string>(),
            };
        }

        var reasonCodes = scored.Votes.Where(v => v.EffectiveWeight > 0)
            .Select(v => v.ReasonCode).Distinct().ToList();
        return Finalize(ctx, scored.RawPrice.Value, scored.RawPrice, scored.Votes, reasonCodes);
    }

    /// <summary>
    /// The guardrail flags that EXPLAIN why an algorithm-silent SKU is being left exactly where it is.
    ///
    /// <para>Without this, "no algorithm had an opinion" and "a guardrail is deliberately holding this
    /// price" were written identically — an empty row — so a reviewer had no way to tell a clearance-locked
    /// or tunnel-held price from an untouched one, and no way to find them: the Proposals Guardrails filter
    /// reflects its options from <see cref="GuardrailFlags"/> and can only match what was recorded. A price
    /// pinned below cost is exactly the case that most needs an account of itself.</para>
    ///
    /// <para>Only the HOLD/PIN flags are reported, because they are statements about the price staying put:
    /// the clearance-batch pin, the two dead-stock holds, the tunnel's floor relaxation, and the
    /// floor-above-anchor hold (a mispriced SKU whose margin floor can't be reached without a raise past
    /// the anchor, so it is held at the anchor). The clamped/capped flags are deliberately dropped — they
    /// describe a MOVE, and nothing moved here. A SKU no guardrail is acting on still yields an empty list,
    /// so this adds no noise to the ~99% of a full-catalog run that is genuinely uneventful.</para>
    /// </summary>
    private static IReadOnlyList<string> HoldFlags(ClampResult verdict)
    {
        var held = verdict.Flags.Where(HoldVocabulary.Contains).ToArray();
        return held.Length == 0 ? Array.Empty<string>() : held;
    }

    /// <summary>The flags that mean "a guardrail is holding this price", as opposed to having moved it.</summary>
    private static readonly HashSet<string> HoldVocabulary = new(StringComparer.Ordinal)
    {
        GuardrailFlags.ClearanceLockHeld,
        GuardrailFlags.DeadStockTunnelHeld,
        GuardrailFlags.DeadStockBelowStageHeld,
        GuardrailFlags.DeadStockFloorRelaxed,
        // Held at the anchor because the margin floor sits above it — the SKU is mispriced and needs a
        // human. Without this the hold would be an invisible no-change, no-flag row (the price stopped at
        // the current price, so the no-vote audit's lift never fires), and the Guardrails filter — whose
        // options reflect GuardrailFlags — could never surface it.
        GuardrailFlags.MarginFloorAboveAnchor,
    };

    /// <summary>
    /// Re-price an algorithm-silent ("free") SKU to the per-band margin-target guardrail's
    /// <paramref name="overrideBaseline"/>, running the SAME clamp (margin floor / anchor cap /
    /// dead-stock tunnel) and psychological rounding tail as the normal path — so the guardrail can
    /// never breach a floor or exceed the anchor, and a held/pinned SKU stays put. The decision carries
    /// no algorithm votes (the SKU had none) and stamps <paramref name="reasonCodes"/> for explainability.
    /// </summary>
    public PricingDecision Reprice(SkuContext ctx, decimal overrideBaseline, IReadOnlyList<string> reasonCodes)
        => Finalize(ctx, overrideBaseline, rawWeighted: null, votes: Array.Empty<WeightedVote>(), reasonCodes: reasonCodes);

    /// <summary>
    /// Shared tail for any priced SKU: clamp the <paramref name="baseline"/> to the guardrail bounds
    /// (margin floor / anchor cap / dead-stock tunnel), apply psychological rounding inside those
    /// bounds, then build the decision. The baseline is the algorithms' weighted price on the normal
    /// path, or the current price in floor + rounding-only mode.
    /// <paramref name="extraFlags"/> are recorded alongside the clamp's own flags, for a caller whose move
    /// is itself a guardrail action the clamp can't infer (see the floor audit in <see cref="Decide"/>).
    /// </summary>
    private PricingDecision Finalize(SkuContext ctx, decimal baseline, decimal? rawWeighted,
        IReadOnlyList<WeightedVote> votes, IReadOnlyList<string> reasonCodes,
        IReadOnlyList<string>? extraFlags = null)
    {
        var clamp = _guardrails.Clamp(ctx, baseline);
        var bounds = _guardrails.GetBounds(ctx);
        var flags = new List<string>(clamp.Flags);
        if (extraFlags is not null) flags.AddRange(extraFlags);

        var (final, roundingSkipped) = RoundWithinBounds(ctx, clamp.Price, bounds);

        // Discount optimization (per-layer, opt-in): a price whose displayed discount off the shelf (Old)
        // price falls in one of the layer's trim ranges has that discount trimmed by the range's share. The
        // trim is taken BEFORE rounding so the raised price still lands on the band's charm grid, and it is
        // bounded by the guardrail ceiling like every other raise — never past max(anchor, current). Only
        // flagged when it actually moved the rounded price: a small trim that rounding absorbs changed
        // nothing. Exempt: dead stock and pinned/held prices (see OptimizedDiscountPrice).
        if (OptimizedDiscountPrice(ctx, bounds, clamp.Price) is decimal optimized)
        {
            var (trimmed, trimmedSkipped) = RoundWithinBounds(ctx, optimized, bounds);
            if (trimmed > final)
            {
                final = trimmed;
                roundingSkipped = trimmedSkipped;
                flags.Add(GuardrailFlags.DiscountOptimized);
            }
        }
        if (roundingSkipped) flags.Add(GuardrailFlags.RoundingSkippedOutOfBounds);

        // Min-displayed-discount guardrail (pipeline tail, on the FINAL price): a token discount off the
        // shelf (Old) price isn't worth advertising, so if the proposed price would show a strike-through
        // smaller than the configured minimum it's held at the full Old price — no discount at all.
        // Deliberate below-shelf prices are exempt: dead stock (clearing markdowns) and pinned/held prices
        // (clearance lock, dead-stock holds), whose guardrail bounds have collapsed to a single point.
        //
        // The lift is BOUNDED BY THE GUARDRAIL CEILING like every other move in the pipeline. The shelf
        // price is a different source (TierPrice.OldPrice) from the anchor (ProductPricing.FinalPrice) and
        // is routinely the higher of the two, so snapping to it unbounded would push a SKU further ahead of
        // its anchor than it already sits — the one thing the engine must never do. When the ceiling blocks
        // the full lift the price stops there instead; if that leaves it exactly where it already was,
        // nothing moved and the flag is not raised.
        if (SuppressesTokenDiscount(ctx, bounds, final))
        {
            var lifted = Math.Min(ctx.OldPrice, bounds.Upper);
            if (lifted > final)
            {
                final = lifted;
                flags.Add(GuardrailFlags.MinDiscountSuppressed);
            }
        }

        return new PricingDecision
        {
            Sku = ctx.Sku,
            AnchorPrice = ctx.AnchorPrice,
            OldPrice = ctx.OldPrice,
            CurrentPrice = ctx.CurrentPrice,
            RawWeightedPrice = rawWeighted,
            ClampedPrice = clamp.Price,
            FinalPrice = final,
            Changed = Math.Abs(final - ctx.CurrentPrice) >= 0.01m,
            Votes = votes,
            GuardrailFlagsApplied = flags,
            ReasonCodes = reasonCodes,
        };
    }

    /// <summary>
    /// Psychological rounding inside the guardrail bounds, on the band's own convention. (GjirafaMall shared
    /// SKUs that must land on Gjirafa50's .50 grid are handled upstream by adopting Gjirafa50's exact proposal
    /// — see SYNCED_TO_GJ50 in PricingRunOrchestrator — so there is no per-SKU rounding override here.) The
    /// rounder is clamped to the bounds, so a held/pinned SKU (Lower == Upper) is a no-op.
    /// </summary>
    private (decimal Price, bool SkippedOutOfBounds) RoundWithinBounds(SkuContext ctx, decimal price, PriceBounds bounds)
    {
        if (!ctx.Band.RoundingEnabled || ctx.RoundingDisabledForSku)
            return (RoundingService.Normalize(price, bounds), false);
        var outcome = _rounding.Apply(price, ctx.Band.Rounding, bounds,
            ctx.Options.LowPriceRoundingThreshold, ctx.Options.CharmRelativePrecision);
        return (RoundingService.Normalize(outcome.Price, bounds), outcome.SkippedOutOfBounds);
    }

    /// <summary>
    /// The price the layer's discount-optimization table would raise <paramref name="price"/> to, bounded
    /// by the no-raise ceiling — or null when the layer has the feature off, the SKU is exempt, or nothing
    /// would move. Exempt: dead stock (its markdown is a deliberate clearing price) and any pinned/held price
    /// whose bounds collapsed to a point (clearance lock, dead-stock tunnel/stage holds, floor-above-anchor).
    /// </summary>
    private static decimal? OptimizedDiscountPrice(SkuContext ctx, PriceBounds bounds, decimal price)
    {
        if (ctx.DiscountTrimRanges is not { Count: > 0 } ranges) return null;
        if (ctx.IsDeadStock) return null;
        if (bounds.Lower == bounds.Upper) return null;
        if (DiscountOptimizer.TrimmedPrice(ctx.OldPrice, price, ranges) is not decimal target) return null;
        var capped = Math.Min(target, bounds.Upper);
        return capped > price ? capped : null;
    }

    /// <summary>
    /// True when <paramref name="final"/> is a positive discount off the shelf (Old) price that comes out
    /// smaller than <see cref="PricingEngineOptions.MinDisplayedDiscountPct"/> — a token markdown the
    /// storefront shouldn't bother showing — AND the SKU is one this rule may lift toward the full Old
    /// price. Exempt: the rule off (threshold ≤ 0), no shelf reference or no discount to remove
    /// (<c>final ≥ OldPrice</c>), dead stock (deliberate clearing markdowns), and pinned/held prices whose
    /// bounds collapsed to a point (clearance lock, dead-stock holds — never lifted to full price).
    /// How FAR the lift may go is the caller's business — it stops at the guardrail ceiling.
    /// </summary>
    private static bool SuppressesTokenDiscount(SkuContext ctx, PriceBounds bounds, decimal final)
    {
        var threshold = ctx.Options.MinDisplayedDiscountPct;
        if (threshold <= 0m) return false;
        if (ctx.OldPrice <= 0m || final >= ctx.OldPrice) return false;
        if (ctx.IsDeadStock) return false;
        if (bounds.Lower == bounds.Upper) return false;
        var discountPct = (ctx.OldPrice - final) / ctx.OldPrice * 100m;
        return discountPct < threshold;
    }
}
