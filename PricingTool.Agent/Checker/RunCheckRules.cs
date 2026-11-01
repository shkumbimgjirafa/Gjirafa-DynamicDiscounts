using System.Globalization;
using PricingTool.Core.Domain;
using PricingTool.Core.Services;

namespace PricingTool.Agent.Checker;

/// <summary>Severity of a rule hit, a model verdict or a row's final outcome.</summary>
public static class Severity
{
    /// <summary>The row is held, like a warned one, and listed for a person; the rest of the run goes ahead (since 1 Nov). Only a check
    /// that didn't finish stops the whole run's export and publish.</summary>
    public const string Block = "block";
    /// <summary>The row is left out of the export; the rest goes ahead.</summary>
    public const string Warn = "warn";
    /// <summary>Context for the model (near the floor); never holds anything by itself.</summary>
    public const string Info = "info";
    /// <summary>The model's fixed price passed every rule and is exported instead of the engine's.</summary>
    public const string Fixed = "fixed";
    public const string Pass = "pass";

    public static int Rank(string s) => s switch { Block => 3, Warn => 2, Info => 1, _ => 0 };
}

/// <summary>Everything the rules need about one changed price of an engine run. Money is the layer's
/// currency, VAT included (prices and PPTCV cost alike).</summary>
public sealed record CheckInput(
    long ProposalId, string Sku, int? BandId, string? BandName, RoundingConvention Rounding, bool RoundingEnabled,
    decimal? FloorPct, decimal Price, decimal CurrentPrice, decimal OldPrice, decimal AnchorPrice, decimal? Cost, decimal ChangePct,
    DeadStockStage DeadStock, string ReasonCodes, string GuardrailFlags)
{
    /// <summary>The SKU's cost in the layer's previous run, when there is one.</summary>
    public decimal? PreviousCost { get; init; }

    /// <summary>Layer 1 only: Gjirafa50's price for the same SKU in its latest run.</summary>
    public decimal? Gj50Price { get; init; }

    public decimal? MarginPct => Cost is { } c && Price > 0 ? 100m * (Price - c) / Price : null;
}

public sealed record RuleHit(string Code, string Severity, string Detail);

/// <summary>
/// The run checker's rules (plan section 5 "RunChecker", prompt 06), all arithmetic in code. Block: below
/// cost outside the dead-stock lane, dead stock under its 40 % hard floor, a ×10 jump, a price or cost at the
/// wrong currency scale, above max(anchor, current). Warn: below the band floor outside dead stock, cost moved
/// more than 5 % since the previous run, |change| over 20 %, off the layer's charm grid, GjirafaMall and
/// Gjirafa50 disagreeing on a shared SKU. Info: within 1 pt above the floor.
/// </summary>
public static class RunCheckRules
{
    public const decimal ConfirmationChangePct = 20m;
    public const decimal NearFloorPp = 1m;
    public const decimal FloorTolerancePp = 0.1m;
    public const decimal CostMovePct = 5m;
    /// <summary>The dead-stock lane's lowest tier; the engine clamps at it, a cent's rounding either side.</summary>
    public const decimal DeadStockHardFloorShare = 0.40m;

