using PricingTool.Core.Domain;

namespace PricingTool.Core.Algorithms;

/// <summary>
/// Sell-through — the consolidated velocity/inventory advisor. It merges the former
/// VELOCITY_FORECAST, STOCKOUT_RISK and MOMENTUM algorithms into one voice, so the same
/// sales-speed signal is not counted three times in the weighted blend.
///
/// Stock is measured as our LOCALLY-HELD (KS) stock only — supplier stock isn't ours to clear and
/// is volatile, so it must never drive a markdown. It reads projected days-to-sellout of local stock
/// (level) plus a statistically-tested change in the recent sales rate (trend):
///  - imminent sellout on a healthy margin → remove the discount (don't burn margin on stock that
///    will sell out anyway). Never a markdown — capped up to today's price.
///  - fast → shave the discount · on pace → hold · slow / overstocked → deepen progressively.
///  - a trend modifier then nudges the target shallower when demand is accelerating and deeper when it
///    is decelerating — but only when the last-7d-vs-baseline change has the statistical power and
///    significance to be real (not Poisson noise) and isn't merely an artifact of a price change.
/// Silent when we hold no local stock (supplier-only is the guardrail's lane) or there is no velocity
/// at all (zero-velocity local stock is DEAD_STOCK's lane).
/// </summary>
public class SellThroughAlgorithm : IPricingAlgorithm
{
    private const decimal HealthyMarginBufferPct = 5m; // "comfortably above the floor" for the remove branch

    // --- Trend-reliability gates ---------------------------------------------------------------------
    // The trend modifier compares the last-7d sales rate to the baseline rate from the prior 83 days.
    // A ratio of small counts is dominated by Poisson noise (0 sold last week when the baseline predicts
    // ~0.4 is an empty week, not "decelerating"), so two statistical guards must pass before the ±3pp
    // nudge fires — plus the price-validity gate below.
    private const int TrendBaselineDays = 83;          // days 8–90: the disjoint baseline for the last-7d window
    // 1. POWER: the baseline must imply at least this many EXPECTED events in the recent 7d window. Below
    //    it there is no power to detect a change — and it is exactly the classic "expected count ≥ 5" rule
    //    that makes the normal approximation to the Poisson valid. Needs ≈60 baseline units/83d to clear.
    private const decimal TrendMinExpectedCount = 5m;
    // 2. SIGNIFICANCE: the recent count must deviate from that expectation μ by at least this many standard
    //    deviations, z = (Qty7 − μ)/√μ. 1.2816 ≈ 90% one-sided — the same z the elasticity gate uses.
    private const double TrendSignificanceZ = 1.2816;

    // 3. Price-validity gate for the trend read. A price change between the windows mechanically moves the
    // rate with no change in underlying demand: under constant-elasticity demand the spurious factor is
    // (P̄7/P̄90)^E. Bounding |E| ≤ 3 and tolerating at most ~10% mechanical lift, the average sold-at price
    // may differ by at most ~3% between windows before the trend is untrustworthy. Expressed as the price
    // ratio r = (1 − Disc7)/(1 − Disc90): r below this floor ⇒ last week sold cheaper (its faster sales are
    // partly just the discount, don't shave); r above 1/floor ⇒ last week sold dearer (its slower sales are
    // partly just the price rise, don't deepen). Tighten by raising it (stricter), loosen by lowering it.
    private const decimal TrendPriceRatioFloor = 0.97m;

    public string Code => AlgorithmCodes.SellThrough;
    public string DisplayName => "Sell-through (velocity + inventory)";

