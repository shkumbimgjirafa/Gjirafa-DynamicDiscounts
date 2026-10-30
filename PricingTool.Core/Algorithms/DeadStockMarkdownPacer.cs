using PricingTool.Core.Domain;

namespace PricingTool.Core.Algorithms;

/// <summary>Which pacing move this run made — carried on the vote's reason text and asserted in tests.</summary>
public enum DeadStockPacedPhase
{
    /// <summary>First run in the tunnel: the opening drop, one step below the live discount (or the band's start discount).</summary>
    Engaged,
    /// <summary>A full period of zero sales passed at the applied level — advanced one step deeper.</summary>
    Stepped,
    /// <summary>The proposed target is not reflected in the live shop price yet — re-opening at the
    /// starting point (start discount, or one step below live when deeper) until it lands.</summary>
    Pending,
    /// <summary>The live price sits at the target — waiting out the band period before the next step.</summary>
    Waiting,
}

/// <summary>
/// The dead-stock markdown pacing state machine: walk the discount down FROM THE LIVE CURRENT
/// DISCOUNT, one band step per band period — never a catch-up jump computed off the absolute
/// zero-sale streak (a SKU that enters the tunnel at -20% continues -25%, -30%, …, not straight to
/// wherever "start + step × streak/period" happens to sit).
///
/// The period is measured in observed zero-sale snapshot days AT THE APPLIED LEVEL: the clock starts
/// only once the live shop price actually reflects the target (<see cref="SkuContext.DeadStockPacedCaughtUpStreak"/>),
/// so the pace is one step per period regardless of how quickly proposals get applied — apply daily
/// and it still steps every period; apply late and the same target just stays proposed until it lands.
///
/// Stateless per run but stateful across runs: the orchestrator persists (target, clock) on
/// SkuDeadStockState and feeds them back through the context. Two invariants hold on every output:
///  - NO-JUMP / START-HOLD: once the target is applied (the live price reflects it), it deepens at
///    most one step per period and sits at most one step below the live discount. Until it is applied,
///    EVERY run re-opens at the starting point — the band's start discount, or one step below the live
///    price when that is deeper — disregarding a target a prior un-applied run persisted. So the start
///    discount does not decay toward one-step-below-live before it reaches the shop, and a long streak
///    still never produces a catch-up jump.
///  - NEVER-SHRINK: the target never proposes a smaller discount than the live one.
/// Call only for SKUs that satisfy <see cref="SkuContext.IsDeadStock"/> — the caller owns the gate.
/// </summary>
public static class DeadStockMarkdownPacer
{
    public readonly record struct PacedMarkdown(decimal TargetDiscount, int? CaughtUpStreak, DeadStockPacedPhase Phase);

    public static PacedMarkdown Next(SkuContext ctx)
    {
        // Step and period are per age stage: as stock ages into a deeper stage its markdown can pace
        // faster (or slower) than while it was younger. The opening minimum (start) stays band-level.
        var stage = ctx.DeadStockStage;
        var step = ctx.Band.StepDiscountPctForStage(stage) / 100m;
        var start = ctx.Band.DeadStockStartDiscountPct / 100m;
        var period = Math.Max(1, ctx.Band.PeriodDaysForStage(stage));
        // Applied-detection tolerance: charm rounding may land the shop price slightly off the exact
        // target, which must still read as "applied" or the period clock would never start.
        var tol = step / 2m;
        var c = ctx.CurrentDiscountFraction;
        var s = ctx.ZeroSaleStreakDays;
        var caught = ctx.DeadStockPacedCaughtUpStreak;

        // The starting point every un-applied run re-opens at: the band's start discount, or one step
        // below the live price when that is already deeper. Deliberately NOT read from the persisted
        // target — a markdown a prior run proposed but that never reached the shop is DISREGARDED, so
        // the start discount holds run after run until it actually goes live (and, once the walk is
        // deep, this still parks one step below a floor-blocked live price).
        var opening = Math.Max(start, c + step);

        decimal target;
        DeadStockPacedPhase phase;
        if (ctx.DeadStockPacedTarget is not decimal prev)
        {
            // Engagement: the SKU just qualified as dead (a full no-sale window without a sale) — open
            // at the starting point.
            target = opening;
            phase = DeadStockPacedPhase.Engaged;
        }
        else if (c >= prev - tol)
        {
            // APPLIED: the live shop price reflects the persisted target (within charm-rounding
            // tolerance). Pace from here — step one band step deeper once a full period of zero sales
            // has passed at this level, otherwise hold and let the period clock run. The clock-start day
            // itself COUNTS: it is already an observed zero-sale day at the applied level (and, since the
            // snapshot only sees a push the day after it lands, the price has typically been live even
            // longer). Counting it inclusively (s - clockStart + 1) is what lets a 15-day period step on
            // a 15-day slow-lane tick — the exclusive interval was one short forever (0 steps on the
            // 2026-08-01 prod tick with 2,964 SKUs at 15 applied days against period 15). On a frozen run
            // (a daily run between 15-day slow-lane ticks) the step is suppressed, so an applied SKU
            // falls through to Waiting and holds its target — the price stays put until the next tick.
            if (!ctx.SlowLanesFrozen && caught is int clockStart && s - clockStart + 1 >= period)
            {
                // Based off the deeper of target/live so a human's deeper manual markdown becomes the
                // new base rather than being stepped back over.
                target = Math.Max(prev, c) + step;
                phase = DeadStockPacedPhase.Stepped;
            }
            else
            {
                target = prev;
                phase = DeadStockPacedPhase.Waiting;
            }
            // NO-JUMP: a deepened target may sit at most one step below the live discount.
            target = Math.Min(target, c + step);
        }
        else
        {
            // NOT yet reflected in the live shop price: re-open at the starting point, disregarding the
            // (possibly eroded) target the prior un-applied run persisted. This is what keeps the
            // band's start discount from decaying toward one-step-below-live while nothing is applied —
            // and it also re-anchors after a human RAISED the price back above the target mid-tunnel.
            target = opening;
            phase = DeadStockPacedPhase.Pending;
        }

        // NEVER-SHRINK the live discount; then the hard cap.
        target = Math.Max(target, c);
        target = Math.Min(target, 0.99m);

        // The period clock runs only while the live price sits at/beyond the target; it (re)starts at
        // today's streak when the price first gets there and resets whenever the target moves ahead.
        int? nextCaught = c >= target - tol ? (caught ?? s) : null;

        return new PacedMarkdown(target, nextCaught, phase);
    }
}
