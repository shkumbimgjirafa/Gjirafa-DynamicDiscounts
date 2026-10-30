using PricingTool.Agent;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Tools;

namespace PricingTool.Tests;

/// <summary>
/// Alpha's scorecard arithmetic: the window (30 days ending 14 days ago, same weeks last year), campaign
/// days kept out of the normal-day figures, shares taken from summed money, thin data, stock and cost
/// moves merged onto the same band × category, and the prompt table.
/// </summary>
public class AlphaScorecardTests
{
    private static readonly LayerInfo Gj50 = new(4, "Gjirafa50 — Kosovo", "Gjirafa50", "EUR", 18m);
    private static readonly LayerInfo Mall = new(1, "GjirafaMall — Kosovo", "GjirafaMall", "EUR", 18m);

    private static readonly BandConfigRow[] Bands =
    {
        new(26, "10–50", 10m, 50m, 13m, 23m, 1),
        new(25, "0–10", 0m, 10m, 15m, 33m, 0),
    };

    private static SalesAggregate Sales(int? band, string cat, int period, bool campaign, decimal units, decimal net, decimal gp,
        decimal below = 0, decimal at = 0, decimal xdock = 0, int skus = 1, int skusElastic = 0, decimal netElastic = 0) =>
        new(band, cat, period, campaign, units, net, gp, below, at, xdock, skus, skusElastic, netElastic);

    [Fact]
    public void The_window_is_thirty_days_ending_two_weeks_ago_and_last_year_keeps_the_weekdays()
    {
        var w = DateWindow.Ending(new DateOnly(2026, 10, 30), days: 30, lagDays: 14);
        var ly = w.SameWeeksLastYear();

        Assert.Equal(new DateOnly(2026, 9, 16), w.From);
        Assert.Equal(new DateOnly(2026, 10, 16), w.To);                     // exclusive: the last day is 15 Oct
        Assert.Equal("2026-09-16 to 2026-10-15", w.ToString());
        Assert.Equal(30, w.Days);
        Assert.Equal(new DateOnly(2025, 9, 17), ly.From);
        Assert.Equal(w.From.DayOfWeek, ly.From.DayOfWeek);
    }

    [Fact]
    public void Normal_day_figures_leave_campaign_days_out_and_shares_come_from_summed_money()
    {
        var sales = new[]
        {
            Sales(26, "Aksesorë", 0, false, units: 40, net: 1000, gp: 250, below: 100, at: 50, xdock: 600, skus: 12, skusElastic: 3, netElastic: 400),
            Sales(26, "Aksesorë", 0, true, units: 90, net: 3000, gp: 150),       // campaign days: excluded
            Sales(26, "Aksesorë", 1, false, units: 50, net: 1200, gp: 200),
            Sales(26, "Aksesorë", 1, true, units: 70, net: 2500, gp: 100),
        };

        var card = Build(sales);

        var row = Assert.Single(card.Rows);
        Assert.Equal((26, "10–50", "Aksesorë"), (row.BandId!.Value, row.BandName, row.Category));
        Assert.Equal((13m, 23m), (row.FloorPct!.Value, row.TargetPct!.Value));
        Assert.Equal((40m, 1000m, 250m, 25m), (row.Units, row.NetRevenue, row.GrossProfit, row.MarginPct!.Value));
        Assert.Equal((50m, 1200m), (row.LyUnits, row.LyNetRevenue));
        Assert.Equal(16.7m, Math.Round(row.LyMarginPct!.Value, 1));
        Assert.Equal((10m, 5m, 60m), (row.BelowFloorPct!.Value, row.AtFloorPct!.Value, row.CrossDockPct!.Value));
        Assert.Equal((3000m, 2500m), (row.CampaignNetRevenue, row.LyCampaignNetRevenue));
        Assert.Equal((12, 3, 40m), (row.SkusSold, row.SkusWithElasticity, row.ElasticityCoveragePct!.Value));
        Assert.False(row.ThinData);
    }

    [Fact]
    public void Few_normal_day_units_are_thin_data_even_with_a_big_campaign()
    {
        var card = Build(new[]
        {
            Sales(25, "Gaming", 0, false, units: 29, net: 300, gp: 90),
            Sales(25, "Gaming", 0, true, units: 500, net: 9000, gp: 500),
        });

        Assert.True(Assert.Single(card.Rows).ThinData);                      // threshold 30
    }

