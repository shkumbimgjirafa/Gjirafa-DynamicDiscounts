using PricingTool.Core.Domain;

namespace PricingTool.Core.Services;

/// <summary>
/// Psychological rounding — the final presentation layer. Candidates are generated below and
/// above the input price; a candidate is only accepted if it stays inside the guardrail bounds,
/// so rounding can never undo a clamp.
/// </summary>
public class RoundingService
{
    public record RoundingOutcome(decimal Price, bool RoundingApplied, bool SkippedOutOfBounds);

    /// <summary>
    /// Default Weber fraction for <see cref="RoundingConvention.Gj50Charm"/>: the charm grid step
    /// stays within ~2% of the price, and a round-up is only taken if it moves the price ≤2%.
    /// </summary>
    public const decimal DefaultRelativePrecision = 0.02m;

    /// <summary>
    /// <see cref="RoundingConvention.Gj50Charm"/> fixed-ending ranges (EUR). €30–99 ends in x5.50 / x9.50
    /// (35.50, 39.50, 45.50 … 99.50); €100–999 ends in xx9.50 (109.50, 119.50 … 999.50). Under €30 and
    /// from €1000 the Weber-scaled .50 grid applies unchanged.
    /// </summary>
    private const decimal Gj50TensFrom = 30m, Gj50HundredsFrom = 100m, Gj50WeberFrom = 1000m;

    public RoundingOutcome Apply(decimal price, RoundingConvention convention, PriceBounds bounds,
        decimal lowPriceThreshold = 5m, decimal relativePrecision = DefaultRelativePrecision)
    {
        if (convention == RoundingConvention.None)
            return new RoundingOutcome(Normalize(price, bounds), false, false);

        // A pinned / degenerate band (e.g. a dead-stock tunnel price held at exactly CurrentPrice,
        // so lower == upper) admits no rounding candidate. Hold the price without flagging it as a
        // rounding anomaly — the hold is deliberate, not a skipped-out-of-bounds event.
        if (bounds.Upper <= bounds.Lower)
            return new RoundingOutcome(Normalize(price, bounds), false, false);

        // GjirafaMall charm: round up to the next …99 if the Weber budget allows it, else nearest …49/…99.
        if (convention == RoundingConvention.GjmCharm)
            return NoRoundTenEuro(ApplyGjmCharm(price, bounds, lowPriceThreshold, relativePrecision),
                RoundingConvention.GjmCharm, bounds);

        var down = RoundDown(price, convention, lowPriceThreshold, relativePrecision);
        var up = RoundUp(price, convention, lowPriceThreshold, relativePrecision);

        var downOk = down >= bounds.Lower && down <= bounds.Upper && down > 0;
        var upOk = up >= bounds.Lower && up <= bounds.Upper;

        // Gjirafa50 charm: nearest x5.50/x9.50 for €30–99, otherwise a round-up-biased selection (claw back margin).
        if (convention == RoundingConvention.Gj50Charm)
            return NoRoundTenEuro(ApplyGj50Charm(price, down, up, downOk, upOk, bounds, relativePrecision),
                RoundingConvention.Gj50Charm, bounds);

        // Gjirafa50 charm, whole-currency (MKD/ALL): the same up-biased engine scaled ×100 — charm gap 50
        // (→ …50 endings) on the whole-currency step set; the round-ten ban scales to round thousands.
        if (convention == RoundingConvention.Gj50CharmHundreds)
            return NoRoundTenEuro(
                SelectCharmUpBiased(price, down, up, downOk, upOk, bounds, relativePrecision, charmGap: 50m, steps: CharmStepsHundreds),
                RoundingConvention.Gj50CharmHundreds, bounds, baseUnit: 100m, gap: 50m);

        // GjirafaMall charm, whole-currency (MKD/ALL): the same up-biased engine with a tighter charm gap
        // of 10 (→ …90 endings, "just below a round number", the margin claw), plus a …50 sub-budget
        // fallback so cheap items don't jump far. Endings never coincide with a round number → no ban.
        if (convention == RoundingConvention.GjmCharmHundreds)
            return SelectCharmUpBiased(price, down, up, downOk, upOk, bounds, relativePrecision,
                charmGap: 10m, steps: CharmStepsHundreds, fallbackHalfOffset: 50m);

        return PickNearest(price, down, up, downOk, upOk, bounds);
    }

