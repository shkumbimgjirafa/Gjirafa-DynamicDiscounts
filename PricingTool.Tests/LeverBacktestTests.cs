using PricingTool.Agent.Backtest;

namespace PricingTool.Tests;

/// <summary>
/// Backtest levers 1–3 on hand-made sales lines: a loss the known cost would have caught, a lift to the agent's category floor
/// over the hand-set floor, a cost jump priced to its prior margin from the day after it was seen, each line counted once.
/// </summary>
public class LeverBacktestTests
{
    private static readonly DateOnly From = new(2026, 8, 1), To = new(2026, 9, 1);

    private static BacktestLine L(string sku, int day, decimal qty, decimal unitNet, decimal unitCogs, string stock = "InStock", int? band = null,
        decimal? costInclVat = null, decimal unitDiscount = 0m, string category = "Aksesorë", bool campaign = false) =>
        new(From.AddDays(day), sku, qty, qty * unitNet, qty * unitNet * 1.2m, qty * unitCogs, qty * (unitNet - unitCogs), qty * unitDiscount, stock, costInclVat,
            band, category, campaign);

    [Fact]
    public void Each_lever_counts_its_own_lines_once_walk_forward()
    {
        var lines = new List<BacktestLine>
        {
            // Lever 1: the first loss line comes with a cost nobody knew yet; the second one the known cost would have caught.
            L("A", -10, 1, 12m, 10m, "CrossDock"), L("A", 4, 1, 12m, 13m, "CrossDock"), L("A", 5, 2, 12m, 13m, "CrossDock"),
            // Lever 2: 9.1 % margin on the engine basis, hand floor 8 %, the agent's 12 %: lifted from 110 to 113.64 (ceiling 120).
            L("B", 3, 1, 110m / 1.2m, 70m, band: 1, costInclVat: 100m, unitDiscount: 10m),
            // Lever 3: cost 10 → 11 on day 11 at an unchanged price; day 12 is priced to the prior margin (1/6) = 13.20 net.
            L("C", -1, 1, 15m, 10m),
        };
        lines.AddRange(Enumerable.Range(0, 11).Select(d => L("C", d, 1, 12m, 10m)));
        lines.Add(L("C", 11, 1, 12m, 11m));
        lines.Add(L("C", 12, 3, 12m, 11m));
        lines.Add(L("C", 13, 1, 12m, 11m, campaign: true));                                        // campaign days are lever 7's

        var trace = new List<(int Lever, BacktestLine Line, decimal Gain)>();
        var r = LeverBacktest.Run(4, lines, From, To, (band, _) => band == 1 ? 8m : null, (band, cat) => band == 1 && cat == "Aksesorë" ? 12m : null, trace);

        var one = Assert.Single(trace, t => t.Lever == 1);
        Assert.Equal((From.AddDays(5), 2m), (one.Line.Day, one.Gain));                             // the caught loss: 2 units × 1
        var two = Assert.Single(trace, t => t.Lever == 2);
        var expectedTwo = LeverBacktest.Gp(two.Line, 100m / 0.88m, -1m) - LeverBacktest.Gp(two.Line, 110m, -1m);
        Assert.Equal(Math.Round(expectedTwo, 6), Math.Round(two.Gain, 6));
        Assert.True(two.Gain > 0);
        var three = Assert.Single(trace, t => t.Lever == 3);
        Assert.Equal(From.AddDays(12), three.Line.Day);                                             // not day 11: the jump is seen that day
        var units = 3m * (decimal)Math.Pow(13.2 / 12.0, -1.0);
        Assert.Equal(Math.Round(units * (13.2m - 11m) - 3m * (12m - 11m), 4), Math.Round(three.Gain, 4));

        var context = Assert.Single(r.Context);
        Assert.Equal((3m, 2, 2m), (context.CrossDockLoss, context.CrossDockLossLines, context.CaughtLoss));   // measured: 1 + 2 lost, 2 caught
        Assert.Equal(3, r.Gains.Count(g => g.Lever == 1));                                         // one row per elasticity case
        Assert.True(r.Gains.Single(g => g.Lever == 3 && g.Elasticity == -2.0m).Gain < r.Gains.Single(g => g.Lever == 3 && g.Elasticity == -0.5m).Gain);
    }
}
