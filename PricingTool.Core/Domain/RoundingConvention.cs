namespace PricingTool.Core.Domain;

public enum RoundingConvention
{
    /// <summary>No psychological rounding; price is only normalized to 2 decimals.</summary>
    None = 0,

    /// <summary>Prices end in .99 (e.g. 7.99, 49.99). Typical for low bands.</summary>
    EndsIn99 = 1,

    /// <summary>Prices end in .95 (e.g. 7.95).</summary>
    EndsIn95 = 2,

    /// <summary>Whole euro amounts (e.g. 120).</summary>
    WholeEuro = 3,

    /// <summary>
    /// Multiples of 5 EUR; round-hundred results step down to X95 (e.g. 1000 -> 995, 1100 -> 1095).
    /// Intended for the EUR 1,000+ bands.
    /// </summary>
    Charm995 = 4,

    /// <summary>
    /// Whole-currency prices whose last two digits are 99 (e.g. 6199, 9999). For currencies with no
    /// minor unit / large nominal values (MKD, ALL) where the .99/.95 EUR conventions don't apply.
    /// </summary>
    EndsIn99Hundreds = 5,

    /// <summary>
    /// Prices end in .50 (e.g. 47.50, 149.50) — Gjirafa50's signature ending. The grid is whole
    /// currency + 0.50 (step 1, offset .50). Selection and guardrail safety are handled by the
    /// standard <see cref="RoundingService.Apply"/> nearest-candidate logic, so the snapped price
    /// stays on the engine's profit-optimal target and never breaches the margin floor / OldPrice cap.
    /// </summary>
    EndsIn50 = 6,

    /// <summary>
    /// Gjirafa50 charm rounding — the platform version of the old Power BI "GJ50 KS FP Final" logic.
    /// Two independent ideas combined:
    ///
    /// <para><b>Grid (Weber's Law):</b> the charm granularity scales with price magnitude so the
    /// snap is always a roughly constant fraction (~<c>relativePrecision</c>, default 2%) of the
    /// price — no hard cliffs. The step is the coarsest "nice" value (1, 5, 10, 25, 50, 100, 250,
    /// 500, 1000) that fits the budget, and every charm point is <c>(multiple of step) − 0.50</c>:
    /// step 1 → …50, step 5 → …4.50/…9.50, step 10 → …9.50, step 100 → …99.50, etc. Every ending is
    /// "just below a round number". See <c>docs/Webers-Law-Pricing.md</c>.</para>
    ///
    /// <para><b>Selection (round-up biased):</b> unlike the other conventions (nearest), this one
    /// prefers the <i>higher</i> charm point to claw back margin — but only when it makes sense:
    /// (1) it stays at/below the anchor / OldPrice cap (enforced by the bounds check),
    /// (2) it does not land just above a salient round number (no 199.80 → 200.50 "1xx → 2xx" jump), and
    /// (3) the up-move stays within the Weber tolerance. The result <b>always ends in .50</b>: when no
    /// charm point fits the budget (cheap items, where one .50 step exceeds the tolerance) it snaps to
    /// the nearest .50 instead of clawing up. Only a pinned/narrow guardrail band can yield a non-.50
    /// hold.</para>
    ///
    /// <para><b>Fixed-ending ranges (2026-10-21)</b> override the Weber grid in the middle:
    /// <b>€30–99</b> → the <i>nearest</i> x5.50 / x9.50 (35.50, 39.50 … 99.50; 32.60 → 35.50, 47.31 → 45.50);
    /// <b>€100–999</b> → xx9.50 (109.50 … 999.50) with the up-biased selection above. Under €30 and
    /// €1000+ keep the Weber grid. A band too narrow for any fixed-ending point falls back to the Weber .50.</para>
    /// </summary>
    Gj50Charm = 7,

