namespace PricingTool.Core.Services;

/// <summary>
/// The profit &amp; margin impact of a run's proposed price changes over one trailing sales window,
/// versus a naive do-nothing baseline: assume we keep selling the SAME units we sold in the window,
/// at the CURRENT price ("now") vs the PROPOSED price. Only SKUs with a known cost contribute.
///
/// Prices and cost (PPTCV) are both VAT-INCLUSIVE. Reported PROFIT is VAT-EXCLUDED — the VAT in the
/// selling price (and the reclaimable VAT inside PPTCV) is pass-through, not earnings — so profit
/// (and the revenue it's measured against) is divided by (1 + VAT). Margin % is unaffected: the VAT
/// cancels in netProfit/netRevenue, which still equals (price − cost) / price.
/// </summary>
public readonly record struct WindowProfit(
    int WindowDays,
    decimal ProfitNow,
    decimal ProfitProposed,
    decimal RevenueNow,
    decimal RevenueProposed)
{
    public decimal ProfitDelta => ProfitProposed - ProfitNow;

    /// <summary>Relative profit change; null when the baseline profit is non-positive (the % is meaningless).</summary>
    public decimal? ProfitDeltaPct =>
        ProfitNow > 0 ? (ProfitProposed - ProfitNow) / ProfitNow * 100m : null;

    public decimal? MarginNowPct => RevenueNow > 0 ? ProfitNow / RevenueNow * 100m : null;
    public decimal? MarginProposedPct => RevenueProposed > 0 ? ProfitProposed / RevenueProposed * 100m : null;
    public decimal? MarginDeltaPp =>
        MarginNowPct is decimal a && MarginProposedPct is decimal b ? b - a : null;
}

/// <summary>
/// An elasticity-AWARE projection for one trailing window: the proposed-side units of confidently-elastic
/// SKUs are scaled by the fitted demand response q' = q·(P_proposed / P_current)^E (E &lt; 0), instead of held
/// constant. This is what the elastic algorithm actually predicts — the naive same-units baseline charges
/// a markdown for the price cut while denying it the volume it cut the price to buy. Dead-stock proposals
/// are excluded (their counterfactual is "the unit doesn't sell", not a demand-curve response). Profit and
/// revenue are reported across ALL cost-known SKUs (elastic ones scaled, the rest at constant units), so
/// the numbers are directly comparable to the naive <see cref="WindowProfit"/> for the same window.
///
/// Two lift figures, deliberately distinct:
///  - <see cref="PredictedUnitsLiftPct"/> — raw quantity-weighted volume response (Σqm/Σq − 1). Informational
///    headline only; with heterogeneous per-unit margins it can overstate the profit effect (the volume may
///    land on thin-margin SKUs), so it must NOT be compared directly to the break-even threshold.
///  - <see cref="PredictedLiftPct"/> — the SAME demand response weighted by margin contribution. This is the
///    figure that is apples-to-apples with <see cref="RequiredLiftPct"/>: PredictedLiftPct ≥ RequiredLiftPct
///    is exactly equivalent to <see cref="ClearsBreakeven"/>. Null when the proposed price is at/below cost.
/// </summary>
public readonly record struct ElasticProjection(
    int WindowDays,
    decimal ProfitProposed,
    decimal RevenueProposed,
    int SkuCount,
    decimal PredictedUnitsLiftPct,
    decimal? PredictedLiftPct,
    decimal? RequiredLiftPct,
    bool ClearsBreakeven)
{
    public decimal? MarginProposedPct => RevenueProposed > 0 ? ProfitProposed / RevenueProposed * 100m : null;
}

/// <summary>
/// One Proposals-Review window KPI: the conservative same-units <see cref="WindowProfit"/> (shared with
/// Movers) plus the optional elasticity-aware <see cref="ElasticProjection"/>. <see cref="Elastic"/> is null
/// when the current view has no confidently-elastic SKU. Profit deltas for the elastic lens are measured
/// against the SAME "now" baseline as the naive lens, so the two are like-for-like.
/// </summary>
public sealed record ProposalWindowKpi(WindowProfit Naive, ElasticProjection? Elastic)
{
    public bool HasElastic => Elastic is not null;

    public decimal? ElasticProfitDelta =>
        Elastic is ElasticProjection e ? e.ProfitProposed - Naive.ProfitNow : null;

    public decimal? ElasticProfitDeltaPct =>
        Elastic is ElasticProjection e && Naive.ProfitNow > 0
            ? (e.ProfitProposed - Naive.ProfitNow) / Naive.ProfitNow * 100m : null;

    public decimal? ElasticMarginDeltaPp =>
        Elastic is ElasticProjection e && Naive.MarginNowPct is decimal a && e.MarginProposedPct is decimal b
            ? b - a : null;
}

