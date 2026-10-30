using PricingTool.Core.Domain;

namespace PricingTool.Core.Services;

public record PriceBounds(decimal Lower, decimal Upper);

public record ClampResult(decimal Price, IReadOnlyList<string> Flags);

/// <summary>
/// Hard filters applied after scoring and before rounding: the margin floor defines the lower
/// bound; the top is capped at the higher of the anchor price (ProductPricing.FinalPrice) and the
/// current price — the tool proposes discounts and may raise back toward the anchor, but it never forces
/// a SKU below where it already sits. There is no discount ceiling — discounts go as deep as the floor allows.
///
/// The locally-held dead-stock "tunnel" lives here, keyed off local stock so that every algorithm
/// (current and future) is covered at once rather than relying on each to be stock-aware:
///  - Locally-held dead stock (no 90-day sales) is the ONE case allowed to pierce the margin floor.
///    Its progressive markdown may run below the floor, down to a percent-of-cost floor that deepens
///    with the stock's age tier (<see cref="SkuContext.DeadStockStage"/>): at the per-band defaults,
///    100% of cost at 90–180 days ("3–6 months"), 80% at 180–360 days ("6–12 months"), 60% at
///    360–1080 days ("12–36 months"), and 40% past 1080 days ("36 months+") — the longer inventory we
///    physically hold sits dead, the more of its cost we give up to clear it. And once such a
///    below-floor price finally starts selling again it is HELD at that level, never raised back — it
///    stays at the price that moved units. That hold requires PROVENANCE (the lane recorded the
///    below-floor price on <c>SkuDeadStockState.BelowFloorTunnelPrice</c>), so a price that fell under a
///    floor that merely MOVED — a cost rise, a stale anchor — is repaired by the margin floor as usual
///    instead of being mistaken for a deliberate clearance markdown.
///  - A dead SKU that ALREADY sits below its current stage's floor (a manual/promo markdown, a cost
///    rise, a stale anchor) is not raised back up to that floor: it is HELD where it is (never raised,
///    never cut further) until the stock ages into a deeper stage whose floor drops to/below its price,
///    at which point the markdown resumes cutting from there. The single hard limit is the deepest
///    (stage-4) cost floor — no dead-stock price may ever sit below it; one that does is pulled up to it.
///
/// Supplier-only stock (none held locally) is NOT frozen here: the cross-dock lane prices it
/// deliberately, so the normal margin-floor clamp is its only lower bound — a markdown may run down to
/// the margin floor but never below it (nothing is sunk to recover, unlike locally-held dead stock).
/// </summary>
public class GuardrailService
{
    /// <summary>
    /// Lower bound = the margin-floor price (0 when cost is unknown), with two dead-stock exceptions:
    /// raised to the current price for supplier-only dead stock (no markdown), or lowered to the
    /// dead-stock cost-fraction floor for locally-held stock that isn't selling. A locally-held
    /// below-floor price that has started selling again is pinned (lower == upper == current).
    /// Upper bound = the higher of the anchor and the current price — a hard cap the proposed price never
    /// passes. When the margin floor sits above that ceiling (a mispriced, at/under-cost SKU) the bounds
    /// collapse to the ceiling instead of letting the floor win: we hold at the anchor and flag it.
    /// </summary>
    public PriceBounds GetBounds(SkuContext ctx)
    {
        // Clearance lock (highest precedence): a SKU in an active approved batch is pinned at its
        // approved pushed price until its local stock clears (release) or the batch is ended — nothing
        // raises or deepens it. Pinned bounds make the rounder a no-op, like the dead-stock held paths.
        if (ClearancePin(ctx) is decimal pinned)
            return new PriceBounds(pinned, pinned);

        var lower = MarginFloor(ctx);

        if (IsLocalDeadStock(ctx))
        {
            var stageFloor = DeadStockFloor(ctx);

            if (ctx.Qty90 == 0)
            {
                if (ctx.CurrentPrice < stageFloor)
                {
                    // Already below THIS stage's floor and still not selling: hold it exactly (never
                    // raised back to the stage floor) until it ages into a deeper stage. Pinned so
                    // nothing moves it, but never below the absolute stage-4 floor.
                    var held = Math.Max(ctx.CurrentPrice, DeadStockHardFloor(ctx));
                    return new PriceBounds(held, held);
                }

                // Still not selling — the markdown may run below the margin floor, down to the
                // dead-stock cost-fraction floor for the current stage.
                lower = Math.Min(lower, stageFloor);
            }
            else if (IsHeldTunnelPrice(ctx, lower))
            {
                // Selling again at a below-floor "tunnel" price (with an achievable floor): pin it
                // exactly so rounding can't drift it and nothing raises it back toward the floor —
                // but never below the absolute stage-4 floor.
                var held = Math.Max(ctx.CurrentPrice, DeadStockHardFloor(ctx));
                return new PriceBounds(held, held);
            }
        }

        // The ceiling is the higher of the anchor and the current price: the tool proposes discounts and
        // may raise a price back toward the anchor, but it never forces a SKU below where it already sits,
        // and it never carries a price ABOVE that ceiling — not even to reach the margin floor. When the
        // floor itself sits above the ceiling the SKU is mispriced (at/under cost): the bounds collapse to
        // the ceiling (a pinned point) so nothing — clamp, rounding, or the min-discount lift — can move it
        // up past the anchor, and MARGIN_FLOOR_ABOVE_ANCHOR flags it for a human.
        var ceiling = Math.Max(ctx.AnchorPrice, ctx.CurrentPrice);
        return new PriceBounds(Math.Min(lower, ceiling), ceiling);
    }