    /// <summary>
    /// The in-bounds candidate closer to the scored price (tie → the lower price); if only one fits the
    /// bounds, that one; if neither does, hold the price normalized and flag the skip.
    /// </summary>
    private static RoundingOutcome PickNearest(decimal price, decimal down, decimal up, bool downOk, bool upOk, PriceBounds bounds)
    {
        if (downOk && upOk) return new RoundingOutcome(price - down <= up - price ? down : up, true, false);
        if (downOk) return new RoundingOutcome(down, true, false);
        if (upOk) return new RoundingOutcome(up, true, false);
        return new RoundingOutcome(Normalize(price, bounds), false, true);
    }

    /// <summary>
    /// Gjirafa50 charm, by price range (<paramref name="down"/> / <paramref name="up"/> already sit on the
    /// range's grid — see <see cref="Gj50CharmGridDown"/>):
    /// <list type="bullet">
    /// <item><b>€30–99</b>: the <b>nearest</b> x5.50 / x9.50 point (32.60 → 35.50, 47.31 → 45.50, 97.60 → 99.50;
    /// tie → lower).</item>
    /// <item><b>€100–999</b>: an xx9.50 point (109.50 … 999.50), chosen by the round-up-biased selection
    /// (<see cref="SelectCharmUpBiased"/>: claw up within the Weber tolerance, never just above a salient
    /// round number, else nearest).</item>
    /// <item><b>Under €30 and €1000+</b>: the Weber-scaled .50 grid with the same up-biased selection.</item>
    /// </list>
    /// When no fixed-ending point fits the guardrail bounds (a [floor, cap] band narrower than the €4–10 gap
    /// between points), fall back to the Weber .50 grid so the price still ends in .50 inside the bounds.
    /// </summary>
    private static RoundingOutcome ApplyGj50Charm(decimal price, decimal down, decimal up, bool downOk, bool upOk,
        PriceBounds bounds, decimal relativePrecision)
    {
        var fixedEnding = price >= Gj50TensFrom && price < Gj50WeberFrom;
        var outcome = price >= Gj50TensFrom && price < Gj50HundredsFrom
            ? PickNearest(price, down, up, downOk, upOk, bounds)
            : SelectCharmUpBiased(price, down, up, downOk, upOk, bounds, relativePrecision, charmGap: 0.5m, steps: CharmSteps);
        if (!fixedEnding || !outcome.SkippedOutOfBounds) return outcome;

        var weberDown = Gj50CharmSnap(price, relativePrecision, up: false, CharmSteps, 0.5m);
        var weberUp = Gj50CharmSnap(price, relativePrecision, up: true, CharmSteps, 0.5m);
        return SelectCharmUpBiased(price, weberDown, weberUp,
            weberDown >= bounds.Lower && weberDown <= bounds.Upper && weberDown > 0,
            weberUp >= bounds.Lower && weberUp <= bounds.Upper,
            bounds, relativePrecision, charmGap: 0.5m, steps: CharmSteps);
    }

