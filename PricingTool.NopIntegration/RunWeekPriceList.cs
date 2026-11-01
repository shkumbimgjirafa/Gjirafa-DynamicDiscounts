using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PricingTool.Agent;
using PricingTool.Agent.Tools;

namespace PricingTool.NopIntegration;

/// <summary>One price a Run week's publish wrote to the sandbox. Dates are local time; a regular-price raise has no end.</summary>
public sealed record PriceListLine(string Sku, decimal Price, DateTime? Start, DateTime? End, string AlgorithmUsed, decimal? NewMarginPct);

/// <summary>
/// The price list a Run week leaves (the user's request, 1 Nov): one line per checked price of the run that is on the sandbox
/// shelf at our price after its publish (written now, or already there from an earlier publish), as
/// <c>Sku, ProposedPrice, StartDate, EndDate, AlgorithmUsed, NewMargin</c>, in the exports folder. Prices the publish left to a
/// cheaper deal, skipped or couldn't read back are not on it. A cut is our <c>ai-price</c> discount, with its start and end read from
/// the sandbox; a raise is the store's regular price, with no end date. AlgorithmUsed is the engine's leading reason code, with
/// Alpha's band × category rule when the SKU priced against one; a price that differs from the engine's says what changed it (the
/// run checker's fix, or a person's margin target raising it). NewMargin is (price − PPTCV) / price on the VAT-inclusive price, the
/// engine's basis.
/// </summary>
public sealed class RunWeekPriceList
{
    public const string Step = "price-list";
    public const string Header = "Sku,ProposedPrice,StartDate,EndDate,AlgorithmUsed,NewMargin";

    /// <summary>Publish exceptions after which our price isn't what a shopper pays (an app-only cheaper deal isn't one: ours is
    /// written for the web).</summary>
    public static readonly IReadOnlySet<string> NotOnShelf = new HashSet<string>(StringComparer.Ordinal)
    {
        "cheaper-deal", "own-push", "at-or-above-shelf", "changed-since-run", "round-trip", "raised-under-team-deal", "raised-under-own-push",
        "above-ceiling", "no-ceiling", "chunk-failed",
    };

    private readonly AgentDataSource _source;
    private readonly AgentOptions _options;
    private readonly TimeProvider _clock;

    public RunWeekPriceList(AgentDataSource source, IOptions<AgentOptions> options, TimeProvider? clock = null)
    {
        _source = source;
        _options = options.Value;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The file a Run week's price list goes to (the newest, when it was written more than once).</summary>
    public static string? FindFile(string directory, long runWeekId) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, $"run-week-{runWeekId}-L*-prices-*.csv").OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault()
            : null;

    /// <summary>The prices of <paramref name="items"/> that are on the shelf at our price after the publish.</summary>
    public static IReadOnlyList<PublishItem> OnShelf(IReadOnlyList<PublishItem> items, PublishOutcome outcome)
    {
        var off = outcome.Exceptions.Where(e => NotOnShelf.Contains(e.Kind)).Select(e => e.Sku).ToHashSet(StringComparer.Ordinal);
        return items.Where(i => !off.Contains(i.Sku)).ToList();
    }

