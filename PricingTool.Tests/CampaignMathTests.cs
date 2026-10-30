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