    /// <summary>
    /// Final safety net for the platform charm conventions: a presented price must NEVER show a
    /// <b>round-ten euro</b> (30.xx, 50.xx, 100.xx, 1000.xx…) — a firm merchandising rule. Whenever the
    /// charm rounder lands on one (because the Weber budget or the margin floor blocked the pull across
    /// the ten), cross <b>down</b> to the charm point just below the ten (…9.99 for GjmCharm, …9.50 for
    /// Gj50Charm) if that stays at/above the floor; if the floor blocks it, cross <b>up</b> past the whole
    /// ten to the next euro's charm point (e.g. 30.49 → 31.49, 30.50 → 31.50) — so the ending is never
    /// x0.49 / x0.99 / x0.50. For Gj50Charm in its €30–999 fixed-ending ranges the rise goes to that range's
    /// next point past the ten (30.50 → 35.50, 110.50 → 119.50), falling back to the next euro's .50 only
    /// when the cap blocks it. The drop below the ten is always preferred (never priced below the floor);
    /// only a floor that lands inside the ten forces the rise. A degenerate band whose floor AND anchor
    /// both sit inside the same ten keeps the price unchanged (no clean charm point fits the bounds).
    /// Sub-€10 prices (single-digit euros, the low-price grids) are exempt — they have no round-ten euro.
    /// </summary>
    private static RoundingOutcome NoRoundTenEuro(RoundingOutcome outcome, RoundingConvention convention, PriceBounds bounds,
        decimal baseUnit = 1m, decimal gap = 0.5m)
    {
        // "Round-ten" is a multiple of ten base units: 10/20/30… euros for the EUR conventions
        // (baseUnit 1), or 1 000/2 000/… for the whole-currency ones (baseUnit 100). Count the base
        // units in the integer part and test that count for a round ten.
        var units = Math.Floor(outcome.Price / baseUnit);
        if (units < 10m || units % 10m != 0m) return outcome;   // sub-ten and non-round-ten values are fine
        var roundTen = units * baseUnit;                        // 30, or 3 000 / 10 000 …

        // The charm point just below the ten, then the first one past the whole ten (skipping the banned
        // x0.49/x0.99/x0.50 / …050 endings). Gj50Charm's €30–999 ranges rise to their own next point
        // (35.50 / 119.50) first, and only then to the next euro's .50.
        var (below, above, aboveFallback) = convention switch
        {
            RoundingConvention.GjmCharm          => (roundTen - 0.01m, roundTen + 1.49m, roundTen + 1.49m),   // 29.99 → else 31.49
            RoundingConvention.Gj50Charm         => (roundTen - 0.50m,                                          // 29.50 → else 35.50 → else 31.50
                roundTen >= Gj50TensFrom && roundTen < Gj50WeberFrom ? Gj50CharmGridUp(roundTen + 0.50m, DefaultRelativePrecision) : roundTen + 1.50m,
                roundTen + 1.50m),
            RoundingConvention.Gj50CharmHundreds => (roundTen - gap,   roundTen + baseUnit + gap, roundTen + baseUnit + gap), // 2 950 → else 3 150
            _ => (outcome.Price, outcome.Price, outcome.Price),                                                // not a charm convention — leave it
        };

        foreach (var candidate in new[] { below, above, aboveFallback })
            if (candidate >= bounds.Lower && candidate <= bounds.Upper)
                return outcome with { Price = candidate, RoundingApplied = true, SkippedOutOfBounds = false };
        return outcome;   // no charm point below or above the ten fits [floor, cap] — unavoidable
    }