    /// <summary>
    /// GjirafaMall charm rounding — Power BI's <b>…49 / …99</b> endings, selected the platform way:
    /// <list type="number">
    /// <item><b>Round-ten euro</b> (integer part a multiple of 10 — 50.xx, 120.xx, 100.xx, 1000.xx):
    /// pull <b>down</b> across the ten to the …9.99 just below it (50.30 → 49.99, 120.88 → 119.99,
    /// 100.00 → 99.99) — never a …0.99. (Falls back to the nearest …49/…99 when the pull exceeds the
    /// Weber budget, e.g. cheap 10.40 → 10.49.)</item>
    /// <item>Otherwise <b>round up to this euro's …99</b> when the Weber budget (k·price) reaches it
    /// (45.40 → 45.99), clawing margin; if it can't afford the jump, the nearest …49/…99.</item>
    /// </list>
    /// Clamped to the guardrail bounds (margin floor / OldPrice cap).
    ///
    /// <para>Unlike the raw PBI column — which forces the …99 at any distance (123.76 → 124.99) — the
    /// up-jump here is bounded by the Weber tolerance, so 123.76 → 123.99.</para>
    ///
    /// <para><b>Low-price range:</b> below <c>lowPriceThreshold</c> (€5) the same up-biased Weber logic
    /// runs on a finer 10-cent <b>.x9 grid</b> (…0.09, 0.19, …, 4.99): round up to the next .x9 when the
    /// budget allows (4.37 → 4.39), else the nearest .x9 (1.21 → 1.19). So cheap items also end in …9.</para>
    /// </summary>
    GjmCharm = 8,

    /// <summary>
    /// Gjirafa50 charm for whole-currency markets (MKD / ALL — no minor unit). The <see cref="Gj50Charm"/>
    /// Weber engine scaled to whole currency: the charm grid step is the coarsest "nice" value (100, 500,
    /// 1000, 2500, 5000, …) within the ~2% Weber budget, and every charm point sits <b>50 below a round
    /// step-multiple</b>, so the price <b>always ends in …50</b> (150, 1 150, 6 150; coarsening to …950,
    /// …9 950 at higher magnitudes — "just below a round number"). Selection is round-up-biased with the
    /// same salience/monotonicity guards as <see cref="Gj50Charm"/> (never lands just above a salient round
    /// number; round-thousand blocks pull to the …950 just below). The whole-currency analog of the EUR
    /// …50 ending — a €-item's 47.50 becomes a MKD/ALL item's …50. Clamped to the guardrail bounds.
    /// </summary>
    Gj50CharmHundreds = 9,

    /// <summary>
    /// GjirafaMall charm for whole-currency markets (MKD / ALL — no minor unit). The whole-currency analog
    /// of <see cref="GjmCharm"/>: prices end in <b>…90 or …50</b>.
    /// <list type="bullet">
    /// <item><b>…90 (primary, margin-claw):</b> the "just below a round number" charm on the same
    /// Weber-scaled step grid as <see cref="Gj50CharmHundreds"/> (charm gap 10 instead of 50). Claw the
    /// price UP to the near-round …90 when the ~2% Weber budget allows (…990 / …9 990 as the grid coarsens
    /// with magnitude), reclaiming margin — but, via the shared salience guard, never landing just above a
    /// salient round number (a round-thousand block resolves to the …990 just below it).</item>
    /// <item><b>…50 (fallback):</b> in the sub-budget regime (cheap items, where the 100-wide …90 grid is
    /// coarser than the Weber budget) the price snaps to the nearest point of the finer …50 / …90 grid, so
    /// a cheap SKU isn't dragged far just to end in …90.</item>
    /// </list>
    /// Unlike <see cref="GjmCharm"/>'s fixed half-euro grid, this convention coarsens with magnitude (true
    /// Weber). Its endings never coincide with a round number, so no separate round-number ban is needed.
    /// Clamped to the guardrail bounds (margin floor / OldPrice cap).
    /// </summary>
    GjmCharmHundreds = 10,
}