public static class KpiMath
{
    /// <summary>
    /// Build one window's profit/margin from three pre-summed terms over the cost-known SKUs (all
    /// VAT-inclusive): Σ(currentPrice·qty), Σ(proposedPrice·qty), Σ(cost·qty). Profit and revenue are
    /// reported VAT-EXCLUDED (÷ (1 + vat)) — the VAT is pass-through, not earnings. Margin % is the same
    /// either way. Identical whether the caller summed in memory (Movers) or in SQL (Proposals).
    /// </summary>
    public static WindowProfit FromSums(
        int windowDays,
        decimal sumCurrentPriceQty,
        decimal sumProposedPriceQty,
        decimal sumCostQty,
        decimal vatRatePct)
    {
        var k = 1m + vatRatePct / 100m;
        var revenueNow = sumCurrentPriceQty / k;
        var revenueProposed = sumProposedPriceQty / k;
        var costNet = sumCostQty / k;
        return new WindowProfit(
            windowDays,
            revenueNow - costNet,
            revenueProposed - costNet,
            revenueNow,
            revenueProposed);
    }

    /// <summary>
    /// Layer the elasticity-aware projection on top of an already-computed naive <see cref="WindowProfit"/>.
    /// All "delta" sums are over the confidently-elastic, non-dead-stock subset only (VAT-INCLUSIVE, raw):
    ///  - proposedDeltaSum = Σ P_proposed·qty·(m−1)   — the extra proposed revenue from the volume response
    ///  - costDeltaSum     = Σ cost·qty·(m−1)         — the extra cost of those extra units
    ///  - qtySum / qtyAdjSum = Σ qty / Σ qty·m        — for the raw (quantity-weighted) units lift
    ///  - curMarginSum / propMarginSum = Σ (P_current−cost)·qty / Σ (P_proposed−cost)·qty — for break-even
    /// where m = clamp((P_proposed/P_current)^E). Returns null when the subset is empty (no elastic SKU in view).
    /// </summary>
    public static ElasticProjection? ElasticProjectionFromSums(
        int windowDays,
        WindowProfit naive,
        decimal vatRatePct,
        decimal proposedDeltaSum,
        decimal costDeltaSum,
        decimal qtySum,
        decimal qtyAdjSum,
        decimal curMarginSum,
        decimal propMarginSum,
        int skuCount)
    {
        if (skuCount == 0 || qtySum <= 0) return null;

        var k = 1m + vatRatePct / 100m;
        // Scale only the elastic subset's proposed side; the rest of the catalogue stays at naive units.
        var profitProposed = naive.ProfitProposed + (proposedDeltaSum - costDeltaSum) / k;
        var revenueProposed = naive.RevenueProposed + proposedDeltaSum / k;

        // Raw volume response (quantity-weighted) — informational headline only; see remarks on the type:
        // NOT directly comparable to the break-even threshold when per-unit margins differ across the subset.
        var predictedUnitsLiftPct = (qtyAdjSum - qtySum) / qtySum * 100m;

        // Break-even comparison, MARGIN-weighted so the two figures and the verdict are internally consistent:
        //  - requiredLiftPct  : the uniform volume lift that holds subset profit flat = curMargin/propMargin − 1.
        //  - predictedLiftPct : the SAME demand response weighted by margin contribution
        //        = Σ(P_prop−cost)·q·(m−1) / Σ(P_prop−cost)·q = (proposedDeltaSum − costDeltaSum)/propMarginSum.
        // With this weighting, predictedLiftPct ≥ requiredLiftPct is EXACTLY equivalent to ClearsBreakeven below
        // (multiply through by propMarginSum > 0). Both are undefined at/below cost (propMarginSum ≤ 0).
        decimal? requiredLiftPct = propMarginSum > 0 ? (curMarginSum / propMarginSum - 1m) * 100m : null;
        decimal? predictedLiftPct = propMarginSum > 0 ? (proposedDeltaSum - costDeltaSum) / propMarginSum * 100m : null;

        // Ground-truth verdict: elastic-adjusted proposed profit ≥ current profit on the elastic subset.
        // Computed from raw profit so it is well-defined even at/below cost where the % figures are null.
        var subsetProfitNow = curMarginSum / k;
        var subsetProfitProposed = (propMarginSum + (proposedDeltaSum - costDeltaSum)) / k;
        var clears = subsetProfitProposed >= subsetProfitNow;

        return new ElasticProjection(
            windowDays, profitProposed, revenueProposed, skuCount,
            predictedUnitsLiftPct, predictedLiftPct, requiredLiftPct, clears);
    }
}