    /// <summary>
    /// GjirafaMall charm — Power BI's …49 / …99 endings, but selected the platform way: <b>round up to the
    /// next …99 when the Weber budget lets the price jump there</b> (clawing margin), otherwise snap to the
    /// <b>nearest …49 / …99</b> half-euro point. So a price reaches the …99 if it can afford it (45.40 → 45.99,
    /// since 0.59 ≤ k·price), drops to the …49 when it can't (10.40 → 10.49), and never overshoots a round
    /// €100/€1000 (100 → 99.99, not 100.99). Below <paramref name="lowPriceThreshold"/> (€5) the same up-bias
    /// runs on a finer 10-cent <b>.x9</b> grid (…0.09, 0.19, …, 4.99): round up to the next .x9 when the
    /// Weber budget reaches it (4.37 → 4.39), else the nearest .x9 (1.21 → 1.19, where a 10-cent jump exceeds
    /// 2% of the price). Everything is clamped to the guardrail bounds (floor / OldPrice cap).
    /// </summary>
    private static RoundingOutcome ApplyGjmCharm(decimal price, PriceBounds bounds, decimal lowPriceThreshold,
        decimal relativePrecision)
    {
        // Below the threshold the charm grid tightens from the half-euro …49/…99 to a 10-cent .x9 grid
        // (…0.09, 0.19, …, 4.99). Mirror the charm regime's up-bias on this finer grid: round UP to the
        // next .x9 when the Weber budget (k·price) reaches it (4.37 → 4.39), clawing margin; otherwise snap
        // to the NEAREST .x9 (1.21 → 1.19, where a 10-cent jump exceeds 2% of the price). The up-jump is
        // held at/under the threshold (≤ €5) so it can't leapfrog the boundary into the half-euro regime —
        // that keeps the whole map monotonic across €5.
        if (price < lowPriceThreshold)
        {
            var lowUp = SnapUp(price, 0.10m, 0.09m);
            if (lowUp - price <= price * relativePrecision && lowUp <= lowPriceThreshold
                && lowUp >= bounds.Lower && lowUp <= bounds.Upper)
                return new RoundingOutcome(Normalize(lowUp, bounds), true, false);
            return NearestOnGrid(price, 0.10m, 0.09m, bounds);   // nearest …x9
        }

        var budget = price * relativePrecision;
        var euro = Math.Floor(price);

        // Round-ten euro (50.xx, 120.xx, 100.xx, 1000.xx): pull DOWN across the ten to the …9.99 just below
        // it (50.30 → 49.99, 120.88 → 119.99) — never round up to a …0.99. Applied when the pull is within
        // the Weber budget and the floor; otherwise fall through to the nearest …49/…99 (cheap-euro case).
        if (euro % 10m == 0m)
        {
            var below = euro - 0.01m;   // 49.99, 119.99, 99.99, 999.99
            if (price - below <= budget && below >= bounds.Lower && below <= bounds.Upper)
                return new RoundingOutcome(Normalize(below, bounds), true, false);
            return NearestOnGrid(price, 0.50m, 0.49m, bounds);
        }

        // Otherwise round UP to this euro's …99 if the Weber budget reaches it (45.40 → 45.99); else the
        // nearest …49/…99 (cheap items that can't afford the …99 jump land on the closer …49).
        var up99 = euro + 0.99m;
        if (up99 - price <= budget && up99 >= bounds.Lower && up99 <= bounds.Upper)
            return new RoundingOutcome(Normalize(up99, bounds), true, false);

        return NearestOnGrid(price, 0.50m, 0.49m, bounds);
    }

    /// <summary>Nearest lattice point of <c>n·step + offset</c> to <paramref name="price"/>, in bounds (tie → lower).</summary>
    private static RoundingOutcome NearestOnGrid(decimal price, decimal step, decimal offset, PriceBounds bounds)
    {
        var down = SnapDown(price, step, offset);
        var up = SnapUp(price, step, offset);
        var downOk = down >= bounds.Lower && down <= bounds.Upper && down > 0;
        var upOk = up >= bounds.Lower && up <= bounds.Upper;
        return PickNearest(price, down, up, downOk, upOk, bounds);
    }

