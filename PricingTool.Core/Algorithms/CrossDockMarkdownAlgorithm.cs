using PricingTool.Core.Domain;
using PricingTool.Core.Services;

namespace PricingTool.Core.Algorithms;

/// <summary>
/// Cross-dock (supplier-fulfilled) markdown — the lane for SKUs we DON'T hold locally but sell by
/// ordering from a supplier once a customer buys (KsStock == 0, SupplierStock &gt; 0). SELL_THROUGH and
/// DEAD_STOCK both abstain on these (they gate on locally-held stock), and elasticity often lacks the
/// dense price history to fit, so without this lane a selling supplier SKU never gets a recommendation
/// and just sits at its anchor price forever.
///
/// A sell-through/dead-stock HYBRID, driven by sales status because there is no on-hand denominator:
///  - Branch A (selling, Qty90 &gt; 0): HOLD the working price. The discount that produced the sale is
///    load-bearing — clawing it back would likely kill the demand it unlocked — so holding is an ACTIVE
///    vote at the current price (a concrete number that anchors the blend, rather than abstaining and
///    letting any co-voting algorithm average the working discount away). Only deepen further when
///    velocity is decaying; never raise.
///  - Branch B (not selling, Qty90 == 0): a soft progressive markdown from the current price toward the
///    band margin floor as the zero-sale streak grows. Softer than dead-stock (gentler start, slower
///    cadence). Unlike dead-stock it is bounded by the NORMAL margin floor, never the 50%-of-cost tunnel:
///    nothing is sunk (we never bought the unit), so there is no capital to recover below the floor —
///    a sale below it would just lose money on every fulfilled order.
///
/// The lane is monotonic: it never raises the price except the one floor-protection lift (a SKU already
/// priced below the margin floor is corrected UP to it). When a tunnelled SKU finally sells, Qty90 turns
/// positive and Branch A holds the achieved price — the markdown is sticky, the progress is kept.
///
/// It votes at a deliberately modest weight so that when elasticity (a stronger signal) also fires, the
/// weighted blend defers to elasticity; when elasticity is absent — the common case for supplier SKUs —
/// this lane carries the vote.
/// </summary>
public class CrossDockMarkdownAlgorithm : IPricingAlgorithm
{
    private const decimal HoldConfidence = 0.7m;    // selling: hold the working price as an active vote that anchors the blend
    private const decimal TunnelConfidence = 0.8m;  // not selling / floor lift: drive the markdown (matches dead-stock)
    private const decimal DecayRatio = 0.5m;        // Velocity7 <= 0.5 × Velocity90 reads as decaying demand
    private const decimal HealthyMarginBufferPct = 5m; // only defend (deepen) when comfortably above the floor

    public string Code => AlgorithmCodes.CrossDock;
    public string DisplayName => "Cross-dock (supplier-fulfilled) markdown";

    public AlgorithmVote? Evaluate(SkuContext ctx)
    {
        // Supplier-only fulfilment only: we hold nothing locally but can sell from supplier stock.
        // Locally-held stock is SELL_THROUGH / DEAD_STOCK's lane.
        if (ctx.KsStock > 0 || ctx.SupplierStock <= 0) return null;
        // New products are held by the engine (NEW_PRODUCT_PROTECTED) — never vote on them.
        if (ctx.IsNewProduct) return null;
        // No cost → no margin floor to bound the markdown by; abstain rather than vote unbounded.
        if (ctx.Pptcv is not > 0m) return null;

        var floor = VatMath.MinGrossPriceForMargin(ctx.Pptcv.Value, ctx.Band.MarginFloorPct);

        // Already at or below the margin floor (underwater / mispriced): lift UP to the floor and stop —
        // the only upward move this lane makes. (The guardrail would clamp too, but voting it keeps the
        // weighted blend honest rather than letting it average in a below-floor number.)
        if (ctx.CurrentPrice <= floor)
            return new AlgorithmVote(floor, TunnelConfidence, "CROSS_DOCK_FLOOR",
                $"Supplier-only SKU priced at/below the margin floor — lift to the floor ({floor:0.##}).");

        return ctx.Qty90 == 0 ? Tunnel(ctx, floor) : Hold(ctx, floor);
    }

    // Branch B — no sales in 90 days: progressive markdown from the current price toward the margin floor.
    // The discount schedule is anchor-relative and a pure function of the zero-sale streak (so it never
    // compounds run-over-run); Math.Min(CurrentPrice, …) holds the price at today's level until the
    // schedule overtakes any existing discount, after which it only deepens. Math.Max(floor, …) caps it.
    private static AlgorithmVote Tunnel(SkuContext ctx, decimal floor)
    {
        var o = ctx.Options;
        var steps = ctx.ZeroSaleStreakDays / Math.Max(1, o.CrossDockStepIntervalDays);
        var disc = Math.Min(0.99m, o.CrossDockStartDiscountPct / 100m + o.CrossDockStepPct / 100m * steps);

        var price = Math.Max(floor, Math.Min(ctx.CurrentPrice, ctx.PriceAtDiscount(disc)));

        return new AlgorithmVote(price, TunnelConfidence, "CROSS_DOCK_TUNNEL",
            $"Supplier-only, no sales in 90 days ({ctx.ZeroSaleStreakDays} snapshot days) — soft progressive markdown toward the margin floor (now {price:0.##}, floor {floor:0.##}).");
    }

    // Branch A — selling (Qty90 > 0): hold the working price; the current discount is what's moving units.
    // Deepen one step only when demand is decaying, and never raise. Holding is an ACTIVE vote (a concrete
    // number in the blend, not an abstention) so the working discount that started the sales isn't averaged away.
    private static AlgorithmVote Hold(SkuContext ctx, decimal floor)
    {
        var decaying = ctx.Velocity90 > 0 && ctx.Velocity7 <= DecayRatio * ctx.Velocity90;
        // Thin margin → hold: only deepen when there's comfortable room above the floor, so a defensive
        // nudge can't erode an already-slim margin (the floor would catch it, but we hold first).
        var hasMarginRoom = ctx.CurrentMarginPct is decimal m && m >= ctx.Band.MarginFloorPct + HealthyMarginBufferPct;
        if (decaying && hasMarginRoom)
        {
            var disc = ctx.CurrentDiscountFraction + ctx.Options.CrossDockStepPct / 100m;
            var price = Math.Max(floor, Math.Min(ctx.CurrentPrice, ctx.PriceAtDiscount(disc)));
            if (price < ctx.CurrentPrice)
                return new AlgorithmVote(price, HoldConfidence, "CROSS_DOCK_DEFEND",
                    $"Supplier-only, selling but decelerating (v7 {ctx.Velocity7:0.##} ≤ ½·v90 {ctx.Velocity90:0.##}) — deepen toward the floor (now {price:0.##}).");
        }

        return new AlgorithmVote(ctx.CurrentPrice, HoldConfidence, "CROSS_DOCK_HOLD",
            $"Supplier-only and selling — hold the working price ({ctx.CurrentPrice:0.##}); the current discount is what's moving units.");
    }
}