    public ClampResult Clamp(SkuContext ctx, decimal rawPrice)
    {
        var flags = new List<string>();

        // Clearance lock (highest precedence): pin an active-batch SKU at its approved pushed price,
        // overriding any algorithm/margin-target raise and any dead-stock deepening. Bounded by the no-raise
        // ceiling and, below, by the dead-stock absolute floor (locally-held) or the margin floor — see
        // ClearancePin. A locally-held below-cost clearance price is HELD, not clamped up to the margin floor.
        if (ClearancePin(ctx) is decimal pinned)
        {
            flags.Add(GuardrailFlags.ClearanceLockHeld);
            if (ctx.ClearanceLockPrice is decimal pushed && pinned > pushed) // pushed sat below the floor; floor won
                flags.Add(IsLocalDeadStock(ctx) ? GuardrailFlags.DeadStockHardFloorClamped : GuardrailFlags.MarginFloorClamped);
            return new ClampResult(pinned, flags);
        }

        var marginFloor = MarginFloor(ctx);

        var price = rawPrice;

        // Locally-held dead stock (no 90-day sales): the one exception to the margin floor.
        if (IsLocalDeadStock(ctx))
        {
            var stageFloor = DeadStockFloor(ctx);

            if (ctx.Qty90 == 0)
            {
                if (ctx.CurrentPrice < stageFloor)
                {
                    // Already below what THIS stage's floor permits (a manual/promo markdown, a cost
                    // rise, a stale anchor). Don't raise it back to the stage floor and don't cut it
                    // further — hold it exactly, and let the stock age into a deeper stage whose floor
                    // drops to/below this price before the markdown resumes. The one hard limit: never
                    // below the deepest (stage-4) floor — a price below that is an error, pull it up.
                    return HoldBelowStageFloor(ctx, GuardrailFlags.DeadStockBelowStageHeld, flags);
                }

                // The "tunnel": let the markdown pierce the margin floor, but no deeper than the
                // dead-stock cost-fraction floor for the current stage (default 100% of cost —
                // never below cost while young). Bypasses the normal-floor clamp.
                if (price < marginFloor)
                {
                    flags.Add(GuardrailFlags.DeadStockFloorRelaxed);
                    if (price < stageFloor) price = stageFloor;
                }
                return ApplyCeiling(ctx, price, marginFloor, flags);
            }

            if (IsHeldTunnelPrice(ctx, marginFloor))
            {
                // It has started selling again at a below-floor tunnel price → hold it exactly.
                // Don't raise it back toward the floor; that price is finally moving units.
                // Still never below the absolute stage-4 floor.
                return HoldBelowStageFloor(ctx, GuardrailFlags.DeadStockTunnelHeld, flags);
            }
        }

        if (price < marginFloor)
        {
            price = marginFloor;
            flags.Add(GuardrailFlags.MarginFloorClamped);
        }

        return ApplyCeiling(ctx, price, marginFloor, flags);
    }

