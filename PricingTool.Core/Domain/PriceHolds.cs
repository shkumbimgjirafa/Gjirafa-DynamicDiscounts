namespace PricingTool.Core.Domain;

/// <summary>
/// Which priced rows the engine is HOLDING (or adopting) rather than pricing itself, read back from a
/// proposal's stage, flags and reason codes — the SKUs a band target or a discount trim cannot move: dead
/// stock (its markdown is a deliberate clearing price), the clearance pin, Mall rows adopting Gjirafa50's
/// price, the new-product hold, the dead-stock tunnel/stage holds, and a floor-above-anchor SKU held for a
/// human. Shared by the Price Bands views so "what we can change" means the same thing everywhere on the page.
/// </summary>
public static class PriceHolds
{
    private static readonly HashSet<string> HoldFlags = new(StringComparer.Ordinal)
    {
        GuardrailFlags.ClearanceLockHeld,
        GuardrailFlags.NewProductProtected,
        GuardrailFlags.DeadStockTunnelHeld,
        GuardrailFlags.DeadStockBelowStageHeld,
        GuardrailFlags.DeadStockFloorRelaxed,
        GuardrailFlags.MarginFloorAboveAnchor,
    };

    /// <summary>True when the row's price is held/adopted (see the class remarks); false when the engine prices it.
    /// A Gjirafa50 adoption is recorded in the REASON codes (SYNCED_TO_GJ50 leads them; the row carries no
    /// guardrail flags at all, since no Mall guardrail ran), so both lists are read.</summary>
    public static bool IsHeld(DeadStockStage stage, string? guardrailFlags, string? reasonCodes)
    {
        if (stage != DeadStockStage.None) return true;
        if (HasCode(reasonCodes, GuardrailFlags.SyncedToGj50)) return true;
        if (string.IsNullOrEmpty(guardrailFlags)) return false;
        foreach (var f in guardrailFlags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (HoldFlags.Contains(f)) return true;
        return false;
    }

    private static bool HasCode(string? csv, string code) =>
        !string.IsNullOrEmpty(csv)
        && csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(code, StringComparer.Ordinal);
}