    public async Task<(string Path, IReadOnlyList<PriceListLine> Lines)> WriteAsync(long runWeekId, int layerId, long batchId,
        IReadOnlyList<PublishItem> onShelf, CancellationToken ct = default)
    {
        var lines = await ReadAsync(layerId, batchId, onShelf, ct);
        var directory = _options.Cycle.ExportDir();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"run-week-{runWeekId}-L{layerId}-prices-{_clock.GetLocalNow():yyyyMMdd-HHmmss}.csv");
        await File.WriteAllTextAsync(path, ToCsv(lines), new UTF8Encoding(true), ct);
        return (path, lines);
    }

    public static string ToCsv(IEnumerable<PriceListLine> lines)
    {
        static string D(DateTime? d) => d?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "";
        static string Csv(string s) => s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        var sb = new StringBuilder(Header).Append('\n');
        foreach (var l in lines)
            sb.Append(string.Join(",", Csv(l.Sku), l.Price.ToString("0.00", CultureInfo.InvariantCulture), D(l.Start), D(l.End), Csv(l.AlgorithmUsed),
                l.NewMarginPct is { } m ? m.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "")).Append('\n');
        return sb.ToString();
    }

    /// <summary>What a price list line says the price came from.</summary>
    public static string Algorithm(decimal price, decimal? proposed, string? reasonCodes, decimal? fixPrice, string? ruleCategory, decimal? ruleTarget, decimal? ruleFloor)
    {
        var lead = (reasonCodes ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "NONE";
        var what = proposed is { } p && Math.Abs(price - p) >= 0.01m
            ? fixPrice is { } f && Math.Abs(price - f) < 0.01m ? $"RUN_CHECK_FIX (engine: {lead} {p:0.00})"
              : price > p ? $"MARGIN_TARGET_RULE (engine: {lead} {p:0.00})"
              : lead
            : lead;
        return ruleCategory is null ? what
            : $"{what}; Alpha's {ruleCategory} rule: floor {ruleFloor:0.#}%, target {(ruleTarget is { } t ? $"{t:0.#}%" : "the band's")}";
    }

    private async Task<IReadOnlyList<PriceListLine>> ReadAsync(int layerId, long batchId, IReadOnlyList<PublishItem> onShelf, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_source.ConnectionString);
        await conn.OpenAsync(ct);
        var schema = await new SqlCommand("SELECT ShopSchema FROM ai.PublishBatch WHERE Id = @Batch", conn) { Parameters = { new SqlParameter("@Batch", batchId) } }
            .ExecuteScalarAsync(ct) as string ?? throw new InvalidOperationException($"Publish batch #{batchId} doesn't exist.");
        if (!schema.StartsWith("shop_sandbox", StringComparison.Ordinal))
            throw new InvalidOperationException($"Batch #{batchId} wrote to '{schema}', not a sandbox schema: no price list.");

        await new SqlCommand("CREATE TABLE #p (Sku nvarchar(400) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY, Price decimal(18,4) NOT NULL, Name nvarchar(450) COLLATE DATABASE_DEFAULT NOT NULL)", conn)
            .ExecuteNonQueryAsync(ct);
        var table = new System.Data.DataTable();
        table.Columns.Add("Sku", typeof(string));
        table.Columns.Add("Price", typeof(decimal));
        table.Columns.Add("Name", typeof(string));
        foreach (var i in onShelf.DistinctBy(i => i.Sku)) table.Rows.Add(i.Sku, i.Price, $"ai-price-{layerId}-{i.Sku}");
        using (var bulk = new SqlBulkCopy(conn) { DestinationTableName = "#p", BulkCopyTimeout = 300 })
            await bulk.WriteToServerAsync(table, ct);

        await using var cmd = new SqlCommand($"""
            DECLARE @Run bigint = (SELECT EngineRunId FROM ai.PublishBatch WHERE Id = @Batch);
            DECLARE @Mall bit = (SELECT CASE WHEN Brand = 'GjirafaMall' THEN 1 ELSE 0 END FROM PricingTool.Layers WHERE Id = @L);
            DECLARE @At datetime2 = (SELECT ISNULL(FinishedUtc, StartedUtc) FROM ai.PublishBatch WHERE Id = @Batch);
            SELECT w.Sku, w.Price,
                   CASE WHEN d.Id IS NOT NULL THEN d.StartDateUtc ELSE @At END,
                   d.EndDateUtc,
                   pp.ProposedPriceValue, pp.Pptcv, pp.ReasonCodes, fix.FixPrice, cr.Category, cr.TargetMarginPct, cr.MarginFloorPct
            FROM #p w
            LEFT JOIN (SELECT x.Name, x.Id, x.StartDateUtc, x.EndDateUtc, x.DiscountAmount,
                              ROW_NUMBER() OVER (PARTITION BY x.Name ORDER BY x.Id DESC) AS Rn
                       FROM [{schema}].[Discount] x WHERE x.Name LIKE @Prefix AND x.EndDateUtc > @At) d
                 ON d.Name = w.Name AND d.Rn = 1 AND ABS(d.DiscountAmount - w.Price) < 0.005
            LEFT JOIN (SELECT p.Id, p.ProductId, p.Sku, p.PriceBandId, p.ProposedPriceValue, p.Pptcv, p.ReasonCodes,
                              ROW_NUMBER() OVER (PARTITION BY p.Sku ORDER BY p.Id) AS Rn
                       FROM PricingTool.ProposedPrices p WHERE p.PricingRunId = @Run) pp ON pp.Sku = w.Sku AND pp.Rn = 1
            OUTER APPLY hk.ProductForProposal(@L, pp.ProductId, pp.Sku) h
            LEFT JOIN PricingTool.PricingRunCategoryRules cr ON cr.PricingRunId = @Run AND cr.BandId = pp.PriceBandId
                 AND cr.Category = CASE WHEN @Mall = 1 THEN ISNULL(h.MainGroup, N'Others') ELSE h.CategoryL1 END
            LEFT JOIN (SELECT r.ProposalId, MAX(r.FixPrice) AS FixPrice FROM ai.AgentRunCheckRow r JOIN ai.AgentRunCheck c ON c.Id = r.CheckId
                       WHERE c.RunId = @Run AND c.IsTest = 0 AND r.FinalSeverity = 'fixed' GROUP BY r.ProposalId) fix ON fix.ProposalId = pp.Id
            ORDER BY w.Sku
            """, conn) { CommandTimeout = 600 };
        cmd.Parameters.AddWithValue("@Batch", batchId);
        cmd.Parameters.AddWithValue("@L", layerId);
        cmd.Parameters.AddWithValue("@Prefix", $"ai-price-{layerId}-%");
        var zone = _clock.LocalTimeZone;
        DateTime? Local(SqlDataReader r, int i) => r.IsDBNull(i) ? null : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc), zone);
        decimal? Dec(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDecimal(i);
        var lines = new List<PriceListLine>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var price = rd.GetDecimal(1);
            var cost = Dec(rd, 5);
            lines.Add(new PriceListLine(rd.GetString(0), price, Local(rd, 2), Local(rd, 3),
                Algorithm(price, Dec(rd, 4), rd.IsDBNull(6) ? null : rd.GetString(6), Dec(rd, 7), rd.IsDBNull(8) ? null : rd.GetString(8), Dec(rd, 9), Dec(rd, 10)),
                cost is { } c && c > 0 && price > 0 ? Math.Round(100m * (price - c) / price, 1) : null));
        }
        return lines;
    }
}