    [Fact]
    public void Stock_and_cost_moves_land_on_the_same_band_and_category_and_rows_follow_band_order()
    {
        var card = Build(
            new[]
            {
                Sales(26, "Aksesorë", 0, false, 100, 2000, 400),
                Sales(25, "Aksesorë", 0, false, 100, 500, 150),
            },
            stock: new[]
            {
                new StockAggregate(26, "Aksesorë", 30, StockValue: 10_000, DeadStockValue: 4_000, MaxDeadObservedDays: 200),
                new StockAggregate(26, "SMART", 5, StockValue: 1_000, DeadStockValue: 1_000, MaxDeadObservedDays: 400),   // stock, no sales
            },
            costs: new[]
            {
                new CostChangeRow("A", 26, "Aksesorë", 10, 11, 0.10m),
                new CostChangeRow("B", 26, "Aksesorë", 10, 9, -0.10m),
                new CostChangeRow("C", 26, "Aksesorë", 10, 10.2m, 0.02m),       // under 5%: not counted
            });

        Assert.Equal(new[] { (25, "Aksesorë"), (26, "Aksesorë"), (26, "SMART") },
            card.Rows.Select(r => (r.BandId!.Value, r.Category)));            // band 25 sorts first
        var acc = card.Rows[1];
        Assert.Equal((10_000m, 40m, 1, 1), (acc.StockValue, acc.DeadStockPct!.Value, acc.SkusCostUp5, acc.SkusCostDown5));
        var smart = card.Rows[2];
        Assert.Equal((0m, 100m, true), (smart.Units, smart.DeadStockPct!.Value, smart.ThinData));
        Assert.Equal((200m, 2500m, 11_000m), (card.Total.Units, card.Total.NetRevenue, card.Total.StockValue));
    }

    [Fact]
    public void Lines_without_a_band_have_no_floor_shares_but_the_total_does()
    {
        var card = Build(new[]
        {
            Sales(null, "Aksesorë", 0, false, 40, 1000, 100, below: 0),
            Sales(26, "Aksesorë", 0, false, 40, 1000, 100, below: 500),
        });

        var noBand = card.Rows.Single(r => r.BandId is null);
        Assert.Equal("no band", noBand.BandName);
        Assert.Null(noBand.BelowFloorPct);
        Assert.Equal(25m, card.Total.BelowFloorPct);                         // 500 of 2000
    }

    [Fact]
    public void The_prompt_table_has_the_window_the_excluded_campaigns_a_header_and_one_line_per_row()
    {
        var campaigns = new[] { new CampaignWindow(10, "Për ty, Oct 2026", new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 18), "main") };
        var card = Build(new[] { Sales(26, "Teknologji", 0, false, 120, 5000.4m, 1000) }, layer: Mall, campaigns: campaigns,
            run: new EngineRunRef(1099, new DateOnly(2026, 10, 30)));

        var text = card.ToPromptTable();

        Assert.Contains("GjirafaMall — Kosovo (layer 1), category level MainGroup", text);
        Assert.Contains("Window: 2026-09-16 to 2026-10-15 (30 days)", text);
        Assert.Contains("Për ty, Oct 2026 (2026-10-14 to 2026-10-18)", text);
        Assert.Contains("engine run #1099", text);
        Assert.Contains("band_id|band|category|floor|target|units|net|gp|margin|", text);
        Assert.Contains("26|10–50|Teknologji|13.0|23.0|120|5000|1000|20.0|0|0|-|", text);
        Assert.Contains("-|all bands|all|", text);
    }

    private static AlphaScorecard Build(IReadOnlyList<SalesAggregate> sales, IReadOnlyList<StockAggregate>? stock = null,
        IReadOnlyList<CostChangeRow>? costs = null, LayerInfo? layer = null, IReadOnlyList<CampaignWindow>? campaigns = null,
        EngineRunRef? run = null)
    {
        var w = DateWindow.Ending(new DateOnly(2026, 10, 30), 30, 14);
        return AlphaScorecard.Build(layer ?? Gj50, w, w.SameWeeksLastYear(), new ScorecardOptions(), Bands, sales,
            stock ?? Array.Empty<StockAggregate>(), costs ?? Array.Empty<CostChangeRow>(),
            campaigns ?? Array.Empty<CampaignWindow>(), run);
    }
}