    /// <summary>The no-raise ceiling — the higher of anchor and current price — (plus the mispriced "floor above ceiling" alert), shared by every clamp path.</summary>
    private static ClampResult ApplyCeiling(SkuContext ctx, decimal price, decimal marginFloor, List<string> flags)
    {
        // No-raise ceiling: the higher of the anchor (full reference price) and the current price. The tool
        // proposes discounts — it may raise a price back up toward the anchor, but it must never force a SKU
        // below the price it's already selling at, NOR carry it ABOVE the anchor. This cap is absolute: the
        // proposed price never passes max(anchor, current), not even to reach the margin floor.
        var ceiling = Math.Max(ctx.AnchorPrice, ctx.CurrentPrice);
        if (price <= ceiling)
            return new ClampResult(price, flags);

        price = ceiling;

        if (marginFloor > ceiling)
        {
            // The margin floor itself sits above the ceiling: the SKU is mispriced (selling at or under
            // cost), and no price clears the floor without exceeding the anchor — which we never do. Hold
            // at the ceiling and flag loudly for a human. Do NOT claim a margin-floor clamp: the price
            // stops SHORT of the floor, so any MarginFloorClamped added upstream (the floor clamp in
            // Clamp) is undone here — MARGIN_FLOOR_ABOVE_ANCHOR is the whole story.
            flags.Remove(GuardrailFlags.MarginFloorClamped);
            if (!flags.Contains(GuardrailFlags.MarginFloorAboveAnchor))
                flags.Add(GuardrailFlags.MarginFloorAboveAnchor);
        }
        else
        {
            flags.Add(GuardrailFlags.CappedAtAnchor);
        }

        return new ClampResult(price, flags);
    }

    /// <summary>
    /// The clearance-lock pin (or null when the SKU isn't under an active clearance lock): the price to
    /// hold a clearance-batch member at — the approved pushed price, never raised, bounded above by the
    /// no-raise ceiling (higher of anchor/current) and below by a floor chosen for the SKU. For
    /// locally-held clearance stock the lower bound is the dead-stock tunnel's absolute (stage-4) floor,
    /// NOT the margin floor: a clearance batch is built from dead-stock markdowns whose approved prices sit
    /// below cost by construction, so bounding at the margin floor would clamp them UP and undo the
    /// clearance — deep below-cost clearance is the tunnel's domain, so the lock holds the approved price
    /// down to the same absolute floor the tunnel respects. A non-locally-held member (no local stock /
    /// fresh / new) falls back to the ordinary margin floor. A pushed price already inside the bounds is
    /// held exactly.
    /// </summary>
    private static decimal? ClearancePin(SkuContext ctx)
    {
        if (ctx.ClearanceLockPrice is not decimal pushed) return null;
        var ceiling = Math.Max(ctx.AnchorPrice, ctx.CurrentPrice);
        // Locally-held clearance stock holds down to the dead-stock absolute floor so a genuine below-cost
        // clearance price is preserved, not raised to the margin floor; others use the margin floor.
        var floor = IsLocalDeadStock(ctx) ? DeadStockHardFloor(ctx) : MarginFloor(ctx);
        var lower = Math.Min(floor, ceiling); // mispriced (floor > ceiling): ceiling wins
        // The lock is a HOLD, never a raise. Where the shopper is already paying LESS than the approved
        // pushed price — a deeper promo, a later manual markdown — pegging back up to the batch price
        // would raise a clearance price on stock we are still trying to clear, which is the exact move
        // the lock exists to prevent. Hold what the customer sees today instead; the floor still binds.
        return Math.Clamp(Math.Min(pushed, ctx.CurrentPrice), lower, ceiling);
    }

    /// <summary>
    /// The band's margin-floor price for this SKU — the lowest price that still clears the floor margin on
    /// the all-in cost, or 0 when cost is unknown (never assume a floor we can't compute). Public so the
    /// orchestrator can decide, on the same basis the clamp uses, whether a priced dead SKU landed below
    /// the floor and therefore owes a <see cref="SkuContext.BelowFloorTunnelPrice"/> marker.
    /// </summary>
    public static decimal MarginFloor(SkuContext ctx) =>
        ctx.Pptcv.HasValue
            ? VatMath.MinGrossPriceForMargin(ctx.Pptcv.Value, ctx.Band.MarginFloorPct)
            : 0m;

