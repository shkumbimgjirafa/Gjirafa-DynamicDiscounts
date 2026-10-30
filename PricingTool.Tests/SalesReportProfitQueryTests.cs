using PricingTool.Data.Services;

namespace PricingTool.Tests;

/// <summary>
/// Guards the cancelled-order correction on the realized-profit reads. The sales report
/// (SR_ProductsData) carries its own OrderStatusId, but it trails the shop by days: an order cancelled
/// in nopCommerce keeps sitting at status 30 there until the feed catches up. Measured on prod
/// 2026-09-14 (GjirafaMall KS), one week's bar held €10.5k of margin — 21% of it — belonging to orders
/// the shop had ALREADY cancelled, and the stale value swung by thousands within minutes as the feed
/// rewrote rows. Both queries therefore also require the authoritative [Order] status to be a valid sale.
/// </summary>
public class SalesReportProfitQueryTests
{
    public static TheoryData<string> ProfitQueries => new()
    {
        SqlSalesReportProfitReader.WeeklyQuery,
        SqlSalesReportProfitReader.ProfitWindowsQuery,
    };

    [Theory, MemberData(nameof(ProfitQueries))]
    public void Query_checks_the_authoritative_order_status(string sql)
    {
        // LEFT, never INNER: an unmatchable order must fall back to the report's own status rather than
        // silently dropping a real sale from the trend.
        Assert.Contains("LEFT JOIN {opDb}.dbo.[Order] o ON o.Id = d.OrderId", sql);
        Assert.Contains("ISNULL(o.OrderStatusId, d.OrderStatusId) IN (10, 20, 30)", sql);
    }

    [Theory, MemberData(nameof(ProfitQueries))]
    public void Query_keeps_the_report_status_filter_too(string sql)
    {
        // The rule is one-sided — it may only REMOVE margin the shop has cancelled, never resurrect a
        // line the report itself already flagged. Dropping this clause would do exactly that.
        Assert.Contains("d.OrderStatusId IN (10, 20, 30)", sql);
    }

    [Theory, MemberData(nameof(ProfitQueries))]
    public void Query_substitutes_the_layer_database(string sql)
    {
        var bound = sql.Replace(SourceQueries.OperationalDbToken,
            SourceQueries.SafeDbName("GjirafaEcommerce"));

        Assert.DoesNotContain(SourceQueries.OperationalDbToken, bound);
        Assert.Contains("GjirafaEcommerce.dbo.[Order]", bound);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("GjirafaMall;DROP TABLE x")]
    [InlineData("Gjirafa Mall")]
    [InlineData("[GjirafaMall]")]
    public void SafeDbName_rejects_anything_but_a_plain_identifier(string name) =>
        Assert.Throws<InvalidOperationException>(() => SourceQueries.SafeDbName(name));

    [Theory]
    [InlineData("GjirafaMall")]
    [InlineData("GjirafaEcommerce")]
    [InlineData("Gjirafa_50")]
    public void SafeDbName_passes_a_real_database_name(string name) =>
        Assert.Equal(name, SourceQueries.SafeDbName(name));
}