    public static IReadOnlyList<RuleHit> Evaluate(CheckInput r, decimal? price = null)
    {
        var p = price ?? r.Price;
        var hits = new List<RuleHit>();
        var synced = (r.ReasonCodes + "," + r.GuardrailFlags).Contains(GuardrailFlags.SyncedToGj50, StringComparison.Ordinal);
        var margin = r.Cost is { } cc && p > 0 ? 100m * (p - cc) / p : (decimal?)null;

        if (p <= 0)
            hits.Add(new("no-price", Severity.Block, "The price is zero or negative."));
        if (r.Cost is null or <= 0)
            hits.Add(new("no-cost", Severity.Warn, "No cost (PPTCV): margin can't be checked."));

        if (r.Cost is { } cost and > 0 && p > 0)
        {
            if (r.DeadStock == DeadStockStage.None && p < cost - 0.005m)
                hits.Add(new("below-cost", Severity.Block, $"Price {Money(p)} is below cost {Money(cost)} outside the dead-stock lane."));
            if (r.DeadStock != DeadStockStage.None && p < DeadStockHardFloorShare * cost - 0.01m)
                hits.Add(new("dead-stock-below-hard-floor", Severity.Block, $"Dead stock at {Money(p)} is under 40 % of cost ({Money(DeadStockHardFloorShare * cost)})."));
            if (cost > 10m * Math.Max(r.OldPrice, r.CurrentPrice) && Math.Max(r.OldPrice, r.CurrentPrice) > 0)
                hits.Add(new("cost-scale", Severity.Block, $"Cost {Money(cost)} is over 10x the shelf price {Money(Math.Max(r.OldPrice, r.CurrentPrice))}: wrong currency scale?"));
            if (r.DeadStock == DeadStockStage.None && r.FloorPct is { } floor && margin < floor - FloorTolerancePp)
                hits.Add(new("below-floor", Severity.Warn, $"Margin {Pct(margin!.Value)} is below the band floor {Pct(floor)}."));
            else if (r.FloorPct is { } f && margin >= f - FloorTolerancePp && margin < f + NearFloorPp)
                hits.Add(new("near-floor", Severity.Info, $"Margin {Pct(margin!.Value)} sits within {NearFloorPp} pt of the floor {Pct(f)}."));
        }

        if (r.CurrentPrice > 0 && (p >= 10m * r.CurrentPrice || p <= r.CurrentPrice / 10m))
            hits.Add(new("x10-jump", Severity.Block, $"Price {Money(p)} against today's {Money(r.CurrentPrice)}: a tenfold jump."));
        if (r.OldPrice > 0 && (p >= 10m * r.OldPrice || p <= r.OldPrice / 10m))
            hits.Add(new("price-scale", Severity.Block, $"Price {Money(p)} against the shelf price {Money(r.OldPrice)}: wrong currency scale?"));
        var ceiling = Math.Max(r.AnchorPrice, r.CurrentPrice);
        if (!synced && ceiling > 0 && p > ceiling + 0.005m)
            hits.Add(new("above-ceiling", Severity.Block, $"Price {Money(p)} is above max(anchor, current) = {Money(ceiling)}."));

        var change = r.CurrentPrice > 0 ? 100m * (p - r.CurrentPrice) / r.CurrentPrice : r.ChangePct;
        if (Math.Abs(change) > ConfirmationChangePct)
            hits.Add(new("change-over-20", Severity.Warn, $"Changes {Pct(change)} against today's price {Money(r.CurrentPrice)}."));
        // A GjirafaMall price synced from Gjirafa50 is Gjirafa50's price verbatim, on Gjirafa50's grid by design (on run
        // #1104 that was 49,456 of 49,466 off-grid prices, all held back until this exemption).
        if (r.RoundingEnabled && r.Rounding != RoundingConvention.None && !RoundingService.IsOnGrid(p, r.Rounding)
            && !synced && !DeliberatelyUnrounded(r, p, ceiling))
            hits.Add(new("off-charm", Severity.Warn, $"{Money(p)} is not on the layer's {r.Rounding} grid."));
        if (r.PreviousCost is { } prev and > 0 && r.Cost is { } now && Math.Abs(100m * (now - prev) / prev) > CostMovePct)
            hits.Add(new("cost-moved", Severity.Warn, $"Cost moved {Pct(100m * (now - prev) / prev)} since the previous run ({Money(prev)} to {Money(now)})."));
        if (r.Gj50Price is { } g && Math.Abs(g - p) > 0.005m)
            hits.Add(new("mall-gj50-disagree", Severity.Warn, $"Gjirafa50 prices this SKU at {Money(g)}, GjirafaMall at {Money(p)}."));
        return hits;
    }

    /// <summary>The engine leaves a price off the grid on purpose when the dead-stock hard floor clamps it, when
    /// rounding would cross a bound, or when it holds the price at the ceiling (an off-grid anchor: rounding up
    /// would price above it). On run #1098 that was 74 of 82 off-grid prices; flagging them held good prices back.</summary>
    private static bool DeliberatelyUnrounded(CheckInput r, decimal price, decimal ceiling)
    {
        var codes = r.ReasonCodes + "," + r.GuardrailFlags;
        return codes.Contains(GuardrailFlags.DeadStockHardFloorClamped, StringComparison.Ordinal)
            || codes.Contains(GuardrailFlags.RoundingSkippedOutOfBounds, StringComparison.Ordinal)
            || (ceiling > 0 && price == ceiling);
    }

    public static string Worst(IEnumerable<RuleHit> hits) =>
        hits.Select(h => h.Severity).DefaultIfEmpty(Severity.Pass).MaxBy(Severity.Rank)!;

    private static string Money(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Pct(decimal v) => v.ToString("0.0", CultureInfo.InvariantCulture) + " %";
}