    /// <summary>
    /// Round-up-biased selection for <see cref="RoundingConvention.Gj50Charm"/>. The selection honours
    /// the three guards on the up candidate (anchor cap via <paramref name="upOk"/>; the Weber tolerance;
    /// and — generalizing "no 1xx → 2xx jump" — never landing just <i>above</i> a round number):
    ///
    /// <para>Each charm point sits exactly 0.50 below a round number (candidate + 0.50). When both
    /// candidates are affordable (within bounds and within the Weber tolerance), prefer the HIGHER one
    /// to claw back margin — UNLESS the lower candidate sits just below a <i>rounder</i> number (more
    /// trailing zeros). So 100.00 → 99.50 (just under 100, not 100.50 just over it), 199.80 → 199.50,
    /// 1000 → 999.50, 5000 → 4999.50; but 1233.23 → 1239.50 and 3450 → 3499.50 still round up because
    /// the up side is no less round than the down side.</para>
    ///
    /// If only one candidate is affordable, take it; if neither is, leave the price un-charmed
    /// (normalized). Rounding up can never breach the margin floor (the lower bound), so the floor is safe.
    ///
    /// <para><paramref name="charmGap"/> is the distance each charm point sits below its round number
    /// (0.50 for <see cref="RoundingConvention.Gj50Charm"/>, 50 for <see cref="RoundingConvention.Gj50CharmHundreds"/>,
    /// 10 for <see cref="RoundingConvention.GjmCharmHundreds"/>'s …90 grid). It only feeds the salience
    /// checks — the candidates themselves are already on the gap's grid. <paramref name="steps"/> is the
    /// Weber step set (EUR or the whole-currency ×100 set), used only by the just-above pull below.</para>
    ///
    /// <para><paramref name="fallbackHalfOffset"/>, when set (GjmCharmHundreds passes 50), makes the
    /// sub-budget regime snap to the nearest point of a two-offset grid — the primary …90 offset
    /// (<c>steps[^1] − charmGap</c>) plus this …50 midpoint — so a cheap SKU lands closer than a full
    /// step away. Null (the Gj50 conventions) keeps the single-grid nearest.</para>
    /// </summary>
    private static RoundingOutcome SelectCharmUpBiased(decimal price, decimal down, decimal up,
        bool downOk, bool upOk, PriceBounds bounds, decimal relativePrecision, decimal charmGap,
        decimal[] steps, decimal? fallbackHalfOffset = null)
    {
        // If the price sits exactly ON a charm point that is itself just ABOVE a salient round number
        // (e.g. 70.50 just over 70, 100.50 just over 100 — so down == up == price), shift the down
        // candidate to the charm point just BELOW that round number, so the anti-charm rule pulls the
        // price there (→ 69.50, 99.50) when the move stays within the Weber tolerance (it does for ≳ €50).
        if (down == up && SitsJustAboveRoundNumber(price, charmGap))
        {
            down -= Gj50CharmStep(price, relativePrecision, steps);
            downOk = down >= bounds.Lower && down <= bounds.Upper && down > 0;
        }

        var tolerance = price * relativePrecision;
        var upInTolerance = upOk && up - price <= tolerance;
        var downInTolerance = downOk && down > 0 && price - down <= tolerance;

        // In-budget: a charm point is within the Weber tolerance. Round up to claw back margin — UNLESS
        // the down charm point sits just below a salient round number (landing just *above* one is
        // anti-charm). So 100→99.50, 1000→999.50, 5000→4999.50, 199.80→199.50; 1233.23→1239.50 rounds up.
        if (upInTolerance && downInTolerance)
            return new RoundingOutcome(SitsJustBelowRoundNumber(down, price, charmGap) ? down : up, true, false);
        if (upInTolerance) return new RoundingOutcome(up, true, false);
        if (downInTolerance) return new RoundingOutcome(down, true, false);

        // Below budget (cheap items, where one charm step is more than the tolerance): the charm ending
        // still wins. Snap to the NEAREST charm point so the price stays on the grid rather than keeping a
        // non-charm optimum. The move here is at most one step.
        //
        // GjmCharmHundreds passes a second (…50) offset: in this sub-budget regime it snaps to the nearest
        // point of the finer …50 / …90 grid, so a cheap SKU isn't dragged up to a full step away just to
        // end in …90. (The Gj50 conventions pass null and keep the single-grid nearest.)
        if (fallbackHalfOffset is decimal halfOffset)
            return NearestOnTwoOffsetGrid(price, steps[^1], halfOffset, steps[^1] - charmGap, bounds);

        var downEligible = downOk && down > 0;
        if (downEligible && upOk)
            return new RoundingOutcome(price - down <= up - price ? down : up, true, false);
        if (downEligible) return new RoundingOutcome(down, true, false);
        if (upOk) return new RoundingOutcome(up, true, false);

        // No charm point fits the guardrail bounds (e.g. a narrow band) — hold the price, normalized.
        return new RoundingOutcome(Normalize(price, bounds), false, true);
    }

    /// <summary>
    /// Nearest in-bounds lattice point across two offsets on the same step (tie → lower price). Used for
    /// <see cref="RoundingConvention.GjmCharmHundreds"/>'s sub-budget fallback, whose grid carries both the
    /// …50 midpoints and the …90 near-round points (step 100, offsets 50 and 90).
    /// </summary>
    private static RoundingOutcome NearestOnTwoOffsetGrid(decimal price, decimal step, decimal offsetA, decimal offsetB, PriceBounds bounds)
    {
        var candidates = new[]
        {
            SnapDown(price, step, offsetA), SnapUp(price, step, offsetA),
            SnapDown(price, step, offsetB), SnapUp(price, step, offsetB),
        };

        var found = false;
        var best = 0m;
        foreach (var c in candidates)
        {
            if (c <= 0m || c < bounds.Lower || c > bounds.Upper) continue;
            if (!found
                || Math.Abs(c - price) < Math.Abs(best - price)
                || (Math.Abs(c - price) == Math.Abs(best - price) && c < best))
            {
                best = c;
                found = true;
            }
        }

        return found
            ? new RoundingOutcome(Normalize(best, bounds), true, false)
            : new RoundingOutcome(Normalize(price, bounds), false, true);
    }

