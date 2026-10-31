using PricingTool.Agent.Operations;
using PricingTool.Agent.Storage;

namespace PricingTool.Tests;

/// <summary>The goals set in the chat: their period, what code refuses, and how progress is measured.</summary>
public class GoalsTests
{
    [Fact]
    public void A_month_runs_from_its_first_day_and_a_week_from_monday()
    {
        Assert.Equal((new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)), Goals.PeriodOf(GoalKinds.Month, new DateOnly(2026, 11, 17)));
        Assert.Equal((new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 1)), Goals.PeriodOf(GoalKinds.Week, new DateOnly(2026, 11, 1)));   // a Sunday
        Assert.Equal((new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 8)), Goals.PeriodOf(GoalKinds.Week, new DateOnly(2026, 11, 2)));    // a Monday
    }

    [Theory]
    [InlineData(1, "month", "gross-profit", 180000, "2026-10-31", null)]
    [InlineData(2, "month", "gross-profit", 180000, "2026-10-31", "Layer 2 isn't one the agents work on.")]
    [InlineData(1, "quarter", "gross-profit", 180000, "2026-10-31", "'quarter' isn't a period: month or week.")]
    [InlineData(1, "month", "margin", 120, "2026-10-31", "120 isn't a margin in percent.")]
    [InlineData(1, "month", "gross-profit", -5, "2026-10-31", "-5 isn't a gross profit in euros.")]
    [InlineData(1, "month", "units", 500, "2026-10-31", "'units' isn't a kind of goal: gross-profit or margin.")]
    [InlineData(1, "week", "margin", 12, "2026-10-20", "That week ended on 2026-10-25.")]
    public void Code_refuses_what_isnt_a_goal(int layer, string period, string kind, decimal value, string start, string? problem) =>
        Assert.Equal(problem, Goals.Problem(layer, period, kind, value, DateOnly.Parse(start), new DateOnly(2026, 10, 31)));

    [Fact]
    public void Progress_is_the_share_made_so_far_and_the_period_at_the_same_pace()
    {
        var goal = new GoalRecord { LayerId = 1, Period = GoalKinds.Month, Start = new DateOnly(2026, 11, 1), End = new DateOnly(2026, 11, 30), Kind = GoalKinds.GrossProfit, Value = 180000m };

        var s = Goals.Evaluate(goal, 60000m, 300000m, new DateTime(2026, 11, 10, 12, 0, 0));       // 9.5 of 30 days

        Assert.Equal(9.5m / 30m, s.ElapsedShare);
        Assert.Equal(Math.Round(60000m / (9.5m / 30m), 0), s.Projected);
        Assert.Equal(60000m / 180000m, s.ShareOfGoal);
        Assert.True(s.OnTrack);                                                                      // 189,474 at this pace
        Assert.Equal(20.0m, s.MarginPct);

        var margin = new GoalRecord { LayerId = 1, Period = GoalKinds.Month, Start = goal.Start, End = goal.End, Kind = GoalKinds.Margin, Value = 22m };
        Assert.False(Goals.Evaluate(margin, 60000m, 300000m, new DateTime(2026, 11, 10)).OnTrack);   // 20 % against 22 %
        Assert.Null(Goals.Evaluate(goal, 1000m, 5000m, new DateTime(2026, 11, 1, 6, 0, 0)).Projected); // too early to project
    }
}
