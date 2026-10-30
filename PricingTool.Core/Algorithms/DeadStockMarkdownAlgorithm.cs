using PricingTool.Core.Domain;

namespace PricingTool.Core.Algorithms;

/// <summary>
/// Algorithm 7 — Dead-stock progressive markdown, PACED FROM THE LIVE DISCOUNT.
/// Zero sales across the entire no-sale window (the dead-stock DEFINITION — default 90 days,
/// configurable per band via <see cref="PriceBandConfig.DeadStockNoSaleWindowDays"/>)
/// with positive locally-held stock: open one band step below the current discount (or at the band's
/// start discount if deeper), then deepen one step per band period of continued zero sales at the
/// applied level — see <see cref="DeadStockMarkdownPacer"/> for the exact state machine. The walk has
/// no discount ceiling; the guardrail clamp sets the real limit, at a floor that deepens with the
/// SKU's age tier (<see cref="SkuContext.DeadStockStage"/> / <c>GuardrailService.DeadStockFloor</c>).
///
/// Deliberately NOT the old absolute curve (start + step × streak/period): that jumped a SKU entering
/// the tunnel with a long-observed streak straight to a deep catch-up discount. Pacing continues from
/// wherever the price actually sits, so the shop never sees a leap.
///
/// Only locally-held (KS) stock counts: dead stock sitting only in supplier warehouses is not
/// ours to give margin away on, so this algorithm abstains when KsStock is zero — we don't
/// discount supplier stock that doesn't sell.
///
/// It also abstains on freshly-stocked SKUs: a pre-order/restock that only just arrived has zero
/// recent sales because it hasn't had a chance to sell, not because it's dead. The oldest on-hand unit
/// must be at least <see cref="PriceBandConfig.DeadStockMinStockAgeDays"/> old (per band, default 90 —
/// ~90% of stock sales happen within 90 days of check-in; see <see cref="SkuContext.IsFreshlyStocked"/>).
/// </summary>
public class DeadStockMarkdownAlgorithm : IPricingAlgorithm
{
    public string Code => AlgorithmCodes.DeadStock;
    public string DisplayName => "Dead-stock progressive markdown";

    public AlgorithmVote? Evaluate(SkuContext ctx)
    {
        // The shared dead-stock definition (SkuContext.IsDeadStock): zero sales across the band's
        // no-sale window, locally-held stock only (supplier-only dead stock is left alone), not
        // freshly stocked, not platform-new.
        if (!ctx.IsDeadStock) return null;

        // Slow-lane freeze (a daily run between 15-day ticks): a SKU that has not yet opened a paced walk
        // stays exactly where it is — don't engage a fresh markdown until the next tick. A SKU that already
        // carries a target falls through and re-emits it; the pacer's step transition is gated off too
        // (SlowLanesFrozen), so an applied markdown holds at a single price for the whole window.
        if (ctx.SlowLanesFrozen && ctx.DeadStockPacedTarget is null) return null;

        var paced = DeadStockMarkdownPacer.Next(ctx);

        // ZeroSaleStreakDays is measured in CALENDAR days (SnapshotService advances it by the days elapsed
        // between snapshots), so the pacing period means the same "N days" at any run cadence — at the
        // 15-day cadence a single run advances the streak ~15 days, so a still-dead SKU steps once per run.
        // Step/period are the SKU's current age stage's (see DeadStockMarkdownPacer).
        var stage = ctx.DeadStockStage;
        var period = Math.Max(1, ctx.Band.PeriodDaysForStage(stage));
        var stepPp = ctx.Band.StepDiscountPctForStage(stage);
        var phaseText = ctx.SlowLanesFrozen
            // Frozen between 15-day ticks: re-emit the held target without promising a next step (the step
            // is gated off until the tick).
            ? $"holding {paced.TargetDiscount:P0} — frozen until the next 15-day slow-lane tick"
            : paced.Phase switch
            {
                DeadStockPacedPhase.Engaged => $"opening paced drop from {ctx.CurrentDiscountFraction:P0} to {paced.TargetDiscount:P0}",
                DeadStockPacedPhase.Stepped => $"stepped {stepPp:0.#}pp deeper to {paced.TargetDiscount:P0} after {period} zero-sale days at the applied level",
                // Waiting always carries the clock start (the phase is assigned only when it is set).
                // Mirrors the pacer's inclusive count: the clock-start day is applied day 1, so the
                // remaining days are period − (streak − start + 1).
                DeadStockPacedPhase.Waiting => $"holding {paced.TargetDiscount:P0} — next step after {Math.Max(0, period - (ctx.ZeroSaleStreakDays - paced.CaughtUpStreak!.Value + 1))} more zero-sale days",
                _ => $"re-proposing {paced.TargetDiscount:P0} until the shop price reflects it",
            };

        return new AlgorithmVote(
            ctx.PriceAtDiscount(paced.TargetDiscount),
            0.8m,
            "DEAD_STOCK_MARKDOWN",
            $"No sales in {ctx.DeadStockWindowDays} days, {ctx.KsStock} locally-held units stuck ({ctx.ZeroSaleStreakDays} snapshot days without movement) — {phaseText}.");
    }
}
