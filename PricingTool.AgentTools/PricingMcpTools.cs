using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using PricingTool.Agent.Tools;

namespace PricingTool.AgentTools;

/// <summary>
/// The agents' read-only data tools, served over MCP (stdio) to Claude Code. Every call is logged in
/// ai.AgentQueryLog under the agent and the AI call that made it. Errors come back as {"error": ...}
/// so the model can correct itself.
/// </summary>
[McpServerToolType]
public sealed class PricingMcpTools
{
    private const string LayerHelp = "Layer id: 1 = GjirafaMall KS, 4 = Gjirafa50 KS.";
    private const string BandHelp = "Optional band id (see get_current_config).";
    private const string CategoryHelp = "Optional category: the main group on layer 1, the L1 category on layer 4.";
    // Relaxed escaping keeps Albanian category names and dashes readable (and short) for the model;
    // the text goes to the agent over stdio, never into HTML.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly AgentDataTools _tools;
    private readonly QueryDataService _query;
    private readonly AgentToolContext _ctx;

    public PricingMcpTools(AgentDataTools tools, QueryDataService query, AgentToolContext ctx)
    {
        _tools = tools;
        _query = query;
        _ctx = ctx;
    }

    [McpServerTool(Name = AgentToolNames.GetCurrentConfig, ReadOnly = true)]
    [Description("Today's price bands for a layer: cost limits, margin floor and target margin per band.")]
    public async Task<string> GetCurrentConfig([Description(LayerHelp)] int layer, CancellationToken ct = default) =>
        Serialize(await _tools.GetCurrentConfigAsync(_ctx, layer, ct));

    [McpServerTool(Name = AgentToolNames.GetBandPerformance, ReadOnly = true)]
    [Description("Units, net revenue, gross profit, margin, share sold below and at the floor, and cross-dock share per band x category, " +
                 "over normal days (campaign days excluded) ending 14 days ago.")]
    public async Task<string> GetBandPerformance(
        [Description(LayerHelp)] int layer,
        [Description(BandHelp)] int? band = null,
        [Description(CategoryHelp)] string? category = null,
        [Description("Window length in days (7-365), ending 14 days ago.")] int days = 30,
        CancellationToken ct = default) =>
        Serialize(await _tools.GetBandPerformanceAsync(_ctx, layer, band, category, days, ct));

    [McpServerTool(Name = AgentToolNames.GetCostChanges, ReadOnly = true)]
    [Description("SKUs whose booked unit cost moved more than minChangePct percent (last 30 days vs the 60 before), largest first.")]
    public async Task<string> GetCostChanges(
        [Description(LayerHelp)] int layer,
        [Description(BandHelp)] int? band = null,
        [Description(CategoryHelp)] string? category = null,
        [Description("Smallest move to list, in percent.")] decimal minChangePct = 5,
        CancellationToken ct = default) =>
        Serialize(await _tools.GetCostChangesAsync(_ctx, layer, band, category, minChangePct, ct));

    [McpServerTool(Name = AgentToolNames.GetStockHealth, ReadOnly = true)]
    [Description("Local stock value at cost, dead-stock share and the longest-dead SKU per band x category, from the latest engine run.")]
    public async Task<string> GetStockHealth(
        [Description(LayerHelp)] int layer,
        [Description(BandHelp)] int? band = null,
        [Description(CategoryHelp)] string? category = null,
        CancellationToken ct = default) =>
        Serialize(await _tools.GetStockHealthAsync(_ctx, layer, band, category, ct));

    [McpServerTool(Name = AgentToolNames.GetPastDecisions, ReadOnly = true)]
    [Description("Earlier agent decisions for this layer and their measured outcomes.")]
    public async Task<string> GetPastDecisions(
        [Description(LayerHelp)] int layer,
        [Description(BandHelp)] int? band = null,
        [Description(CategoryHelp)] string? category = null,
        CancellationToken ct = default) =>
        Serialize(await _tools.GetPastDecisionsAsync(_ctx, layer, band, category, ct));

    [McpServerTool(Name = AgentToolNames.GetElasticitySummary, ReadOnly = true)]
    [Description("Fitted price elasticities per band x category: how many SKUs have a usable fit, the median, and the share steeper than -1.")]
    public async Task<string> GetElasticitySummary(
        [Description(LayerHelp)] int layer,
        [Description(BandHelp)] int? band = null,
        [Description(CategoryHelp)] string? category = null,
        CancellationToken ct = default) =>
        Serialize(await _tools.GetElasticitySummaryAsync(_ctx, layer, band, category, ct));

    [McpServerTool(Name = AgentToolNames.GetCampaignHistory, ReadOnly = true)]
    [Description("Every past campaign of the layer: units per day vs the 28 days before (season-adjusted with the same windows a year " +
                 "earlier), discount depth vs normal days, margin and gross profit per day; plus the uplift curve (units multiplier per " +
                 "extra point of discount), per category. Calendar ids for get_campaign_results; upcoming campaigns.")]
    public async Task<string> GetCampaignHistory(
        [Description(LayerHelp)] int layer,
        [Description(CategoryHelp + " Without it: per-campaign totals and a curve per category.")] string? category = null,
        CancellationToken ct = default) =>
        Serialize(await _tools.GetCampaignHistoryAsync(_ctx, layer, category, ct));

    [McpServerTool(Name = AgentToolNames.GetSalesTrends, ReadOnly = true)]
    [Description("Revenue, gross profit, margin, units and discount depth per week vs the same weeks a year earlier, with campaign days " +
                 "per week, plus local stock and dead-stock share. Use it to spot where a mini-campaign would help.")]
    public async Task<string> GetSalesTrends(
        [Description(LayerHelp)] int layer,
        [Description(CategoryHelp)] string? category = null,
        [Description("Weeks back from today (2-52).")] int weeks = 12,
        CancellationToken ct = default) =>
        Serialize(await _tools.GetSalesTrendsAsync(_ctx, layer, category, weeks, ct));

    [McpServerTool(Name = AgentToolNames.GetCampaignResults, ReadOnly = true)]
    [Description("A finished campaign's actual results against its goal and against the 28 days before.")]
    public async Task<string> GetCampaignResults(
        [Description("The campaign's calendarId (from get_campaign_history).")] long campaignId,
        CancellationToken ct = default) =>
        Serialize(await _tools.GetCampaignResultsAsync(_ctx, campaignId, ct));

    [McpServerTool(Name = AgentToolNames.QueryData, ReadOnly = true)]
    [Description("Run your own read-only SQL against the agent.* views (see the data dictionary). Exactly one SELECT statement; " +
                 "at most 500 rows come back; 30-second limit. Aggregate in SQL. A refused or failed query returns the reason in 'error'.")]
    public async Task<string> QueryData([Description("One T-SQL SELECT statement over agent.* views.")] string sql, CancellationToken ct = default)
    {
        var r = await _query.RunAsync(_ctx, sql, ct);
        return Serialize(r.Error is not null
            ? new { error = r.Error }
            : new { columns = r.Columns, rows = r.Rows, rowCount = r.Rows.Count, truncated = r.Truncated, note = r.Note, durationMs = r.DurationMs });
    }

    private static string Serialize(object value) => JsonSerializer.Serialize(value, Json);
}
