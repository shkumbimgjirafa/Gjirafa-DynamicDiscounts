using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PricingTool.Agent.Storage;
using PricingTool.Agent.Tools;

namespace PricingTool.Agent.Operations;

public sealed record RefreshResult(string Table, string Mode, long Rows, long ReplicaMs, long TotalMs, string Note);

/// <summary>
/// Keeps the analysis copy of the shop data (<c>hk.*</c>) current from the read-only NOP replica, inside the app
/// (the queries are ported from docs/hackathon-2026/exports/, the data-prep scripts are not called):
/// <list type="bullet">
/// <item>sales lines: from the last loaded day minus <see cref="CycleOptions.SalesLateWindowDays"/> on, replacing
///   that window, because late cancellations still remove lines;</item>
/// <item>products and the KS discount snapshot: a full reload when older than
///   <see cref="CycleOptions.SnapshotRefreshDays"/> (the heaviest reads, and they change slowly).</item>
/// </list>
/// Each replica read is timed; each load swaps in one transaction and writes a row to hk.DataSources.
/// </summary>
public sealed class HkRefresher
{
    private const int ReplicaTimeoutSeconds = 1800;
    private readonly AgentDataSource _local;
    private readonly string _replica;
    private readonly CycleOptions _options;
    private readonly ILogger<HkRefresher> _logger;
    private readonly TimeProvider _clock;

    public HkRefresher(AgentDataSource local, IConfiguration config, IOptions<AgentOptions> options, ILogger<HkRefresher> logger,
        TimeProvider? clock = null)
    {
        _local = local;
        _replica = config.GetConnectionString("SourceReadOnly") ?? "";
        _options = options.Value.Cycle;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<RefreshResult>> RefreshAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_replica))
            throw new InvalidOperationException("No replica connection (ConnectionStrings:SourceReadOnly, from user-secrets).");
        var results = new List<RefreshResult> { await RefreshSalesLinesAsync(ct) };
        foreach (var (table, sql) in new[]
                 {
                     ("hk.Products", "products_ks.sql"),
                     ("hk.ActiveDiscountsKs", "active_discounts_ks.sql"),
                     ("hk.ActiveDiscountProductsKs", "active_discount_products_ks.sql"),
                     ("hk.ProductDiscountFlagsKs", "product_discount_flags_ks.sql"),
                 })
        {
            var age = await AgeDaysAsync(table, ct);
            if (age is { } days && days < _options.SnapshotRefreshDays)
            {
                results.Add(new RefreshResult(table, "kept", 0, 0, 0, $"loaded {days:0.0} days ago (reloaded every {_options.SnapshotRefreshDays})"));
                continue;
            }
            results.Add(await ReloadAsync(table, Sql(sql), null, null, $"full reload, NOP replica read-only: ported from docs/hackathon-2026/exports/{sql}", ct));
        }
        // Keep the proposal → product-map lookup in place (a no-op when it already is).
        await ProductMapSql.EnsureAsync(_local.ConnectionString, ct);
        return results;
    }

    private async Task<RefreshResult> RefreshSalesLinesAsync(CancellationToken ct)
    {
        DateTime from;
        await using (var conn = await OpenLocalAsync(ct))
        await using (var cmd = new SqlCommand("SELECT MAX(DateAdded) FROM hk.SalesLines", conn))
        {
            var last = await cmd.ExecuteScalarAsync(ct);
            if (last is not DateTime d) throw new InvalidOperationException("hk.SalesLines is empty: the initial load is the data-prep script's job.");
            from = d.Date.AddDays(-_options.SalesLateWindowDays);
        }
        return await ReloadAsync("hk.SalesLines", Sql("sales_lines_incremental.sql"), "DateAdded", from,
            $"incremental from {from:yyyy-MM-dd} (last loaded day minus {_options.SalesLateWindowDays}), NOP replica read-only: ported from docs/hackathon-2026/exports/sales_lines_ks.sql", ct);
    }

    /// <summary>Reads the replica query into a staging table, then in one transaction replaces the target
    /// (all of it, or the rows from <paramref name="from"/> on) and logs the load.</summary>
    private async Task<RefreshResult> ReloadAsync(string table, string sql, string? windowColumn, DateTime? from, string origin, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        await using var local = await OpenLocalAsync(ct);
        await Exec(local, null, $"DROP TABLE IF EXISTS #stage; SELECT TOP 0 * INTO #stage FROM {table};", ct);

        var replicaWatch = Stopwatch.StartNew();
        await using (var src = new SqlConnection(_replica))
        {
            await src.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, src) { CommandTimeout = ReplicaTimeoutSeconds };
            if (from is { } f) cmd.Parameters.Add("@From", System.Data.SqlDbType.DateTime).Value = f;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            using var bulk = new SqlBulkCopy(local) { DestinationTableName = "#stage", BatchSize = 20000, BulkCopyTimeout = 0 };
            for (var i = 0; i < reader.FieldCount; i++) bulk.ColumnMappings.Add(reader.GetName(i), reader.GetName(i));
            await bulk.WriteToServerAsync(reader, ct);
        }
        replicaWatch.Stop();

        long rows;
        await using (var tx = (SqlTransaction)await local.BeginTransactionAsync(ct))
        {
            var where = windowColumn is null ? "" : $" WHERE {windowColumn} >= @From";
            await using (var del = new SqlCommand($"DELETE FROM {table}{where};", local, tx) { CommandTimeout = 1800 })
            {
                if (from is { } f) del.Parameters.Add("@From", System.Data.SqlDbType.DateTime).Value = f;
                await del.ExecuteNonQueryAsync(ct);
            }
            await using (var ins = new SqlCommand($"INSERT INTO {table} SELECT * FROM #stage; SELECT CAST(@@ROWCOUNT AS bigint);", local, tx) { CommandTimeout = 1800 })
                rows = (long)(await ins.ExecuteScalarAsync(ct))!;
            await using (var log = new SqlCommand("""
                INSERT INTO hk.DataSources (TableName, Source, Origin, ExtractedAtUtc, [RowCount], ExtractedBy, Notes)
                VALUES (@T, 'nop-replica', @O, SYSUTCDATETIME(), @N, 'daily cycle', @Notes)
                """, local, tx))
            {
                log.Parameters.AddWithValue("@T", table);
                log.Parameters.AddWithValue("@O", origin);
                log.Parameters.AddWithValue("@N", rows);
                log.Parameters.AddWithValue("@Notes", $"replica read {replicaWatch.ElapsedMilliseconds / 1000.0:0.0} s");
                await log.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        _logger.LogInformation("Refreshed {Table}: {Rows} rows, replica {Replica} ms.", table, rows, replicaWatch.ElapsedMilliseconds);
        return new RefreshResult(table, windowColumn is null ? "full" : "window", rows, replicaWatch.ElapsedMilliseconds, total.ElapsedMilliseconds, origin);
    }

    private async Task<double?> AgeDaysAsync(string table, CancellationToken ct)
    {
        await using var conn = await OpenLocalAsync(ct);
        await using var cmd = new SqlCommand("SELECT MAX(ExtractedAtUtc) FROM hk.DataSources WHERE TableName = @T", conn);
        cmd.Parameters.AddWithValue("@T", table);
        return await cmd.ExecuteScalarAsync(ct) is DateTime last ? (_clock.GetUtcNow().UtcDateTime - last).TotalDays : null;
    }

    private static string Sql(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"PricingTool.Agent.Operations.Sql.{name}")
            ?? throw new InvalidOperationException($"Embedded query {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private async Task<SqlConnection> OpenLocalAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(_local.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static async Task Exec(SqlConnection conn, SqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = 600 };
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