    /// <summary>
    /// True if the charm point <paramref name="candidate"/> sits just below a "salient" round number,
    /// scaled to the price's magnitude: a multiple of <b>half the leading magnitude</b> — …50 in the
    /// hundreds (50, 100, 150, 200…), …500 in the thousands (500, 1000, 1500…), …5 in the tens. Each
    /// charm point is 0.50 below a whole number; this tests whether that whole number is salient.
    /// Tying salience to magnitude (rather than raw trailing zeros) keeps the selection monotonic across
    /// the grid's step transitions — e.g. it does <i>not</i> treat 1250 as salient, so 1250 rounds up
    /// like its neighbours instead of dropping below them.
    /// </summary>
    private static bool SitsJustBelowRoundNumber(decimal candidate, decimal price, decimal charmGap)
    {
        var modulus = LeadingMagnitude(price) / 2m;        // 5 (tens), 50 (hundreds), 500 (thousands)…
        return (candidate + charmGap) % modulus == 0m;     // candidate + gap is the round number it sits below
    }

    /// <summary>
    /// True if the price sits exactly <paramref name="charmGap"/> <i>above</i> a salient round number —
    /// i.e. it is itself a charm point of the form R + gap (70.50 over 70, 100.50 over 100 for gap 0.50).
    /// Such a price reads as just over the round number, so it should be pulled to the charm point just
    /// below it instead. (For the small GjmCharm gaps this is effectively inert — those points sit just
    /// <i>below</i> round numbers, never above.)
    /// </summary>
    private static bool SitsJustAboveRoundNumber(decimal price, decimal charmGap)
    {
        var modulus = LeadingMagnitude(price) / 2m;
        return (price - charmGap) % modulus == 0m;         // price - gap is a salient round number
    }

    /// <summary>The place value of the leading digit: 10^floor(log10(price)). 47→10, 199→100, 1233→1000.</summary>
    private static decimal LeadingMagnitude(decimal price)
    {
        var magnitude = 1m;
        var p = Math.Abs(price);
        while (magnitude * 10m <= p) magnitude *= 10m;
        return magnitude;
    }

    /// <summary>
    /// True when the price sits on the convention's grid. GjmCharm has no static round-down (it is applied with bounds),
    /// so its grid is checked directly: .x9 under the threshold, …49 / …99 from it.
    /// </summary>
    public static bool IsOnGrid(decimal price, RoundingConvention c, decimal lowPriceThreshold = 5m,
        decimal relativePrecision = DefaultRelativePrecision) => c switch
    {
        RoundingConvention.None => true,
        RoundingConvention.GjmCharm => price < lowPriceThreshold ? SnapDown(price, 0.10m, 0.09m) == price : SnapDown(price, 0.50m, 0.49m) == price,
        _ => RoundDown(price, c, lowPriceThreshold, relativePrecision) == price,
    };

    /// <summary>Largest convention-conforming price ≤ input.</summary>
    public static decimal RoundDown(decimal price, RoundingConvention c, decimal lowPriceThreshold = 5m,
        decimal relativePrecision = DefaultRelativePrecision) => c switch
    {
        // Below the threshold the .99 grid tightens from €1 steps to a 10-cent .x9 grid (…0.99, 1.09,
        // 1.19), so rounding a cheap item can't swing it between 0.99 and 1.99 and distort its margin.
        RoundingConvention.EndsIn99 => price < lowPriceThreshold ? SnapDown(price, 0.10m, 0.09m) : SnapDown(price, 1m, 0.99m),
        RoundingConvention.EndsIn95 => SnapDown(price, 1m, 0.95m),
        RoundingConvention.EndsIn50 => SnapDown(price, 1m, 0.50m),
        RoundingConvention.WholeEuro => Math.Floor(price),
        RoundingConvention.Charm995 => Charm995Down(price),
        RoundingConvention.EndsIn99Hundreds => Hundreds99Down(price),
        RoundingConvention.Gj50Charm => Gj50CharmGridDown(price, relativePrecision),
        // Whole-currency charms (MKD/ALL) on the ×100 step set: Gj50 ends …50 (gap 50), GjmMall's …90 grid (gap 10).
        RoundingConvention.Gj50CharmHundreds => Gj50CharmSnap(price, relativePrecision, up: false, CharmStepsHundreds, 50m),
        RoundingConvention.GjmCharmHundreds => Gj50CharmSnap(price, relativePrecision, up: false, CharmStepsHundreds, 10m),
        _ => price,
    };

