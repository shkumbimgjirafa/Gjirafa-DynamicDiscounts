using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using PricingTool.Agent.Tools;
using PricingTool.NopIntegration;

namespace PricingTool.Web.Services;

/// <summary>What the sandbox shelf shows for one SKU after its run's publish: the text for the cell, a Bootstrap text class and a tooltip.</summary>
public sealed record SandboxShelf(string Text, string Css, string Title);

/// <summary>
/// The Proposals tab's Sandbox column (the user's request, 1 Nov): for the SKUs on screen, what the run's latest publish put on the
/// shop sandbox's shelf. A cut is our <c>ai-price</c> discount (its price and dates, read from the sandbox), a raise is the store's
/// regular price; a price the run checker held, or the publish left to a cheaper deal, says why it isn't there. Read-only.
/// </summary>
public class SandboxShelfLookup
{
    private readonly AgentDataSource _source;

    public SandboxShelfLookup(AgentDataSource source) => _source = source;

    private static readonly Dictionary<string, string> Why = new(StringComparer.Ordinal)
    {
        ["cheaper-deal"] = "a cheaper deal holds the shelf",
        ["own-push"] = "our earlier push holds the shelf",
        ["at-or-above-shelf"] = "the shelf is already at or under this price",
        ["changed-since-run"] = "the shop's price changed since the run",
        ["round-trip"] = "didn't read back as written",
        ["raised-under-team-deal"] = "raised, but a cheaper team deal holds the shelf",
        ["raised-under-own-push"] = "raised, but our earlier push holds the shelf",
        ["above-ceiling"] = "above its ceiling: not written",
        ["no-ceiling"] = "no ceiling known: not written",
        ["chunk-failed"] = "the write failed and was rolled back",
    };

    public async Task<IReadOnlyDictionary<string, SandboxShelf>> ForRunAsync(long runId, int layerId, IReadOnlyCollection<string> skus,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, SandboxShelf>(StringComparer.Ordinal);
        if (skus.Count == 0) return result;
        await using var conn = new SqlConnection(_source.ConnectionString);
        await conn.OpenAsync(ct);

        long? batchId = null, checkId = null;
        string? schema = null;
        await using (var head = new SqlCommand("""
            SELECT (SELECT TOP 1 Id FROM ai.PublishBatch WHERE EngineRunId = @Run AND Kind = 'Regular' AND Status = 'Published' ORDER BY Id DESC),
                   (SELECT TOP 1 ShopSchema FROM ai.PublishBatch WHERE EngineRunId = @Run AND Kind = 'Regular' AND Status = 'Published' ORDER BY Id DESC),
                   (SELECT TOP 1 Id FROM ai.AgentRunCheck WHERE RunId = @Run AND IsTest = 0 AND Status <> 'Running' ORDER BY Id DESC)
            """, conn))
        {
            head.Parameters.AddWithValue("@Run", runId);
            await using var r = await head.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                batchId = r.IsDBNull(0) ? null : r.GetInt64(0);
                schema = r.IsDBNull(1) ? null : r.GetString(1);
                checkId = r.IsDBNull(2) ? null : r.GetInt64(2);
            }
        }
        // The schema name goes into the SQL, so only the sandbox's own (shop_sandbox_*) is ever read.
        var sandbox = schema is not null && schema.StartsWith("shop_sandbox_", StringComparison.Ordinal) && schema.All(c => char.IsLetterOrDigit(c) || c == '_')
            ? schema : null;
        var discounts = sandbox is null
            ? "SELECT CAST(NULL AS nvarchar(450)) AS Name, CAST(NULL AS decimal(18,4)) AS DiscountAmount, CAST(NULL AS datetime2) AS StartDateUtc, CAST(NULL AS datetime2) AS EndDateUtc, 1 AS Rn WHERE 1 = 0"
            : $"SELECT x.Name, x.DiscountAmount, x.StartDateUtc, x.EndDateUtc, ROW_NUMBER() OVER (PARTITION BY x.Name ORDER BY x.Id DESC) AS Rn FROM [{sandbox}].[Discount] x " +
              "WHERE x.Name IN (SELECT N'ai-price-' + @L + N'-' + Sku FROM s) AND x.EndDateUtc > SYSUTCDATETIME()";

