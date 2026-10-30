namespace PricingTool.Agent.Backtest;

/// <summary>One MK/AL layer-month: its tool-scope gross profit in EUR and whether a regular-price push went live that month.</summary>
public sealed record CoverageMonth(int LayerId, string Month, decimal GrossProfitEur, bool Pushed);

/// <summary>The calculator's measured Kosovo effect as a share of Kosovo's gross profit, one case per measurement.</summary>
public sealed record CoverageEffect(string Case, decimal GainPerMonth, decimal KsGpPerMonth, string Source)
{
    public decimal Ratio => KsGpPerMonth > 0 ? GainPerMonth / KsGpPerMonth : 0m;
}

/// <summary>
/// Backtest lever 4 (plan section 5): v2 publishes on all six layers, the calculator's tested effect was measured in Kosovo only.
/// For each MK/AL layer-month without a regular-price push of its own (a public, all-platform discount set of at least
/// <see cref="PushMinDiscounts"/> products starting that month), the gain is the Kosovo effect's share of gross profit times that
/// layer-month's tool-scope gross profit (EUR at the shop's rate). Estimated: the Kosovo effect is assumed to carry over.
/// </summary>
public static class CoverageBacktest
{
    public const int PushMinDiscounts = 50_000;

    public static IReadOnlyList<(string Month, decimal Base, decimal Gain)> Gains(IEnumerable<CoverageMonth> months, CoverageEffect effect) =>
        months.Where(m => !m.Pushed).GroupBy(m => m.Month).OrderBy(g => g.Key)
            .Select(g => (g.Key, Math.Round(g.Sum(m => m.GrossProfitEur), 0), Math.Round(g.Sum(m => m.GrossProfitEur) * effect.Ratio, 0))).ToList();
}