    /// <summary>Smallest convention-conforming price ≥ input.</summary>
    public static decimal RoundUp(decimal price, RoundingConvention c, decimal lowPriceThreshold = 5m,
        decimal relativePrecision = DefaultRelativePrecision) => c switch
    {
        RoundingConvention.EndsIn99 => price < lowPriceThreshold ? SnapUp(price, 0.10m, 0.09m) : SnapUp(price, 1m, 0.99m),
        RoundingConvention.EndsIn95 => SnapUp(price, 1m, 0.95m),
        RoundingConvention.EndsIn50 => SnapUp(price, 1m, 0.50m),
        RoundingConvention.WholeEuro => Math.Ceiling(price),
        RoundingConvention.Charm995 => Charm995Up(price),
        RoundingConvention.EndsIn99Hundreds => Hundreds99Up(price),
        RoundingConvention.Gj50Charm => Gj50CharmGridUp(price, relativePrecision),
        RoundingConvention.Gj50CharmHundreds => Gj50CharmSnap(price, relativePrecision, up: true, CharmStepsHundreds, 50m),
        RoundingConvention.GjmCharmHundreds => Gj50CharmSnap(price, relativePrecision, up: true, CharmStepsHundreds, 10m),
        _ => price,
    };

    /// <summary>
    /// "Nice" charm grid steps, descending. The chosen step is the coarsest one no larger than the
    /// Weber budget (<c>relativePrecision · price</c>); each yields a "just below a round number"
    /// ending via offset <c>step − 0.5</c> (1 → …50, 5 → …4.50/9.50, 25 → …24.50/49.50/74.50/99.50,
    /// 100 → …99.50). 2 and 2.5 are omitted on purpose — they'd produce off-charm endings (…1.50, …7.00).
    /// </summary>
    private static readonly decimal[] CharmSteps = { 1000m, 500m, 250m, 100m, 50m, 25m, 10m, 5m, 1m };

    /// <summary>
    /// The <see cref="CharmSteps"/> set scaled ×100 for whole-currency markets (MKD/ALL — no minor unit),
    /// so the finest step is 100 (offset step−gap keeps the …50 / …90 ending: 100 → …50 / …90; 1000 →
    /// …950 / …990; 5000 → …4 950 / …4 990). Used by <see cref="RoundingConvention.Gj50CharmHundreds"/>
    /// and <see cref="RoundingConvention.GjmCharmHundreds"/>.
    /// </summary>
    private static readonly decimal[] CharmStepsHundreds = { 100000m, 50000m, 25000m, 10000m, 5000m, 2500m, 1000m, 500m, 100m };

    /// <summary>
    /// Largest <see cref="RoundingConvention.Gj50Charm"/> point ≤ price: x5.50 / x9.50 in €30–99, xx9.50 in
    /// €100–999, else the Weber-scaled .50 grid (under €30 and €1000+).
    /// </summary>
    private static decimal Gj50CharmGridDown(decimal price, decimal relativePrecision)
    {
        if (price >= Gj50TensFrom && price < Gj50HundredsFrom)
            return Math.Max(SnapDown(price, 10m, 5.5m), SnapDown(price, 10m, 9.5m));
        if (price >= Gj50HundredsFrom && price < Gj50WeberFrom)
            return SnapDown(price, 10m, 9.5m);
        return Gj50CharmSnap(price, relativePrecision, up: false, CharmSteps, 0.5m);
    }

