namespace PricingTool.Web.Services;

/// <summary>
/// Pure recompute for a manual Proposed-price override. Mirrors how the engine derives the same fields
/// (<see cref="PricingTool.Core.Domain.PricingDecision.ChangePct"/> and the orchestrator's rounding) so a
/// manually-edited row is indistinguishable from an engine-priced one downstream: <c>ChangePct</c> feeds
/// the ±20% approval gate and the Δ% display, <c>HasChange</c> feeds the "changed only" / Movers filters,
/// and the DB-computed <c>AbsChangePct</c> follows <c>ChangePct</c> automatically.
/// </summary>
public static class ProposalOverride
{
    public static (decimal Proposed, decimal ChangePct, bool HasChange) Recompute(decimal currentPrice, decimal newPrice)
    {
        // Stored precision is (18,2); round money away-from-zero so a typed 41.999 lands on 42.00.
        var proposed = Math.Round(newPrice, 2, MidpointRounding.AwayFromZero);

        // Same shape as PricingDecision.ChangePct, stored at 4 dp like the orchestrator does.
        var changePct = currentPrice > 0
            ? Math.Round((proposed - currentPrice) / currentPrice * 100m, 4)
            : 0m;

        return (proposed, changePct, proposed != currentPrice);
    }
}
