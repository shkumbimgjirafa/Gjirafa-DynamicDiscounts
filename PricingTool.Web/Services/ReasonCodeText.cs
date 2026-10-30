using System.Reflection;
using PricingTool.Core.Domain;

namespace PricingTool.Web.Services;

/// <summary>Plain-language descriptions for reason codes and guardrail flags shown in the UI.</summary>
public static class ReasonCodeText
{
    private static readonly Dictionary<string, string> Map = new()
    {
        ["SELL_THROUGH_REMOVE"] = "Sells out soon — discount removed",
        ["SELL_THROUGH_FAST"] = "Fast sell-through — discount trimmed",
        ["SELL_THROUGH_HOLD"] = "Sell-through on pace — hold",
        ["SELL_THROUGH_SLOW"] = "Slow sell-through — deeper discount",
        ["NEW_PRODUCT_PROTECTED"] = "New product (MarkAsNew window) — price held, no discount",
        ["ALGORITHMS_DISABLED"] = "Algorithms off — margin floor + rounding only",
        ["SYNCED_TO_GJ50"] = "Synced to Gjirafa50 — adopted Gjirafa50's proposed price (shared product)",
        // Legacy reason codes from the pre-consolidation roster (kept so old proposals still render):
        ["VELOCITY_FORECAST"] = "Sell-through forecast",
        ["STOCK_AGING"] = "Stock aging without sales",
        ["STOCKOUT_RISK"] = "Sells out soon — discount unnecessary",
        ["ELASTIC_OPTIMAL"] = "Elastic demand — moved to profit-max price",
        ["INELASTIC_DEMAND"] = "Discount didn't lift sales",
        ["ELASTIC_RESPONSE"] = "Sales respond to discount — protected",
        ["HIGH_MARGIN_ROOM"] = "High margin can absorb discount",
        ["THIN_MARGIN_CONSERVE"] = "Thin margin — conservative",
        ["DEAD_STOCK_MARKDOWN"] = "Dead stock — progressive markdown",
        ["DISCOUNT_WASTED"] = "Discount giving margin away for nothing",
        ["MOMENTUM_UP"] = "Demand accelerating",
        ["MOMENTUM_DOWN"] = "Demand decelerating",
        ["SUPPLIER_ONLY_SLOW"] = "Supplier-warehouse-only slow mover",
        ["LOCAL_FAST"] = "Local stock selling well",
        ["MISSING_COST"] = "Missing cost data (PPTCV) — skipped",
        ["MISSING_PRICE"] = "No usable shelf price — skipped",
        ["NO_BAND"] = "No matching price band — skipped",
        ["MARGIN_FLOOR_CLAMPED"] = "Raised to margin floor",
        ["CURRENT_BELOW_FLOOR_LIFTED"] = "Current price was below the margin floor — raised to the floor",
        ["CAPPED_AT_ANCHOR"] = "Capped — not raised above the current price or the anchor (whichever is higher)",
        ["MARGIN_FLOOR_ABOVE_ANCHOR"] = "⚠ Even the current price / anchor misses the margin floor",
        ["ANCHOR_FALLBACK_TO_SHELF"] = "No FinalPrice — anchored to the shelf price",
        ["MIN_DISCOUNT_SUPPRESSED"] = "Discount below the minimum — held at full shelf price (never above the anchor / current price)",
        // Legacy flag codes kept so historical proposals still render:
        ["CAPPED_AT_OLD_PRICE"] = "Capped at full shelf price",
        ["MARGIN_FLOOR_ABOVE_OLD_PRICE"] = "⚠ Even full price misses margin floor",
        ["ROUNDING_SKIPPED_OUT_OF_BOUNDS"] = "Rounding skipped (would breach guardrail)",
        ["SUPPLIER_ONLY_NO_MARKDOWN"] = "Supplier-only dead stock — markdown blocked",
        ["DEAD_STOCK_FLOOR_RELAXED"] = "Dead stock — discount taken below the margin floor (margin given away to clear it, never below cost)",
        ["DEAD_STOCK_TUNNEL_HELD"] = "Dead stock started selling — price held at the markdown level",
        ["DEAD_STOCK_BELOW_STAGE_HELD"] = "Already below this age stage's floor — held, not raised; markdown resumes once it ages into a deeper stage",
        ["DEAD_STOCK_HARD_FLOOR_CLAMPED"] = "⚠ Was below the deepest (stage-4) cost floor — pulled back up to it",
        ["MARGIN_TARGET_TRIM"] = "Trimmed toward the band's margin goal",
        ["MARGIN_TARGET_LIFT"] = "Raised toward the band's margin goal",
        ["MARGIN_TARGET_ADJUSTED"] = "Adjusted to meet the band's margin goal",
        ["GTIN_PARITY_MATCHED"] = "Same product (EAN/GTIN) is cheaper under another SKU — matched down to it",
        ["GTIN_PARITY_FLOOR_BLOCKED"] = "⚠ Could not fully match the cheaper listing of this EAN/GTIN — stopped at the margin floor",
    };

