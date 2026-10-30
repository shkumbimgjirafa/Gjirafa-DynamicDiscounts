using PricingTool.Agent.Checker;
using PricingTool.Core.Domain;

namespace PricingTool.Tests;

/// <summary>The run checker's rules, one by one, on a Gjirafa50 KS-shaped price (band floor 13 %, .50 grid).</summary>
public class RunCheckRulesTests
{
    private static CheckInput Row(decimal price, decimal current = 52.50m, decimal old = 60m, decimal anchor = 60m, decimal? cost = 30m,
        DeadStockStage dead = DeadStockStage.None, string reasons = "", decimal? floor = 13m) =>
        new(1, "SKU", 26, "10–50", RoundingConvention.Gj50Charm, true, floor, price, current, old, anchor, cost,
            current > 0 ? 100m * (price - current) / current : 0m, dead, reasons, "");

    private static string[] Codes(CheckInput r) => RunCheckRules.Evaluate(r).Select(h => h.Code).ToArray();

    [Fact]
    public void A_clean_price_on_the_grid_raises_nothing() => Assert.Empty(Codes(Row(49.50m)));

    [Fact]
    public void Below_cost_outside_dead_stock_blocks()
    {
        var hits = RunCheckRules.Evaluate(Row(24.50m, current: 26.50m, cost: 25m));
        Assert.Contains(hits, h => h is { Code: "below-cost", Severity: Severity.Block });
        Assert.Equal(Severity.Block, RunCheckRules.Worst(hits));
    }

    [Fact]
    public void Dead_stock_may_go_below_cost_down_to_its_40_percent_hard_floor()
    {
        Assert.DoesNotContain("below-cost", Codes(Row(12.50m, current: 14.50m, cost: 25m, dead: DeadStockStage.Stage3)));
        Assert.DoesNotContain("dead-stock-below-hard-floor", Codes(Row(9.99m, current: 10.50m, cost: 25m, dead: DeadStockStage.Stage4)));   // 39.96 %: rounding
        Assert.Contains("dead-stock-below-hard-floor", Codes(Row(8.50m, current: 10.50m, cost: 25m, dead: DeadStockStage.Stage4)));
    }

    [Fact]
    public void A_tenfold_jump_and_a_cost_at_the_wrong_scale_block()
    {
        Assert.Contains("x10-jump", Codes(Row(525m, current: 52.50m, old: 600m, anchor: 600m)));
        var cost62 = RunCheckRules.Evaluate(Row(49.50m, cost: 30m * 62));
        Assert.Contains(cost62, h => h is { Code: "cost-scale", Severity: Severity.Block });
    }

    [Fact]
    public void Above_max_of_anchor_and_current_blocks_unless_gjirafa50_set_the_price()
    {
        Assert.Contains("above-ceiling", Codes(Row(64.50m)));
        Assert.DoesNotContain("above-ceiling", Codes(Row(64.50m, reasons: "SYNCED_TO_GJ50")));
    }

    [Fact]
    public void Below_the_floor_warns_and_near_the_floor_is_only_information()
    {
        var below = RunCheckRules.Evaluate(Row(33.50m, current: 34.50m, cost: 30m));      // 10.4 % < 13 %
        Assert.Contains(below, h => h is { Code: "below-floor", Severity: Severity.Warn });
        var near = RunCheckRules.Evaluate(Row(34.50m, current: 35.50m, cost: 30m) with { RoundingEnabled = false });   // 13.0 %, floor rule alone
        Assert.Contains(near, h => h is { Code: "near-floor", Severity: Severity.Info });
        Assert.Equal(Severity.Info, RunCheckRules.Worst(near));
    }

    [Fact]
    public void Changes_over_20_percent_off_grid_prices_cost_moves_and_mall_gj50_gaps_warn()
    {
        Assert.Contains("change-over-20", Codes(Row(39.50m, current: 52.50m)));
        Assert.Contains("off-charm", Codes(Row(49.37m)));
        Assert.Contains("cost-moved", Codes(Row(49.50m) with { PreviousCost = 27m }));
        Assert.DoesNotContain("cost-moved", Codes(Row(49.50m) with { PreviousCost = 29.5m }));
        Assert.Contains("mall-gj50-disagree", Codes(Row(49.50m) with { Gj50Price = 47.50m }));
    }

    [Fact]
    public void A_proposed_fix_is_judged_by_the_same_rules()
    {
        var row = Row(49.37m);
        Assert.Contains("off-charm", RunCheckRules.Evaluate(row).Select(h => h.Code));
        Assert.Empty(RunCheckRules.Evaluate(row, price: 49.50m));
        Assert.Contains("below-cost", RunCheckRules.Evaluate(row, price: 29.50m).Select(h => h.Code));
    }
}
