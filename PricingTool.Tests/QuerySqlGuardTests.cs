using PricingTool.Agent.Tools;

namespace PricingTool.Tests;

/// <summary>
/// The parse-level wall of query_data: one SELECT over agent.* views (or its own CTEs), nothing else.
/// The agent_reader user (SELECT on schema agent only) is the second wall, checked against the real DB.
/// </summary>
public class QuerySqlGuardTests
{
    [Theory]
    [InlineData("SELECT BandId, SUM(Qty) AS Units FROM agent.SalesLines WHERE LayerId = 4 GROUP BY BandId")]
    [InlineData("select top 10 * from [agent].[Products] p join agent.SalesLines s on s.Sku = p.Sku;")]
    [InlineData("WITH m AS (SELECT LayerId, SUM(NetRevenue) AS Net FROM agent.DailySales GROUP BY LayerId) SELECT * FROM m")]
    [InlineData("SELECT x.n FROM (VALUES (1), (2)) AS x(n)")]
    [InlineData("-- units by band\nSELECT BandId, COUNT(*) FROM agent.SalesLines GROUP BY BandId -- done")]
    public void One_select_over_agent_views_is_accepted(string sql)
    {
        var v = QuerySqlGuard.Check(sql);
        Assert.True(v.Ok, v.Error);
    }

    [Theory]
    [InlineData("UPDATE agent.SalesLines SET Qty = 0", "Only SELECT")]
    [InlineData("DELETE FROM hk.SalesLines", "Only SELECT")]
    [InlineData("SELECT 1; SELECT 2", "Exactly one statement")]
    [InlineData("SELECT 1 FROM agent.Layers; WAITFOR DELAY '00:00:10'", "Exactly one statement")]
    [InlineData("WAITFOR DELAY '00:00:10'", "Only SELECT")]
    [InlineData("EXEC sp_who", "Only SELECT")]
    [InlineData("SELECT * INTO #t FROM agent.Layers", "INTO")]
    [InlineData("SELECT * FROM PricingTool.Layers", "outside the agent schema")]
    [InlineData("SELECT * FROM hk.SalesLines", "outside the agent schema")]
    [InlineData("SELECT * FROM sys.tables", "outside the agent schema")]
    [InlineData("SELECT * FROM Layers", "needs its schema")]
    [InlineData("SELECT * FROM master.agent.Layers", "other databases")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'Server=x;', 'SELECT 1') AS r", "OPENROWSET")]
    [InlineData("SELECT * FROM OPENQUERY(srv, 'SELECT 1')", "OPENQUERY")]
    [InlineData("SELEC * FROM agent.Layers", "does not parse")]
    [InlineData("   ", "Empty")]
    [InlineData("SELECT * FROM agent.Layers\nGO\nSELECT 1", "Exactly one statement")]
    public void Anything_else_is_refused_with_a_reason(string sql, string reason)
    {
        var v = QuerySqlGuard.Check(sql);
        Assert.False(v.Ok);
        Assert.Contains(reason, v.Error);
    }

    [Fact]
    public void A_cte_name_is_not_a_way_around_the_schema_rule()
    {
        var v = QuerySqlGuard.Check("WITH m AS (SELECT * FROM PricingTool.ApprovalItems) SELECT * FROM m");
        Assert.False(v.Ok);
        Assert.Contains("PricingTool.ApprovalItems", v.Error);
    }
}
