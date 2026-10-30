using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace PricingTool.Agent.Tools;

/// <summary>The answer to one <c>query_data</c> call. A refused or failed query has <see cref="Error"/>
/// set; that text goes back to the model as the tool result so it can fix its query.</summary>
public sealed record QueryDataResult(
    IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, bool Truncated, int DurationMs, string? Error, string? Note);

/// <summary>
/// <c>query_data(sql)</c>: the agents' own read-only SQL. Guardrails, all in code:
/// <list type="number">
/// <item>parsed with ScriptDom: one SELECT, no INTO, agent.* views or own CTEs only (<see cref="QuerySqlGuard"/>);</item>
/// <item>run as the database user <c>agent_reader</c> (no login, SELECT on schema agent only), on a fresh,
///   unpooled connection so the impersonation dies with it;</item>
/// <item>timeout (30 s) and row cap (500, the answer says when it was cut off);</item>
/// <item>one <c>ai.AgentQueryLog</c> row per call, refused or run.</item>
/// </list>
/// </summary>
public sealed class QueryDataService
{
    private static readonly Regex SafeUserName = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);

    private readonly AgentDataSource _source;
    private readonly QueryDataOptions _options;
    private readonly AgentToolLog _log;

    public QueryDataService(AgentDataSource source, IOptions<AgentOptions> options, AgentToolLog log)
    {
        _source = source;
        _options = options.Value.QueryData;
        _log = log;
        if (!SafeUserName.IsMatch(_options.ReaderUser))
            throw new InvalidOperationException($"Agent:QueryData:ReaderUser '{_options.ReaderUser}' is not a plain user name.");
    }

    public async Task<QueryDataResult> RunAsync(AgentToolContext ctx, string sql, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var verdict = QuerySqlGuard.Check(sql);
        if (!verdict.Ok)
        {
            var checkedIn = sw.Elapsed;
            await _log.WriteAsync(ctx, AgentToolNames.QueryData, sql, accepted: false, null, false, checkedIn, verdict.Error);
            return new QueryDataResult(Array.Empty<string>(), Array.Empty<object?[]>(), false, (int)checkedIn.TotalMilliseconds,
                "Refused: " + verdict.Error, null);
        }

        var columns = new List<string>();
        var rows = new List<object?[]>();
        var truncated = false;
        string? error = null;
        try
        {
            var csb = new SqlConnectionStringBuilder(_source.ConnectionString) { Pooling = false };
            await using var conn = new SqlConnection(csb.ConnectionString);
            await conn.OpenAsync(ct);
            await using (var impersonate = new SqlCommand($"EXECUTE AS USER = '{_options.ReaderUser}';", conn))
                await impersonate.ExecuteNonQueryAsync(ct);

            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _options.TimeoutSeconds };
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            for (var i = 0; i < reader.FieldCount; i++) columns.Add(reader.GetName(i));
            while (await reader.ReadAsync(ct))
            {
                if (rows.Count == _options.MaxRows) { truncated = true; break; }
                var values = new object?[reader.FieldCount];
                for (var i = 0; i < values.Length; i++) values[i] = ToJsonValue(reader.GetValue(i));
                rows.Add(values);
            }
            if (truncated) cmd.Cancel();
        }
        catch (SqlException ex) when (ex.Number == -2)
        {
            error = $"Timed out after {_options.TimeoutSeconds} s. Aggregate more in SQL, or narrow the dates.";
        }
        catch (SqlException ex)
        {
            error = $"SQL error {ex.Number}: {ex.Message}";
        }
        sw.Stop();

        await _log.WriteAsync(ctx, AgentToolNames.QueryData, sql, accepted: true, error is null ? rows.Count : null, truncated,
            sw.Elapsed, error);
        return new QueryDataResult(columns, rows, truncated, (int)sw.ElapsedMilliseconds, error,
            truncated ? $"Cut off at {_options.MaxRows} rows; there are more. Aggregate in SQL instead of reading rows." : null);
    }

    private static object? ToJsonValue(object value) => value switch
    {
        DBNull => null,
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd") : d.ToString("yyyy-MM-ddTHH:mm:ss"),
        DateTimeOffset d => d.ToString("O"),
        byte[] b => $"<{b.Length} bytes>",
        _ => value,
    };
}
