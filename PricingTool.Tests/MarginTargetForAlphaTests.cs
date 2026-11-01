using PricingTool.Agent.Alpha;
using PricingTool.Agent.Storage;
using PricingTool.NopIntegration;

namespace PricingTool.Tests;

/// <summary>
/// Alpha's view of a person's margin target and of GjirafaMall's own products, and the price list a Run week leaves
/// (the user's requests, 1 Nov).
/// </summary>
public class MarginTargetForAlphaTests
{
    private static ChatRule Rule(long id, int layer, string value, int from, int to) => new()
    {
        Id = id, LayerId = layer, Kind = ChatRuleKinds.MarginTarget, Value = value, FromDate = new DateOnly(2026, 11, from), ToDate = new DateOnly(2026, 11, to),
        Instruction = "@alpha 1–3 Nov GjirafaMall 19.5%, Gjirafa50 15%", CreatedBy = "demo",
    };

    [Fact]
    public void No_margin_target_adds_nothing_to_the_prompt() =>
        Assert.Equal("", FloorTargetAgent.MarginTargetBlock(Array.Empty<ChatRule>(), null, mall: false));

    [Fact]
    public void A_margin_target_reaches_Alpha_with_where_the_shop_stands()
    {
        var shop = new ShopMarginEstimate(1111, new DateOnly(2026, 9, 20), new DateOnly(2026, 10, 18), 17.9m, 20.4m, 19.0m, 21.6m, 66.2m);
        var block = FloorTargetAgent.MarginTargetBlock(new[] { Rule(1, 1, "19.5", 1, 3) }, shop, mall: true);

        Assert.Contains("2026-11-01 to 2026-11-03: 19.5% (rule #1, set by demo", block);
        Assert.Contains("engine run #1111", block);
        Assert.Contains("2026-09-20 to 2026-10-17", block);                       // the window's last day, not its exclusive end
        Assert.Contains("17.9% at today's shop prices, 20.4% at the run's proposed prices", block);
        Assert.Contains("own products (66.2% of the revenue; the only ones your targets move): 19.0% now, 21.6% at the run's prices", block);
        Assert.Contains("never above a regular price", block);
    }

    [Fact]
    public void Gjirafa50_gets_no_own_products_line()
    {
        var shop = new ShopMarginEstimate(1110, new DateOnly(2026, 9, 20), new DateOnly(2026, 10, 18), 14.0m, 24.8m, 14.0m, 24.8m, 100m);
        Assert.DoesNotContain("own products", FloorTargetAgent.MarginTargetBlock(new[] { Rule(2, 4, "15", 1, 3) }, shop, mall: false));
    }

    [Fact]
    public void The_shop_margin_is_summed_first_then_divided_and_own_leaves_out_what_takes_Gjirafa50s_price()
    {
        var e = ShopMarginEstimate.Of(7, new DateOnly(2026, 9, 20), new DateOnly(2026, 10, 18), new[]
        {
            (false, 600m, 120m, 620m, 140m),   // own: 20.0% now, 22.6% at the run's prices
            (true, 400m, 40m, 400m, 40m),      // takes Gjirafa50's price: 10%
        });
        Assert.Equal((16.0m, 17.6m, 20.0m, 22.6m, 60.0m), (e.NowPct!.Value, e.RunPct!.Value, e.OwnNowPct!.Value, e.OwnRunPct!.Value, e.OwnSharePct!.Value));
    }

    [Fact]
    public void Mall_scope_line_says_what_was_left_out()
    {
        var note = AlphaInputBuilder.OwnOnlyNote(new[] { (false, 900m, 600_000m, 114_000m), (true, 400m, 300_000m, 33_000m) });
        Assert.Contains("own products only", note);
        Assert.Contains("net 300,000 (33.3% of the layer's revenue) at 11.0% margin", note);
        Assert.Contains("the whole layer with them: net 900,000 at 16.3% margin", note);
    }

    [Theory]
    [InlineData(9.50, 9.50, "MARGIN_TARGET_TRIM,CHARM", null, null, "MARGIN_TARGET_TRIM")]
    [InlineData(10.50, 9.50, "MARGIN_TARGET_TRIM", null, null, "MARGIN_TARGET_RULE (engine: MARGIN_TARGET_TRIM 9.50)")]
    [InlineData(9.50, 9.37, "ELASTIC_OPTIMAL", 9.50, null, "RUN_CHECK_FIX (engine: ELASTIC_OPTIMAL 9.37)")]
    [InlineData(9.50, 9.50, "", null, "Aksesorë", "NONE; Alpha's Aksesorë rule: floor 16%, target 34%")]
    public void The_price_list_says_what_set_each_price(decimal price, decimal proposed, string codes, double? fix, string? category, string expected) =>
        Assert.Equal(expected, RunWeekPriceList.Algorithm(price, proposed, codes, (decimal?)fix, category, category is null ? null : 34m, category is null ? null : 16m));

    [Fact]
    public void The_price_list_is_the_columns_asked_for_with_local_dates_and_margin()
    {
        var csv = RunWeekPriceList.ToCsv(new[]
        {
            new PriceListLine("1002483mo", 35.5m, new DateTime(2026, 11, 1, 14, 5, 0), new DateTime(2026, 11, 9, 1, 0, 0), "MARGIN_TARGET_TRIM; Alpha's Celular, Tablet & Navigim rule", 18.44m),
            new PriceListLine("RAISE1", 12m, new DateTime(2026, 11, 1, 14, 5, 0), null, "ELASTIC_OPTIMAL", null),
        }).Split('\n');
        Assert.Equal("Sku,ProposedPrice,StartDate,EndDate,AlgorithmUsed,NewMargin", csv[0]);
        Assert.Equal("1002483mo,35.50,2026-11-01 14:05,2026-11-09 01:00,\"MARGIN_TARGET_TRIM; Alpha's Celular, Tablet & Navigim rule\",18.4%", csv[1]);
        Assert.Equal("RAISE1,12.00,2026-11-01 14:05,,ELASTIC_OPTIMAL,", csv[2]);   // a raise is the regular price: no end date
    }

    [Fact]
    public void The_price_list_keeps_only_what_is_on_the_shelf_at_our_price()
    {
        var items = new[] { new PublishItem("A", 9.5m), new PublishItem("B", 19.5m), new PublishItem("C", 29.5m), new PublishItem("D", 39.5m) };
        var outcome = new PublishOutcome(1, "Published", 2, 0, 1, 1, 3, 0, false, new[]
        {
            new PublishException { Sku = "B", Kind = "cheaper-deal" },          // a cheaper team deal holds the shelf
            new PublishException { Sku = "C", Kind = "app-deal-cheaper" },      // ours is written for the web: on the shelf
        });
        Assert.Equal(new[] { "A", "C", "D" }, RunWeekPriceList.OnShelf(items, outcome).Select(i => i.Sku));
    }
}
