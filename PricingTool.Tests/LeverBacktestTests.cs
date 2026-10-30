using PricingTool.Agent.Backtest;
using PricingTool.Agent.Campaigns;
using PricingTool.Agent.Tools;

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

    [Fact]
    public void Cadence_prices_the_days_a_computed_price_waited_and_learning_undoes_a_raise()
    {
        var lines = new List<BacktestLine>
        {
            L("D", 2, 1, 10m, 6m),                                                                 // live at 12 gross, the engine had 13.20 ready
            L("D2", 2, 1, 12.5m, 6m),                                                              // sold at 15: not the old price, not counted
            L("E", 3, 2, 10m, 6m, category: "Shtëpi"),                                             // a raise 10 → 12 undone
            L("E", 4, 1, 10m, 6m, category: "Kozmetikë"),                                          // another category: kept
        };
        var trace = new List<(int Lever, BacktestLine Line, decimal Gain)>();
        LeverBacktest.Run(1, lines, From, To, (_, _) => null, (_, _) => null, trace,
            (sku, day) => sku is "D" or "D2" && day == From.AddDays(2) ? (12m, 13.2m) : null,
            (sku, category, _) => sku == "E" && category == "Shtëpi" ? (10m, 12m) : null);

        var five = Assert.Single(trace, t => t.Lever == 5);
        Assert.Equal("D", five.Line.Sku);
        Assert.Equal(Math.Round(LeverBacktest.Gp(five.Line, 13.2m, -1m) - five.Line.Gp, 6), Math.Round(five.Gain, 6));
        var six = Assert.Single(trace, t => t.Lever == 6);
        Assert.Equal(Math.Round(LeverBacktest.Gp(six.Line, 10m, -1m) - six.Line.Gp, 6), Math.Round(six.Gain, 6));
        Assert.True(six.Gain < 0);                                                                  // undoing a raise on a 40 % margin costs GP at e -1
    }

    [Fact]
    public void A_campaign_replay_moves_depth_between_skus_but_keeps_the_campaign_as_deep_on_average()
    {
        var w = new CampaignWindow(1, "Për ty, Oct 2025", new DateOnly(2025, 10, 8), new DateOnly(2025, 10, 12), "main");
        var depth = new DepthEffect(3m, 4, 20, 25m, 0m, new Dictionary<string, decimal>(), new Dictionary<string, decimal>(), "test");
        var rows = new[]
        {
            // Normal price 100 for both; THIN costs 85 and sold 10 at 20 % off (80, a loss); FAT costs 40 and sold 10 at 0 % off.
            new ReplaySkuRow(1, "THIN", 10m, 1000m, 150m, 10m, 800m, -50m),
            new ReplaySkuRow(1, "FAT", 10m, 1000m, 600m, 10m, 1000m, 600m),
            new ReplaySkuRow(1, "TAIL", 0m, 0m, 0m, 5m, 500m, 100m),                              // no normal-day sales: kept as it was
        };
        var r = CampaignDepthBacktest.Score(w, rows, depth, "reallocate");

        Assert.Equal((650m, 2), (r.ActualGp, r.SkusScored));
        Assert.Equal(8.9m, r.ActualDepthPct);                                                     // (20 × 800 + 0 × 1000) / 1800: weighted by campaign revenue
        Assert.True(r.PlannedDepthPct >= r.ActualDepthPct);                                       // as deep on average as it was
        Assert.True(r.Gain > 0);                                                                  // the depth moved off the loss-making SKU
        var none = CampaignDepthBacktest.Score(w, rows, depth with { B = 0m }, "reallocate");
        Assert.True(none.PlannedDepthPct >= none.ActualDepthPct);
    }

    [Fact]
    public void The_per_ty_reproduction_passes_only_within_both_bounds()
    {
        Assert.True(PerTyReproduction.Score(15.1m, 20.1m).Passed);                                  // the hand plan itself: 15.1 %, 75 % of normal
        var nearMiss = PerTyReproduction.Score(14.9m, 21.6m);                                        // 30 Oct run: margin in, 69 % kept
        Assert.Equal((true, false, 69m), (nearMiss.MarginWithin, nearMiss.KeptWithin, nearMiss.KeptPct));
        Assert.False(nearMiss.Passed);
        Assert.False(PerTyReproduction.Score(19.0m, 21.6m).Passed);                                  // the first run: no strategy spent the floor
        var normal = PerTyReproduction.NormalMarginPct(new[]
        {
            new CampaignCandidate("A", null, "X", null, 100m, 100m, 80m, 1m, 1, 0, false, false),   // 20 % on 100 a day
            new CampaignCandidate("B", null, "X", null, 50m, 50m, 30m, 2m, 1, 0, false, false),     // 40 % on 100 a day
        });
        Assert.Equal(30.0m, normal);
    }
}