    /// <summary>The guardrail/reason code emitted for products inside the platform MarkAsNew window.</summary>
    public const string NewProductCode = "NEW_PRODUCT_PROTECTED";

    /// <summary>The fixed guardrail-flag vocabulary (the constants on <see cref="GuardrailFlags"/>), each
    /// paired with its friendly text and sorted by that text — the option list for the Proposals Guardrails
    /// filter dropdown. Reflected once so it stays in sync as flags are added; values are the raw codes the
    /// substring filter matches, exactly like the Algorithm dropdown lists its codes.</summary>
    public static readonly IReadOnlyList<(string Code, string Text)> GuardrailCatalog =
        typeof(GuardrailFlags)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(fld => fld.IsLiteral && fld.FieldType == typeof(string))
            .Select(fld => (string)fld.GetRawConstantValue()!)
            .Select(code => (Code: code, Text: Describe(code)))
            .OrderBy(x => x.Text, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string Describe(string code) =>
        Map.TryGetValue(code.Trim(), out var text) ? text : code;

    /// <summary>
    /// Splits a CSV reason/guardrail string into (code, friendly-text) pairs, optionally dropping
    /// codes in <paramref name="exclude"/> (e.g. ones already surfaced as a dedicated badge).
    /// </summary>
    public static IEnumerable<(string Code, string Text)> DescribeList(string csv, params string[] exclude) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Where(c => exclude.Length == 0 || !exclude.Contains(c, StringComparer.Ordinal))
           .Select(c => (c, Describe(c)));

    /// <summary>True when any of the supplied CSV reason/guardrail strings contains <paramref name="code"/>.</summary>
    public static bool HasCode(string code, params string?[] csvs) =>
        csvs.Any(csv => !string.IsNullOrEmpty(csv)
            && csv!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Contains(code, StringComparer.Ordinal));

    /// <summary>
    /// True when a proposal is a held new product (inside the platform MarkAsNew window). Pass its
    /// ReasonCodes and/or GuardrailFlags — the flag is written to both.
    /// </summary>
    public static bool IsNewProduct(params string?[] reasonOrGuardrailCsvs) =>
        HasCode(NewProductCode, reasonOrGuardrailCsvs);

    /// <summary>
    /// The Proposals grid's computed "current price is below the band's margin floor" diagnostic — shown
    /// as a Guardrails badge and exported as a column. True when the CURRENT price yields a margin under
    /// the band's floor (<paramref name="bandMarginFloorPct"/>). This is NOT an engine guardrail flag —
    /// it's derived from the row, so it applies retroactively to existing proposals. Suppressed where a
    /// below-floor price is deliberate and already flagged elsewhere: locally-held dead stock (any
    /// DEAD_STOCK_* guardrail) and Gjirafa50-synced SKUs (Mall guardrails skipped). Shared by the view and
    /// both CSV exports so all three stay identical.
    /// </summary>
    public static bool IsCurrentBelowFloor(
        decimal? pptcv, decimal currentPrice, decimal? bandMarginFloorPct,
        string? guardrailFlags, string? reasonCodes)
    {
        if (pptcv is not decimal cost || currentPrice <= 0m) return false;
        if (bandMarginFloorPct is not decimal floor) return false;
        if ((currentPrice - cost) / currentPrice * 100m >= floor) return false;
        if (!string.IsNullOrEmpty(guardrailFlags) && guardrailFlags.Contains("DEAD_STOCK", StringComparison.Ordinal)) return false;
        return !HasCode(GuardrailFlags.SyncedToGj50, reasonCodes);
    }
}