    public AlgorithmVote? Evaluate(SkuContext ctx)
    {
        // Locally-held dead stock is the paced dead-stock lane's EXCLUSIVELY (mirrors the elastic lane).
        // At the default 90-day no-sale window a dead SKU already has zero velocity, so the days-to-sellout
        // guard below silences this anyway; but at a shorter band window a SKU can be "dead" yet still show
        // trailing velocity, and a co-vote here would dilute the paced markdown — abstain up front.
        if (ctx.IsDeadStock) return null;
        if (ctx.KsStock <= 0) return null;                              // no local stock to clear — supplier-only is the guardrail's lane
        if (ctx.DaysToSelloutLocal is not decimal days) return null;   // zero velocity → dead-stock territory

        var horizon = ctx.Options.StockoutRiskDays;

        // Imminent sellout of local stock + healthy margin: discounting it only burns margin — remove
        // the discount. Math.Max keeps it from ever becoming a markdown (same guard the old Stockout algo used).
        if (days <= horizon &&
            ctx.CurrentMarginPct is decimal margin &&
            margin >= ctx.Band.MarginFloorPct + HealthyMarginBufferPct)
        {
            var removeConfidence = Math.Clamp(0.6m + 0.3m * (1m - days / horizon), 0m, 0.9m);
            return new AlgorithmVote(
                Math.Max(ctx.AnchorPrice, ctx.CurrentPrice),
                removeConfidence,
                "SELL_THROUGH_REMOVE",
                $"Local stock sells out in ≈{Math.Round(days)} days (≤{horizon}d) at {margin:0.#}% margin — remove the discount.");
        }

        var current = ctx.CurrentDiscountFraction;
        (decimal target, string shape) = days switch
        {
            <= 21  => (Math.Max(0m, current - 0.05m), "fast sell-through — shave the discount"),
            <= 45  => (current,                       "on pace — hold the discount"),
            <= 90  => (current + 0.03m,               "slow (≤90d of local stock) — slightly deeper discount"),
            <= 180 => (current + 0.06m,               "very slow (≤180d of local stock) — deeper discount"),
            _      => (current + 0.10m,               "over 180d of local stock — markdown pressure"),
        };

        // Trend modifier: nudge shallower on accelerating demand, deeper on decelerating — but only when
        // the recent-vs-baseline comparison is statistically trustworthy. Baseline = the disjoint prior
        // 83 days (days 8–90); μ = expected last-7d count if that baseline rate held.
        var trendNote = "";
        var baselineQty = ctx.Qty90 - ctx.Qty7;                              // units sold in days 8–90 (nested windows ⇒ ≥ 0)
        var expectedRecent = baselineQty * 7m / TrendBaselineDays;           // μ
        if (expectedRecent >= TrendMinExpectedCount)                         // (1) enough power to read a trend
        {
            var mu = (double)expectedRecent;
            var z = (ctx.Qty7 - mu) / Math.Sqrt(mu);                         // Poisson normal-approx statistic

            // (3) Sold-at price ratio between the windows, or null when we can't compare — DiscN is null
            // exactly when that window had no sales (null ≠ 0). With no sales last week (Qty7 == 0 → Disc7
            // null) a significant drop is genuine demand collapse, not a price artifact, so the gate allows it.
            decimal? priceRatio = ctx.Disc7 is decimal d7 && ctx.Disc90 is decimal d90 && d90 < 1m
                ? (1m - d7) / (1m - d90)
                : null;

            if (z >= TrendSignificanceZ)          // (2) recent rate significantly ABOVE baseline → accelerating
            {
                // Block only when last week sold materially cheaper (price-driven lift, not real demand).
                if (priceRatio is decimal ra && ra < TrendPriceRatioFloor)
                    trendNote = $" · accel not trusted (sold {(1m - ra) * 100:0.#}% cheaper recently)";
                else { target = Math.Max(0m, target - 0.03m); trendNote = $" · accelerating ({ctx.Qty7} vs ≈{expectedRecent:0.#} expected in 7d)"; }
            }
            else if (z <= -TrendSignificanceZ)    // (2) recent rate significantly BELOW baseline → decelerating
            {
                // Block only when last week sold materially dearer (e.g. a promo ended), not real demand.
                if (priceRatio is decimal rd && rd > 1m / TrendPriceRatioFloor)
                    trendNote = $" · decel not trusted (sold {(rd - 1m) * 100:0.#}% dearer recently)";
                else { target += 0.03m; trendNote = $" · decelerating ({ctx.Qty7} vs ≈{expectedRecent:0.#} expected in 7d)"; }
            }
        }

        var confidence = Math.Min(0.9m, 0.3m + ctx.Qty30 / 50m);
        var reason = target < current ? "SELL_THROUGH_FAST"
                   : target > current ? "SELL_THROUGH_SLOW"
                   : "SELL_THROUGH_HOLD";

        return new AlgorithmVote(
            ctx.PriceAtDiscount(target),
            confidence,
            reason,
            $"≈{Math.Round(days)} days to clear {ctx.KsStock} local units at {ctx.WeightedDailyVelocity:0.##}/day — {shape}{trendNote}.");
    }
}
