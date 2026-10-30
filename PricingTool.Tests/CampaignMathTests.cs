using PricingTool.Agent.Campaigns;
using PricingTool.Agent.Storage;
using PricingTool.Agent.Tools;

namespace PricingTool.Tests;

/// <summary>The campaign arithmetic behind get_campaign_history and the planner: uplift against the weeks before, the season
/// factor, discount depth, and the uplift curve.</summary>
public class CampaignMathTests
{
    // units, net, gp, discount, pre-discount gross (VAT incl.), days, lines, discounted lines
    private static SegmentSales S(decimal units, int days, decimal discount = 0, decimal preDiscount = 0, int lines = 100, int discounted = 90) =>
        new(units, units * 10, units * 2, discount, preDiscount, days, lines, discounted);

    [Fact]
    public void Uplift_is_units_per_day_against_the_weeks_before_divided_by_last_years_same_ratio()
    {
        var e = CampaignMath.Read(new CampaignSegments(
            Campaign: S(400, 4, discount: 400, preDiscount: 1000), Before: S(560, 28, discount: 200, preDiscount: 1000),
            CampaignLastYear: S(150, 4), BeforeLastYear: S(700, 28)));

        Assert.Equal(5m, e.RawUplift);                     // 100/day vs 20/day
        Assert.Equal(1.5m, e.SeasonFactor);                // 37.5/day vs 25/day a year earlier
        Assert.Equal(3.33m, Math.Round(e.Uplift!.Value, 2));
        Assert.Equal((20m, 40m, 20m), (e.DepthBeforePct!.Value, e.DepthCampaignPct!.Value, e.ExtraDepthPp!.Value));
    }

    [Fact]
    public void Without_a_usable_year_ago_window_the_raw_uplift_stands_and_without_a_before_window_there_is_none()
    {
        var noSeason = CampaignMath.Read(new CampaignSegments(S(400, 4), S(560, 28), S(150, 0), S(700, 0)));   // 0 days = not usable
        Assert.Equal((5m, (decimal?)null, 5m), (noSeason.RawUplift!.Value, noSeason.SeasonFactor, noSeason.Uplift!.Value));

        var noBefore = CampaignMath.Read(new CampaignSegments(S(400, 4), S(0, 0), S(150, 4), S(700, 28)));
        Assert.Null(noBefore.Uplift);
    }

    [Fact]
    public void Depth_is_unknown_where_the_lines_dont_record_discounts()
    {
        // GjirafaMall from Nov 2025: about 5 % of lines carry a discount.
        var e = CampaignMath.Read(new CampaignSegments(S(400, 4, 50, 1000, lines: 100, discounted: 5), S(560, 28, 20, 1000, lines: 100, discounted: 6),
            S(0, 0), S(0, 0)));
        Assert.Equal(((decimal?)null, (decimal?)null), (e.DepthCampaignPct, e.ExtraDepthPp));
        Assert.Equal(10m, new WeekSales(0, false, 1, 1, 1, 10, 100, Lines: 10, DiscountedLines: 9).DepthPct);
        Assert.Null(new WeekSales(0, false, 1, 1, 1, 10, 100, Lines: 100, DiscountedLines: 5).DepthPct);
    }

    private static CampaignEffect Effect(decimal extraPp, decimal uplift, bool thin = false) =>
        new(10, 10 * uplift, uplift, null, uplift, 20, 20 + extraPp, extraPp, 15, 10, 100, 150, thin);