    /// <summary>
    /// The dead-stock markdown floor: a percent of the all-in unit cost, selected by the SKU's age
    /// tier (per band — defaults 100% at stage 1, 80% at stage 2, 60% at stage 3: the longer stock
    /// sits dead, the deeper below cost it may clear). (Pptcv is already VAT-inclusive, so this is
    /// directly a selling price.) <see cref="DeadStockStage.None"/> falls back to the stage-1 pct
    /// defensively — the tunnel being open implies the SKU is dead, so None is unreachable here.
    /// </summary>
    private static decimal DeadStockFloor(SkuContext ctx)
    {
        if (ctx.Pptcv is not decimal cost) return 0m;
        var pct = ctx.DeadStockStage switch
        {
            DeadStockStage.Stage4 => ctx.Band.DeadStockStage4FloorCostPct,
            DeadStockStage.Stage3 => ctx.Band.DeadStockStage3FloorCostPct,
            DeadStockStage.Stage2 => ctx.Band.DeadStockStage2FloorCostPct,
            _ => ctx.Band.DeadStockFloorCostPct,
        };
        return cost * (pct / 100m);
    }

    /// <summary>
    /// The absolute deepest a dead-stock price may ever sit: the stage-4 cost fraction (default 40% of
    /// cost — "~60% below cost"), regardless of the SKU's current stage. Even the held paths below are
    /// pinned at or above this; a price below it is treated as an error and pulled back up to it.
    /// 0 when cost is unknown.
    /// </summary>
    private static decimal DeadStockHardFloor(SkuContext ctx) =>
        ctx.Pptcv is decimal cost ? cost * (ctx.Band.DeadStockStage4FloorCostPct / 100m) : 0m;

    /// <summary>
    /// Hold a below-floor dead-stock SKU at its current price without raising it back — the shared body
    /// of the "below the current stage's floor" and "started selling again" holds. Pinned at the current
    /// price, except it is pulled UP to the absolute deepest (stage-4) floor if it somehow sits below it.
    /// </summary>
    private static ClampResult HoldBelowStageFloor(SkuContext ctx, string holdFlag, List<string> flags)
    {
        flags.Add(holdFlag);
        var hardFloor = DeadStockHardFloor(ctx);
        if (ctx.CurrentPrice < hardFloor)
        {
            flags.Add(GuardrailFlags.DeadStockHardFloorClamped);
            return new ClampResult(hardFloor, flags);
        }
        return new ClampResult(ctx.CurrentPrice, flags);
    }

    /// <summary>
    /// Locally-held stock we may clear at a loss: held in our own warehouse, with a known cost, not a
    /// platform-new product (those are held by the new-product guardrail anyway), and not freshly stocked
    /// (a just-arrived pre-order/restock shouldn't get the below-floor tunnel either — same gate the
    /// dead-stock algorithm uses). Combined with the 90-day no-sales test inside the guardrail this is the
    /// dead-stock "tunnel" — the only case allowed below the margin floor. NOTE that this test says nothing
    /// about sales: the "started selling again" hold needs <see cref="IsHeldTunnelPrice"/> on top of it.
    /// </summary>
    public static bool IsLocalDeadStock(SkuContext ctx) =>
        ctx.KsStock > 0 && ctx.Pptcv is > 0m && !ctx.IsNewProduct && !ctx.IsFreshlyStocked;

    /// <summary>
    /// The "started selling again" hold: true when this SKU's below-floor price is one the DEAD-STOCK LANE
    /// put there, and is therefore protected from being clawed back now that it is moving units again.
    ///
    /// <para>Three things must hold: the price sits below the margin floor; that floor is achievable at all
    /// (<paramref name="marginFloor"/> ≤ anchor — a floor above even the anchor means a fundamentally
    /// mispriced SKU, which still needs the human alert rather than a silent hold); and the dead-stock lane
    /// actually recorded a below-floor price for this SKU
    /// (<see cref="SkuContext.BelowFloorTunnelPrice"/>).</para>
    ///
    /// <para>That last test is the point. Without it the hold fired on nothing more than "locally-held stock
    /// older than the freshness gate + selling + below floor" — which equally describes a SKU whose price
    /// never moved and whose FLOOR did (a PPTCV rise, a stale anchor). Those are precisely the breaches the
    /// margin-floor audit exists to repair, and pinning them made the repair impossible AND invisible: the
    /// pin makes <see cref="Clamp"/> a no-op, so <c>PriceCalculator</c>'s no-vote audit sees no raise and
    /// writes the row with no guardrail flags at all.</para>
    /// </summary>
    private static bool IsHeldTunnelPrice(SkuContext ctx, decimal marginFloor) =>
        ctx.CurrentPrice < marginFloor
        && marginFloor <= ctx.AnchorPrice
        && ctx.BelowFloorTunnelPrice is not null;
}
