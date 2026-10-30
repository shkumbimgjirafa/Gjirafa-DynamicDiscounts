using PricingTool.Core.Domain;
using PricingTool.Core.Options;

namespace PricingTool.Core.Services;

/// <summary>
/// One SKU's inputs to the per-band margin-target guardrail, captured during the baseline scan.
/// Prices are the layer's VAT-inclusive currency; <c>Cost</c> is PPTCV (same space). <c>IsLocked</c> =
/// an enabled algorithm cast an effective vote (conclusion fixed, never moved). <c>Price</c> is the
/// reference price the guardrail measures the band margin on and nudges from — the orchestrator feeds
/// the anchor (ProductPricing.FinalPrice), so today's (possibly discounted) current price plays no part
/// in the margin math. <c>Floor</c> is the margin-floor price; <c>Ceiling</c> is the anchor (a lift never
/// prices above list).
/// </summary>
public record MarginCandidate(
    string Sku,
    int BandId,
    bool IsLocked,
    decimal Price,
    decimal Cost,
    decimal Floor,
    decimal Ceiling);

/// <summary>The guardrail's pre-rounding target for one moved SKU. <c>IsTrim</c> = discounted (else raised).</summary>
public record MarginOverride(decimal AdjustedPrice, bool IsTrim);

/// <summary>Per-band diagnostics for logging and tests.</summary>
public record MarginBandResult(
    int BandId, decimal TargetPct, decimal BaselineMarginPct,
    bool NoFreeProducts, bool Unreachable, int MovedCount);

public record MarginTargetResult(
    IReadOnlyDictionary<string, MarginOverride> Overrides,
    IReadOnlyList<MarginBandResult> Bands);

/// <summary>
/// Per-band margin-target guardrail (post-pass, pure). For each band that has a target it nudges the
/// band's blended margin toward the goal by re-pricing ONLY the SKUs no algorithm moved ("free"),
/// leaving every algorithm conclusion ("locked") intact, bounded by each SKU's floor and anchor.
///
/// <para>The metric is unit-weighted per band: <c>(Σ price − Σ cost) / Σ price = target</c>. Locked
/// prices are fixed, so the free set must supply the difference. The shift is allocated across the free
/// SKUs in proportion to each one's headroom, measured only to that SKU's own target-margin price and
/// never past it (a trim stays above the floor; a lift stays below the anchor). A SKU already on the
/// target's correct side therefore has zero headroom and is left untouched, and no move can carry a SKU
/// across the goal. Because the total shift is capped at the total available headroom, a single
/// proportional pass never breaches a bound; when the free set can't close the gap the band is left short
/// (Unreachable) rather than trimming past the goal into the floor. Returned prices are pre-rounding — the
/// caller re-clamps and re-rounds them through the normal pipeline tail.</para>
///
/// <para>New-product-protected and locally-held dead-stock SKUs are excluded upstream (during the scan)
/// and must not appear in <paramref name="candidates"/> at all — they neither count toward a band's
/// margin nor get adjusted.</para>
/// </summary>
public class MarginTargetService
{
    /// <summary>Moves smaller than this (currency units) are dropped — they'd vanish under rounding.</summary>
    private const decimal MinMove = 0.005m;

    public MarginTargetResult BuildOverrides(
        IReadOnlyList<PriceBandConfig> bands,
        IReadOnlyList<MarginCandidate> candidates,
        PricingEngineOptions options)
    {
        var overrides = new Dictionary<string, MarginOverride>(StringComparer.OrdinalIgnoreCase);
        var bandResults = new List<MarginBandResult>();

        var byBand = candidates
            .GroupBy(c => c.BandId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var band in bands)
        {
            if (band.TargetMarginPct is not decimal targetPct) continue;   // no goal → guardrail off
            if (targetPct <= 0m || targetPct >= 100m) continue;            // invalid → ignore (UI validates too)
            if (!byBand.TryGetValue(band.BandId, out var items) || items.Count == 0) continue;

            var targetFrac = targetPct / 100m;
            var sumCost = items.Sum(c => c.Cost);
            var sumPrice = items.Sum(c => c.Price);
            if (sumPrice <= 0m) continue;

            var baselineMarginPct = (sumPrice - sumCost) / sumPrice * 100m;

            // Deadband: already within tolerance of the goal → don't churn prices for a rounding-level miss.
            if (Math.Abs(baselineMarginPct - targetPct) <= options.MarginTargetTolerancePct)
            {
                bandResults.Add(new MarginBandResult(band.BandId, targetPct, baselineMarginPct, false, false, 0));
                continue;
            }

            var free = items.Where(c => !c.IsLocked).ToList();
            if (free.Count == 0)
            {
                // Every product in the band is an algorithm conclusion — nothing the guardrail may touch.
                bandResults.Add(new MarginBandResult(band.BandId, targetPct, baselineMarginPct, true, true, 0));
                continue;
            }

            // Total band revenue that makes the blended margin == target (locked prices fixed). The whole
            // shift falls on the free set: delta = requiredTotalPrice − currentTotalPrice.
            var requiredTotalPrice = sumCost / (1m - targetFrac);
            var delta = requiredTotalPrice - sumPrice;     // <0 → trim (discount free); >0 → lift (raise free)
            var isTrim = delta < 0m;

            // The price at which THIS SKU sits exactly at the band target margin. Headroom runs only from
            // the current price to that target price -- never past it -- so a SKU already on the target's
            // correct side has zero headroom (it is left untouched) and no move can carry a SKU across the
            // goal. A trim is still floored (the target price sits above the floor); a lift is still capped
            // at the anchor. When the free set's headroom can't close the gap the band is left short
            // (Unreachable) rather than trimming past the goal down toward the floor.
            decimal TargetPrice(MarginCandidate c) => c.Cost / (1m - targetFrac);
            decimal Headroom(MarginCandidate c) => isTrim
                ? Math.Max(0m, c.Price - Math.Max(TargetPrice(c), c.Floor))    // surrender only margin ABOVE target
                : Math.Max(0m, Math.Min(TargetPrice(c), c.Ceiling) - c.Price); // recover only margin BELOW target

            var totalHeadroom = free.Sum(Headroom);
            if (totalHeadroom <= 0m)
            {
                bandResults.Add(new MarginBandResult(band.BandId, targetPct, baselineMarginPct, false, true, 0));
                continue;
            }

            var need = Math.Abs(delta);
            var toMove = Math.Min(need, totalHeadroom);
            var unreachable = need > totalHeadroom;        // can't close the gap without pushing SKUs past the target

            var moved = 0;
            foreach (var c in free)
            {
                var h = Headroom(c);
                if (h <= 0m) continue;
                var move = toMove * (h / totalHeadroom);   // ≤ h since toMove ≤ totalHeadroom → always in-bounds
                if (move < MinMove) continue;
                var adjusted = Math.Clamp(isTrim ? c.Price - move : c.Price + move, c.Floor, c.Ceiling);
                overrides[c.Sku] = new MarginOverride(adjusted, isTrim);
                moved++;
            }

            bandResults.Add(new MarginBandResult(band.BandId, targetPct, baselineMarginPct, false, unreachable, moved));
        }

        return new MarginTargetResult(overrides, bandResults);
    }
}