    [Fact]
    public void The_curve_goes_through_no_extra_depth_no_extra_units_and_is_not_read_beyond_the_deepest_campaign()
    {
        // ln u = b d with b = ln 2 / 0.10: 10 pt doubles units, 20 pt quadruples them.
        var b = Math.Log(2) / 0.10;
        var points = new[] { Effect(10, 2m), Effect(20, 4m), Effect(5, (decimal)Math.Exp(b * 0.05)) };
        var curve = CampaignMath.Fit(points.Append(Effect(30, 50m, thin: true)).Append(Effect(-3, 2m)), "test");   // thin and shallower left out

        Assert.Equal(3, curve.Points);
        Assert.Equal(6.9315m, curve.B);
        Assert.Equal(2m, Math.Round(curve.UpliftAt(10)!.Value, 2));
        Assert.Equal(20m, curve.MaxDepthPct);
        Assert.Equal(4m, Math.Round(curve.UpliftAt(25)!.Value, 2));          // capped at the deepest campaign seen
        Assert.Equal(5, curve.Grid.Count);

        Assert.Null(CampaignMath.Fit(points.Take(2), "too few").B);
    }

    [Fact]
    public void Within_campaign_the_depth_slope_is_told_apart_from_the_campaigns_own_lift()
    {
        // True model: units multiplier = event lift x exp(3 x extra depth). Three campaigns with big, different event lifts;
        // two categories in each, at different depths.
        const double trueB = 3;
        CampaignPoint P(string campaign, string type, double eventLift, decimal extraPp) =>
            new(campaign, type, Effect(extraPp, (decimal)(eventLift * Math.Exp(trueB * (double)extraPp / 100))));
        var points = new[]
        {
            P("bf24", "Black Friday", 5, 10), P("bf24", "Black Friday", 5, 20),
            P("pt25", "Për ty", 2, 5), P("pt25", "Për ty", 2, 15),
            P("bf25", "Black Friday", 3, 10), P("bf25", "Black Friday", 3, 25),
        };

        var throughOrigin = CampaignMath.Fit(points.Select(p => p.Effect), "event + depth");
        Assert.True(throughOrigin.B > 8m);                                   // the event lift lands in the slope

        var within = CampaignMath.FitWithinCampaign(points, "test");
        Assert.Equal(3m, within.B);                                          // the true slope
        Assert.Equal((3, 6), (within.Campaigns, within.Points));
        Assert.Equal(5m, within.EventLiftByCampaign["bf24"]);
        Assert.Equal(4m, within.EventLift("Black Friday"));                  // median of 5 and 3
        Assert.Equal(2m, within.EventLift("Për ty"));
        Assert.Equal(1.82m, Math.Round(within.DepthMultiplier(20)!.Value, 2)); // exp(0.6)
        Assert.Equal(within.DepthMultiplier(25), within.DepthMultiplier(40)); // never beyond the deepest point seen

        // A campaign with one category doesn't count; too few left gives no slope.
        Assert.Null(CampaignMath.FitWithinCampaign(points.Take(4).Append(P("x", "x", 2, 10)), "too few").B);
    }

    [Theory]
    [InlineData("Për ty, Oct 2025", "Për ty")]
    [InlineData("50 Orë Magjike 2026 (Mall)", "50 Orë Magjike")]
    [InlineData("Black Friday 2026", "Black Friday")]
    [InlineData("Valentine's 2026", "Valentine's")]
    public void A_campaign_type_is_its_name_without_year_month_or_note(string name, string type) =>
        Assert.Equal(type, CampaignMath.TypeOf(name));

    [Fact]
    public void A_calendar_entry_moves_from_planned_to_live_to_done_with_its_dates()
    {
        var start = new DateOnly(2026, 11, 27);
        var end = new DateOnly(2026, 11, 30);
        Assert.Equal(CampaignStatus.Planned, CampaignCalendar.StatusOn(start, end, start.AddDays(-1), CampaignStatus.Planned));
        Assert.Equal(CampaignStatus.Live, CampaignCalendar.StatusOn(start, end, start, CampaignStatus.Planned));
        Assert.Equal(CampaignStatus.Live, CampaignCalendar.StatusOn(start, end, end, CampaignStatus.Planned));
        Assert.Equal(CampaignStatus.Done, CampaignCalendar.StatusOn(start, end, end.AddDays(1), CampaignStatus.Live));
    }
}
