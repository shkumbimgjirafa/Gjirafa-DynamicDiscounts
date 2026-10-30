using PricingTool.Agent;
using PricingTool.Agent.Alpha;

namespace PricingTool.Tests;

/// <summary>Each limit on a floor/target decision, as code enforces it whatever the model asked for.</summary>
public class SafetyLimitsTests
{
    private static readonly SafetyLimitOptions Limits = new()
    {
        MaxStepPpPerWeek = 1.0m,
        HardMinFloorPct = new() { ["4"] = 4m },
    };

    /// <summary>Band 10–50 on Gjirafa50 KS today: floor 13 %, target 23 %, unchanged this week.</summary>
    private static LimitInput Ask(string action, decimal floor, decimal target, bool thin = false, bool holdout = false,
        decimal currentFloor = 13m, decimal currentTarget = 23m, decimal? floorWeekAgo = null, decimal? targetWeekAgo = null) =>
        new(4, "Aksesorë", thin, holdout, currentFloor, currentTarget, floorWeekAgo ?? currentFloor, targetWeekAgo ?? currentTarget,
            action, floor, target);

    [Fact]
    public void A_one_point_raise_is_applied()
    {
        var v = SafetyLimits.Evaluate(Ask("raise", 14m, 24m), Limits);
        Assert.Equal((DecisionStatus.Applied, "raise", 14m, 24m, (string?)null), (v.Status, v.Action, v.Floor, v.Target, v.Limit));
    }

    [Fact]
    public void Five_points_is_held_for_a_person_with_the_limit_named()
    {
        var v = SafetyLimits.Evaluate(Ask("raise", 18m, 28m), Limits);
        Assert.Equal((DecisionStatus.Held, "max-step"), (v.Status, v.Limit));
        Assert.Contains("5 pt", v.Detail);
    }

    [Fact]
    public void The_step_counts_from_last_weeks_value_so_two_small_moves_cannot_add_up()
    {
        // Raised 13 -> 14 three days ago; another +0.5 would be 1.5 pt in one week.
        var v = SafetyLimits.Evaluate(Ask("raise", 14.5m, 24m, currentFloor: 14m, floorWeekAgo: 13m), Limits);
        Assert.Equal((DecisionStatus.Held, "max-step"), (v.Status, v.Limit));
    }

    [Fact]
    public void The_target_also_moves_one_point_at_most()
    {
        var v = SafetyLimits.Evaluate(Ask("raise", 13m, 25m), Limits);
        Assert.Equal(DecisionStatus.Held, v.Status);
    }

    [Fact]
    public void Lowering_to_just_above_the_hard_minimum_is_allowed()
    {
        var v = SafetyLimits.Evaluate(Ask("lower", 4.5m, 9m, currentFloor: 5m, currentTarget: 9.5m), Limits);
        Assert.Equal((DecisionStatus.Applied, "lower"), (v.Status, v.Action));
    }

    [Fact]
    public void A_floor_below_the_layers_hard_minimum_is_rejected()
    {
        var v = SafetyLimits.Evaluate(Ask("lower", 3.5m, 8m, currentFloor: 4.2m, currentTarget: 8.5m), Limits);
        Assert.Equal((DecisionStatus.Rejected, "hard-minimum"), (v.Status, v.Limit));
    }

    [Fact]
    public void A_target_below_the_floor_is_rejected()
    {
        var v = SafetyLimits.Evaluate(Ask("raise", 14m, 13.5m, currentTarget: 13m), Limits);
        Assert.Equal((DecisionStatus.Rejected, "target-below-floor"), (v.Status, v.Limit));
    }

    [Theory]
    [InlineData(0, 23)]
    [InlineData(70, 75)]
    [InlineData(13, 95)]
    [InlineData(-2, 23)]
    public void Out_of_range_values_are_rejected(double floor, double target)
    {
        var v = SafetyLimits.Evaluate(Ask("raise", (decimal)floor, (decimal)target), Limits);
        Assert.Equal((DecisionStatus.Rejected, "out-of-range"), (v.Status, v.Limit));
    }

    [Theory]
    [InlineData("raise", 12.5, 23)]
    [InlineData("lower", 13.5, 23)]
    public void An_action_that_disagrees_with_its_values_is_rejected_as_malformed(string action, double floor, double target)
    {
        var v = SafetyLimits.Evaluate(Ask(action, (decimal)floor, (decimal)target), Limits);
        Assert.Equal((DecisionStatus.Rejected, "malformed"), (v.Status, v.Limit));
    }

    [Fact]
    public void An_unknown_action_is_rejected()
    {
        Assert.Equal("malformed", SafetyLimits.Evaluate(Ask("double", 26m, 46m), Limits).Limit);
    }

    [Fact]
    public void Thin_data_is_kept_by_code_whatever_was_asked()
    {
        var v = SafetyLimits.Evaluate(Ask("raise", 14m, 24m, thin: true), Limits);
        Assert.Equal((DecisionStatus.Applied, "keep", 13m, 23m, "thin-data"), (v.Status, v.Action, v.Floor, v.Target, v.Limit));
        Assert.Contains("raise", v.Detail);
    }

    [Fact]
    public void A_holdout_category_is_never_touched()
    {
        var v = SafetyLimits.Evaluate(Ask("lower", 12m, 22m, holdout: true), Limits);
        Assert.Equal((DecisionStatus.Applied, "keep", 13m, 23m, "holdout"), (v.Status, v.Action, v.Floor, v.Target, v.Limit));
    }

    [Fact]
    public void Keep_and_unchanged_values_change_nothing()
    {
        Assert.Equal(("keep", 13m, 23m), Tuple(SafetyLimits.Evaluate(Ask("keep", 20m, 30m), Limits)));
        Assert.Equal(("keep", 13m, 23m), Tuple(SafetyLimits.Evaluate(Ask("raise", 13m, 23m), Limits)));
    }

    [Fact]
    public void A_layer_without_its_own_minimum_uses_the_default()
    {
        var o = new SafetyLimitOptions { DefaultHardMinFloorPct = 5m };
        Assert.Equal(5m, o.HardMinFloorFor(1));
        Assert.Equal(4m, Limits.HardMinFloorFor(4));
    }

    private static (string, decimal, decimal) Tuple(LimitVerdict v) => (v.Action, v.Floor, v.Target);
}