        await using var cmd = new SqlCommand($"""
            WITH s AS (SELECT DISTINCT CAST(value AS nvarchar(400)) AS Sku FROM OPENJSON(@Skus)),
            held AS (SELECT r.Sku, MIN(r.FinalSeverity) AS Sev FROM ai.AgentRunCheckRow r
                     WHERE r.CheckId = @Check AND r.FinalSeverity IN ('block', 'warn') AND r.Sku IN (SELECT Sku FROM s) GROUP BY r.Sku),
            ex AS (SELECT e.Sku, STRING_AGG(e.Kind, ',') AS Kinds FROM ai.PublishException e
                   WHERE e.BatchId = @Batch AND e.Sku IN (SELECT Sku FROM s) GROUP BY e.Sku),
            tier AS (SELECT a.Sku, MAX(a.NewTierPrice) AS Price, MAX(a.CreatedUtc) AS At FROM ai.PublishAudit a
                     WHERE a.BatchId = @Batch AND a.Applied = 1 AND a.Change LIKE 'TierPrice%' AND a.Sku IN (SELECT Sku FROM s) GROUP BY a.Sku),
            d AS ({discounts})
            SELECT s.Sku, held.Sev, ex.Kinds, tier.Price, tier.At, d.DiscountAmount, d.StartDateUtc, d.EndDateUtc
            FROM s
            LEFT JOIN held ON held.Sku = s.Sku
            LEFT JOIN ex ON ex.Sku = s.Sku
            LEFT JOIN tier ON tier.Sku = s.Sku
            LEFT JOIN d ON d.Name = N'ai-price-' + @L + N'-' + s.Sku AND d.Rn = 1
            """, conn) { CommandTimeout = 60 };
        cmd.Parameters.AddWithValue("@Skus", JsonSerializer.Serialize(skus));
        cmd.Parameters.AddWithValue("@Check", (object?)checkId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Batch", (object?)batchId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@L", layerId.ToString(CultureInfo.InvariantCulture));

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var sku = rd.GetString(0);
            var held = rd.IsDBNull(1) ? null : rd.GetString(1);
            var kinds = rd.IsDBNull(2) ? Array.Empty<string>() : rd.GetString(2).Split(',');
            var off = kinds.FirstOrDefault(RunWeekPriceList.NotOnShelf.Contains);
            result[sku] = held is not null
                ? new SandboxShelf(held == "block" ? "held: blocked" : "held by the check", held == "block" ? "text-danger" : "text-warning",
                    "The run checker held this price; it waits for a person in Waiting for you and wasn't published.")
                : off is not null
                    ? new SandboxShelf("not on the shelf", "text-muted", Why.GetValueOrDefault(off, off))
                    : !rd.IsDBNull(5)
                        ? new SandboxShelf($"{rd.GetDecimal(5):0.00} · {Day(rd, 6)}–{Day(rd, 7)}", "text-success",
                            "Our ai-price discount on the sandbox shelf, local time " + Stamp(rd, 6) + " to " + Stamp(rd, 7) +
                            (kinds.Contains("app-deal-cheaper") ? "; a cheaper app-only deal applies in the app" : ""))
                        : !rd.IsDBNull(3)
                            ? new SandboxShelf($"{rd.GetDecimal(3):0.00} regular", "text-success",
                                "Raised as the store's regular price (a discount can only lower a price), written " + Stamp(rd, 4) + "; no end date.")
                            : batchId is null
                                ? new SandboxShelf("–", "text-muted", "This run hasn't been published to the sandbox.")
                                : new SandboxShelf("–", "text-muted", "Not published by this run: no change, a holdout category, a held-back cut, or not in its publish.");
        }
        return result;
    }

    private static DateTime? Local(SqlDataReader r, int i) =>
        r.IsDBNull(i) ? null : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc), TimeZoneInfo.Local);
    private static string Day(SqlDataReader r, int i) => Local(r, i)?.ToString("d MMM", CultureInfo.InvariantCulture) ?? "?";
    private static string Stamp(SqlDataReader r, int i) => Local(r, i)?.ToString("d MMM HH:mm", CultureInfo.InvariantCulture) ?? "?";
}