    /// <summary>Smallest <see cref="RoundingConvention.Gj50Charm"/> point ≥ price (same ranges as <see cref="Gj50CharmGridDown"/>).</summary>
    private static decimal Gj50CharmGridUp(decimal price, decimal relativePrecision)
    {
        if (price >= Gj50TensFrom && price < Gj50HundredsFrom)
        {
            var up = Math.Min(SnapUp(price, 10m, 5.5m), SnapUp(price, 10m, 9.5m));
            return up < Gj50HundredsFrom ? up : SnapUp(price, 10m, 9.5m);   // past 99.50 → 109.50, never a 105.50
        }
        if (price >= Gj50HundredsFrom && price < Gj50WeberFrom)
            return SnapUp(price, 10m, 9.5m);
        return Gj50CharmSnap(price, relativePrecision, up: true, CharmSteps, 0.5m);
    }

    /// <summary>Charm point nearest below/above <paramref name="price"/> on the Weber-scaled grid (offset step−gap).</summary>
    private static decimal Gj50CharmSnap(decimal price, decimal relativePrecision, bool up, decimal[] steps, decimal gap)
    {
        var step = Gj50CharmStep(price, relativePrecision, steps);
        var offset = step - gap;
        return up ? SnapUp(price, step, offset) : SnapDown(price, step, offset);
    }

    /// <summary>The coarsest "nice" charm step within the Weber budget (~relativePrecision of price), floored at the smallest step.</summary>
    private static decimal Gj50CharmStep(decimal price, decimal relativePrecision, decimal[] steps)
    {
        var budget = Math.Abs(price) * relativePrecision;
        foreach (var step in steps)
            if (step <= budget) return step;
        return steps[^1]; // smallest step for low prices — the whole-currency + gap grid
    }

    /// <summary>Largest value of form n·step + offset (n integer) that is ≤ price. step 1/offset .99 → x.99; step .10/offset .09 → x.x9.</summary>
    private static decimal SnapDown(decimal price, decimal step, decimal offset) =>
        Math.Floor((price - offset) / step) * step + offset;

    /// <summary>Smallest value of form n·step + offset (n integer) that is ≥ price.</summary>
    private static decimal SnapUp(decimal price, decimal step, decimal offset) =>
        Math.Ceiling((price - offset) / step) * step + offset;

    private static decimal Charm995Down(decimal price)
    {
        var candidate = Math.Floor(price / 5m) * 5m;
        if (candidate % 100m == 0m) candidate -= 5m; // 1000 -> 995, 1100 -> 1095
        return candidate;
    }

    private static decimal Charm995Up(decimal price)
    {
        var candidate = Math.Ceiling(price / 5m) * 5m;
        if (candidate % 100m == 0m)
        {
            // Prefer the 995-style value just below; only valid if it is still >= price.
            var below = candidate - 5m;
            if (below >= price) return below;
        }
        return candidate;
    }

    /// <summary>Largest whole-currency price ending in 99 (…99) that is ≤ input. e.g. 6149 -> 6099.</summary>
    private static decimal Hundreds99Down(decimal price)
    {
        var candidate = Math.Floor(price / 100m) * 100m + 99m;
        return candidate <= price ? candidate : candidate - 100m;
    }

    /// <summary>Smallest whole-currency price ending in 99 (…99) that is ≥ input. e.g. 9990 -> 9999.</summary>
    private static decimal Hundreds99Up(decimal price)
    {
        var candidate = Math.Floor(price / 100m) * 100m + 99m;
        return candidate >= price ? candidate : candidate + 100m;
    }

    /// <summary>Round to 2 decimals without leaving the guardrail bounds.</summary>
    public static decimal Normalize(decimal price, PriceBounds bounds)
    {
        var rounded = Math.Round(price, 2, MidpointRounding.AwayFromZero);
        if (rounded < bounds.Lower) rounded = Math.Ceiling(bounds.Lower * 100m) / 100m;
        if (rounded > bounds.Upper) rounded = Math.Floor(bounds.Upper * 100m) / 100m;
        // Degenerate bounds (e.g. lower == upper with >2 decimals) — fall back to the exact bound.
        if (rounded < bounds.Lower) rounded = bounds.Lower;
        return rounded;
    }
}
