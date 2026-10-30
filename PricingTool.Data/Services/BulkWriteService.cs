using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using PricingTool.Data.Entities;

namespace PricingTool.Data.Services;

/// <summary>
/// One SKU's paced dead-stock markdown update from a pricing run: the new target (PERCENT discount
/// off the anchor; null clears an ended markdown) and the period clock (see
/// <c>SkuDeadStockState.MarkdownCaughtUpStreak</c>).
/// </summary>
public readonly record struct SkuMarkdownPacingUpdate(string Sku, decimal? TargetPct, int? CaughtUpStreak);

/// <summary>
/// One SKU's dead-stock tunnel provenance update from a pricing run: the below-margin-floor price the
/// dead-stock lane owns for it, or null to clear the marker (see
/// <c>SkuDeadStockState.BelowFloorTunnelPrice</c>). Kept separate from
/// <see cref="SkuMarkdownPacingUpdate"/> so the pacing period clock — which a sale deliberately resets —
/// is never written on the same path as a marker that must survive one.
/// </summary>
public readonly record struct SkuTunnelPriceUpdate(string Sku, decimal? TunnelPrice);

/// <summary>
/// High-throughput writer for the volume tables (DailySnapshots, ProposedPrices, AlgorithmVotes).
/// A full-catalog run produces ~680k snapshots + ~680k proposals + millions of votes; EF Core's
/// row-by-row inserts cannot persist that in reasonable time. Abstracted behind an interface so
/// tests can substitute an EF-backed implementation against the in-memory provider.
/// </summary>
public interface IBulkWriteService
{
    Task<long> GetMaxProposedPriceIdAsync(CancellationToken ct = default);
    Task DeleteSnapshotsForDateAsync(int layerId, DateTime date, CancellationToken ct = default);
    Task ReseedProposedPricesAsync(CancellationToken ct = default);
    Task BulkInsertSnapshotsAsync(IReadOnlyCollection<DailySnapshot> rows, CancellationToken ct = default);
    Task BulkInsertProposalsAsync(IReadOnlyCollection<ProposedPrice> proposals, CancellationToken ct = default);
    Task BulkInsertVotesAsync(IReadOnlyCollection<AlgorithmVoteRecord> votes, CancellationToken ct = default);
    Task DeleteElasticityForLayerAsync(int layerId, CancellationToken ct = default);
    Task BulkInsertElasticityAsync(IReadOnlyCollection<SkuElasticity> rows, CancellationToken ct = default);
    Task UpsertDeadStockStateAsync(IReadOnlyCollection<SkuDeadStockState> rows, CancellationToken ct = default);
    /// <summary>Post-run update of the paced dead-stock markdown fields for one layer's SKUs. Rows whose state row is missing are skipped (matched-only).</summary>
    Task UpsertDeadStockMarkdownAsync(int layerId, IReadOnlyCollection<SkuMarkdownPacingUpdate> rows, CancellationToken ct = default);
    /// <summary>Post-run update of the dead-stock tunnel provenance marker for one layer's SKUs. Matched-only, like the markdown write.</summary>
    Task UpsertDeadStockTunnelPriceAsync(int layerId, IReadOnlyCollection<SkuTunnelPriceUpdate> rows, CancellationToken ct = default);
    Task UpsertDailyKpiAsync(DailyLayerKpi row, CancellationToken ct = default);

    /// <summary>Retention: delete DailySnapshots with SnapshotDate &lt; cutoff. Returns rows deleted.</summary>
    Task<int> DeleteSnapshotsBeforeDateAsync(DateTime cutoff, CancellationToken ct = default);
    /// <summary>Retention: delete ProposedPrices whose run StartedUtc &lt; cutoff (cascades their votes). Returns proposals deleted.</summary>
    Task<int> DeleteProposalsBeforeRunDateAsync(DateTime cutoff, CancellationToken ct = default);
    /// <summary>Retention: delete PricingRuns with StartedUtc &lt; cutoff. Returns runs deleted.</summary>
    Task<int> DeleteRunsBeforeDateAsync(DateTime cutoff, CancellationToken ct = default);
}

/// <summary>
/// Production <see cref="IBulkWriteService"/> backed by <see cref="SqlBulkCopy"/>.
/// Proposal Ids are assigned by the caller and inserted with KeepIdentity so votes can reference
/// them; the identity seed is repaired afterwards via <see cref="ReseedProposedPricesAsync"/>.
/// </summary>
public class BulkWriteService : IBulkWriteService
{
    private const int BatchSize = 10_000;
    private const int CopyTimeoutSeconds = 600;
    private const int RetentionTimeoutSeconds = 1800;

    private readonly string _connectionString;

    public BulkWriteService(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("PricingToolDb")
            ?? throw new InvalidOperationException("Missing connection string 'PricingToolDb'.");
    }

    public async Task<long> GetMaxProposedPriceIdAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = CopyTimeoutSeconds;
        cmd.CommandText = "SELECT ISNULL(MAX(Id), 0) FROM [PricingTool].[ProposedPrices]";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task DeleteSnapshotsForDateAsync(int layerId, DateTime date, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = CopyTimeoutSeconds; // a full-catalog same-date replace deletes ~680k rows
        // Scoped by LayerId: a same-day re-pull of one layer must not wipe another layer's snapshots.
        // Batched so the delete doesn't run as one giant transaction (which timed out / bloated the log).
        cmd.CommandText = @"
DECLARE @rows int = 1;
WHILE @rows > 0
BEGIN
    DELETE TOP (50000) FROM [PricingTool].[DailySnapshots]
    WHERE [LayerId] = @layer AND [SnapshotDate] = @d;
    SET @rows = @@ROWCOUNT;
END";
        var pl = cmd.CreateParameter();
        pl.ParameterName = "@layer";
        pl.DbType = DbType.Int32;
        pl.Value = layerId;
        cmd.Parameters.Add(pl);
        var p = cmd.CreateParameter();
        p.ParameterName = "@d";
        p.DbType = DbType.Date;
        p.Value = date.Date;
        cmd.Parameters.Add(p);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Realigns the identity seed after KeepIdentity inserts so later inserts don't collide.</summary>
    public async Task ReseedProposedPricesAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = CopyTimeoutSeconds;
        // RESEED with no explicit value sets the seed to the current MAX(Id); the next insert is MAX+1.
        cmd.CommandText = "DBCC CHECKIDENT('[PricingTool].[ProposedPrices]', RESEED)";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task BulkInsertSnapshotsAsync(IReadOnlyCollection<DailySnapshot> rows, CancellationToken ct = default)
        => WriteAsync("[PricingTool].[DailySnapshots]", BuildSnapshotTable(rows), SqlBulkCopyOptions.Default, ct);

    /// <summary>Proposals must already have their Id and PricingRunId assigned.</summary>
    public Task BulkInsertProposalsAsync(IReadOnlyCollection<ProposedPrice> proposals, CancellationToken ct = default)
        => WriteAsync("[PricingTool].[ProposedPrices]", BuildProposalTable(proposals), SqlBulkCopyOptions.KeepIdentity, ct);

    /// <summary>Votes must already have their ProposedPriceId assigned; their own Id is identity-generated.</summary>
    public Task BulkInsertVotesAsync(IReadOnlyCollection<AlgorithmVoteRecord> votes, CancellationToken ct = default)
        => WriteAsync("[PricingTool].[AlgorithmVotes]", BuildVoteTable(votes), SqlBulkCopyOptions.Default, ct);

    public async Task DeleteElasticityForLayerAsync(int layerId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = CopyTimeoutSeconds;
        cmd.CommandText = @"
DECLARE @rows int = 1;
WHILE @rows > 0
BEGIN
    DELETE TOP (50000) FROM [PricingTool].[SkuElasticity] WHERE [LayerId] = @layer;
    SET @rows = @@ROWCOUNT;
END";
        var p = cmd.CreateParameter();
        p.ParameterName = "@layer";
        p.DbType = DbType.Int32;
        p.Value = layerId;
        cmd.Parameters.Add(p);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task BulkInsertElasticityAsync(IReadOnlyCollection<SkuElasticity> rows, CancellationToken ct = default)
        => WriteAsync("[PricingTool].[SkuElasticity]", BuildElasticityTable(rows), SqlBulkCopyOptions.Default, ct);

    /// <summary>
    /// Upserts the per-(layer, SKU) dead-stock counters. The caller has already computed each row's
    /// new values, so this is a plain merge-on-natural-key: bulk-copy into a connection-scoped staging
    /// table, then one MERGE updates matches / inserts new SKUs. Never loads the target into memory.
    /// </summary>
    public async Task UpsertDeadStockStateAsync(IReadOnlyCollection<SkuDeadStockState> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using (var create = conn.CreateCommand())
        {
            create.CommandTimeout = CopyTimeoutSeconds;
            create.CommandText = @"
CREATE TABLE #DeadStockStage (
    LayerId int NOT NULL,
    Sku nvarchar(64) NOT NULL,
    ZeroSaleStreakDays int NOT NULL,
    PrevStreakDays int NOT NULL,
    DeadObservedDays int NOT NULL,
    PrevDeadObservedDays int NOT NULL,
    LastSnapshotDate date NOT NULL,
    PrevSnapshotDate date NOT NULL,
    MarkdownTargetPct decimal(9,4) NULL,
    MarkdownCaughtUpStreak int NULL,
    BelowFloorTunnelPrice decimal(18,2) NULL);";
            await create.ExecuteNonQueryAsync(ct);
        }

        using (var bulk = new SqlBulkCopy(conn)
        {
            DestinationTableName = "#DeadStockStage",
            BulkCopyTimeout = CopyTimeoutSeconds,
            BatchSize = BatchSize,
        })
        {
            var table = BuildDeadStockTable(rows);
            foreach (DataColumn col in table.Columns)
                bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            await bulk.WriteToServerAsync(table, ct);
        }

        await using (var merge = conn.CreateCommand())
        {
            merge.CommandTimeout = CopyTimeoutSeconds;
            merge.CommandText = @"
MERGE [PricingTool].[SkuDeadStockStates] AS t
USING #DeadStockStage AS s
    ON t.[LayerId] = s.[LayerId] AND t.[Sku] = s.[Sku]
WHEN MATCHED THEN UPDATE SET
    t.[ZeroSaleStreakDays]     = s.[ZeroSaleStreakDays],
    t.[PrevStreakDays]         = s.[PrevStreakDays],
    t.[DeadObservedDays]       = s.[DeadObservedDays],
    t.[PrevDeadObservedDays]   = s.[PrevDeadObservedDays],
    t.[LastSnapshotDate]       = s.[LastSnapshotDate],
    t.[PrevSnapshotDate]       = s.[PrevSnapshotDate],
    t.[MarkdownTargetPct]      = s.[MarkdownTargetPct],
    t.[MarkdownCaughtUpStreak] = s.[MarkdownCaughtUpStreak],
    t.[BelowFloorTunnelPrice]  = s.[BelowFloorTunnelPrice]
WHEN NOT MATCHED THEN INSERT
    ([LayerId], [Sku], [ZeroSaleStreakDays], [PrevStreakDays], [DeadObservedDays], [PrevDeadObservedDays], [LastSnapshotDate], [PrevSnapshotDate], [MarkdownTargetPct], [MarkdownCaughtUpStreak], [BelowFloorTunnelPrice])
    VALUES (s.[LayerId], s.[Sku], s.[ZeroSaleStreakDays], s.[PrevStreakDays], s.[DeadObservedDays], s.[PrevDeadObservedDays], s.[LastSnapshotDate], s.[PrevSnapshotDate], s.[MarkdownTargetPct], s.[MarkdownCaughtUpStreak], s.[BelowFloorTunnelPrice]);";
            await merge.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Post-run write of the paced dead-stock markdown state (target + period clock) the pricing run
    /// advanced — see <c>DeadStockMarkdownPacer</c>. Matched-only by design: the state row for every
    /// priced SKU was upserted by the snapshot save moments earlier in the same run, so an unmatched
    /// SKU simply hasn't been snapshotted (a crash window) and re-engages on the next run instead.
    /// </summary>
    public async Task UpsertDeadStockMarkdownAsync(
        int layerId, IReadOnlyCollection<SkuMarkdownPacingUpdate> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using (var create = conn.CreateCommand())
        {
            create.CommandTimeout = CopyTimeoutSeconds;
            create.CommandText = @"
CREATE TABLE #MarkdownPacing (
    Sku nvarchar(64) NOT NULL,
    MarkdownTargetPct decimal(9,4) NULL,
    MarkdownCaughtUpStreak int NULL);";
            await create.ExecuteNonQueryAsync(ct);
        }

        using (var bulk = new SqlBulkCopy(conn)
        {
            DestinationTableName = "#MarkdownPacing",
            BulkCopyTimeout = CopyTimeoutSeconds,
            BatchSize = BatchSize,
        })
        {
            var table = BuildMarkdownPacingTable(rows);
            foreach (DataColumn col in table.Columns)
                bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            await bulk.WriteToServerAsync(table, ct);
        }

        await using (var update = conn.CreateCommand())
        {
            update.CommandTimeout = CopyTimeoutSeconds;
            update.CommandText = @"
UPDATE t SET
    t.[MarkdownTargetPct]      = s.[MarkdownTargetPct],
    t.[MarkdownCaughtUpStreak] = s.[MarkdownCaughtUpStreak]
FROM [PricingTool].[SkuDeadStockStates] AS t
JOIN #MarkdownPacing AS s ON t.[Sku] = s.[Sku]
WHERE t.[LayerId] = @layer;";
            var p = update.CreateParameter();
            p.ParameterName = "@layer";
            p.DbType = DbType.Int32;
            p.Value = layerId;
            update.Parameters.Add(p);
            await update.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Post-run write of the dead-stock tunnel provenance marker (see
    /// <c>SkuDeadStockState.BelowFloorTunnelPrice</c>) — the record of which SKUs the dead-stock lane took
    /// below the margin floor, which is what entitles such a price to be HELD once the SKU starts selling
    /// again. Matched-only for the same reason as <see cref="UpsertDeadStockMarkdownAsync"/>: the state row
    /// for every priced SKU was upserted by the snapshot save moments earlier in the same run.
    /// </summary>
    public async Task UpsertDeadStockTunnelPriceAsync(
        int layerId, IReadOnlyCollection<SkuTunnelPriceUpdate> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using (var create = conn.CreateCommand())
        {
            create.CommandTimeout = CopyTimeoutSeconds;
            create.CommandText = @"
CREATE TABLE #TunnelPrice (
    Sku nvarchar(64) NOT NULL,
    BelowFloorTunnelPrice decimal(18,2) NULL);";
            await create.ExecuteNonQueryAsync(ct);
        }

        using (var bulk = new SqlBulkCopy(conn)
        {
            DestinationTableName = "#TunnelPrice",
            BulkCopyTimeout = CopyTimeoutSeconds,
            BatchSize = BatchSize,
        })
        {
            var table = BuildTunnelPriceTable(rows);
            foreach (DataColumn col in table.Columns)
                bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            await bulk.WriteToServerAsync(table, ct);
        }

        await using (var update = conn.CreateCommand())
        {
            update.CommandTimeout = CopyTimeoutSeconds;
            update.CommandText = @"
UPDATE t SET
    t.[BelowFloorTunnelPrice] = s.[BelowFloorTunnelPrice]
FROM [PricingTool].[SkuDeadStockStates] AS t
JOIN #TunnelPrice AS s ON t.[Sku] = s.[Sku]
WHERE t.[LayerId] = @layer;";
            var p = update.CreateParameter();
            p.ParameterName = "@layer";
            p.DbType = DbType.Int32;
            p.Value = layerId;
            update.Parameters.Add(p);
            await update.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Upserts the single per-(layer, date) KPI rollup row for a run. One row, so a plain
    /// parameterized MERGE on the natural key — a same-UTC-date re-pull (which replaces that day's
    /// snapshots) replaces the rollup too, keeping the dashboard in step.
    /// </summary>
    public async Task UpsertDailyKpiAsync(DailyLayerKpi row, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = CopyTimeoutSeconds;
        cmd.CommandText = @"
MERGE [PricingTool].[DailyLayerKpi] AS t
USING (SELECT @layer AS LayerId, @date AS [Date]) AS s
    ON t.[LayerId] = s.[LayerId] AND t.[Date] = s.[Date]
WHEN MATCHED THEN UPDATE SET
    t.[Net7] = @net7, t.[Qty7] = @qty7,
    t.[CostedNet7] = @costedNet7, t.[Cost7] = @cost7, t.[SkuCount] = @skuCount
WHEN NOT MATCHED THEN INSERT
    ([LayerId], [Date], [Net7], [Qty7], [CostedNet7], [Cost7], [SkuCount])
    VALUES (@layer, @date, @net7, @qty7, @costedNet7, @cost7, @skuCount);";
        AddParam(cmd, "@layer", DbType.Int32, row.LayerId);
        AddParam(cmd, "@date", DbType.Date, row.Date.Date);
        AddParam(cmd, "@net7", DbType.Decimal, row.Net7);
        AddParam(cmd, "@qty7", DbType.Int64, row.Qty7);
        AddParam(cmd, "@costedNet7", DbType.Decimal, row.CostedNet7);
        AddParam(cmd, "@cost7", DbType.Decimal, row.Cost7);
        AddParam(cmd, "@skuCount", DbType.Int32, row.SkuCount);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // --- Retention/TTL deletes. Batched (DELETE TOP(50000) loops) so a multi-million-row prune never runs as one
    //     giant transaction; each returns the total rows it deleted (accumulated server-side). Two things are never
    //     deleted, however old: a layer's newest succeeded run and a layer's newest snapshot day. Every page reads
    //     those, the Gj50→Mall sync reads the newest Gj50 run, and a layer that stops running must not go blank. ---

    // The newest succeeded run of each layer. Prepended to the proposal and run deletes.
    private const string KeepNewestRunsSql = @"
DECLARE @keep TABLE ([RunId] bigint PRIMARY KEY);
INSERT INTO @keep ([RunId])
SELECT x.[RunId] FROM (
    SELECT (SELECT MAX(r.[Id]) FROM [PricingTool].[PricingRuns] r WHERE r.[LayerId] = l.[Id] AND r.[Status] IN (1, 2)) AS [RunId]
    FROM [PricingTool].[Layers] l) x
WHERE x.[RunId] IS NOT NULL;
";

    public Task<int> DeleteSnapshotsBeforeDateAsync(DateTime cutoff, CancellationToken ct = default)
        => ExecuteBatchedDeleteAsync(@"
DECLARE @keepDay TABLE ([LayerId] int PRIMARY KEY, [D] date NOT NULL);
INSERT INTO @keepDay ([LayerId], [D])
SELECT x.[LayerId], x.[D] FROM (
    SELECT l.[Id] AS [LayerId], (SELECT MAX(s.[SnapshotDate]) FROM [PricingTool].[DailySnapshots] s WHERE s.[LayerId] = l.[Id]) AS [D]
    FROM [PricingTool].[Layers] l) x
WHERE x.[D] IS NOT NULL;
DECLARE @rows int = 1; DECLARE @total int = 0;
WHILE @rows > 0
BEGIN
    DELETE TOP (50000) s FROM [PricingTool].[DailySnapshots] s
    WHERE s.[SnapshotDate] < @cutoff
      AND NOT EXISTS (SELECT 1 FROM @keepDay k WHERE k.[LayerId] = s.[LayerId] AND k.[D] = s.[SnapshotDate]);
    SET @rows = @@ROWCOUNT; SET @total += @rows;
END
SELECT @total;", ct, ("@cutoff", DbType.Date, cutoff.Date));

    public Task<int> DeleteProposalsBeforeRunDateAsync(DateTime cutoff, CancellationToken ct = default)
        // Run by run, inside each run's Id block (a run's proposals are bulk-inserted as one contiguous block).
        // COUNT(*) beside MIN/MAX forces the index seek (without it the plan is a row-goal scan of the whole
        // table). The run's votes go first, so the ProposedPrice → AlgorithmVotes cascade finds nothing, and
        // LOOP JOIN keeps that cascade a per-row seek: left to itself it merge-joins, i.e. scans every vote on
        // every batch, which made a prod prune ~15x slower. PriceChangeOutcome.ProposedPriceId is a nullable
        // NoAction FK (outcomes outlive their proposal), so referencing outcomes are detached first.
        => ExecuteBatchedDeleteAsync(KeepNewestRunsSql + @"
DECLARE @runs TABLE ([RunId] bigint PRIMARY KEY);
INSERT INTO @runs ([RunId])
SELECT r.[Id] FROM [PricingTool].[PricingRuns] r
WHERE r.[StartedUtc] < @cutoff AND NOT EXISTS (SELECT 1 FROM @keep k WHERE k.[RunId] = r.[Id]);

UPDATE o SET o.[ProposedPriceId] = NULL
FROM [PricingTool].[PriceChangeOutcomes] o
    INNER JOIN [PricingTool].[ProposedPrices] p ON p.[Id] = o.[ProposedPriceId]
WHERE p.[PricingRunId] IN (SELECT [RunId] FROM @runs);

DECLARE @total int = 0, @rows int, @rid bigint, @lo bigint, @hi bigint, @n int;
DECLARE runs CURSOR LOCAL FAST_FORWARD FOR SELECT [RunId] FROM @runs ORDER BY [RunId];
OPEN runs;
FETCH NEXT FROM runs INTO @rid;
WHILE @@FETCH_STATUS = 0
BEGIN
    SELECT @lo = MIN([Id]), @hi = MAX([Id]), @n = COUNT(*) FROM [PricingTool].[ProposedPrices] WHERE [PricingRunId] = @rid;
    IF @n > 0
    BEGIN
        DELETE v FROM [PricingTool].[AlgorithmVotes] v
            INNER JOIN [PricingTool].[ProposedPrices] p ON p.[Id] = v.[ProposedPriceId]
        WHERE v.[ProposedPriceId] BETWEEN @lo AND @hi AND p.[PricingRunId] = @rid;
        SET @rows = 1;
        WHILE @rows > 0
        BEGIN
            DELETE TOP (50000) FROM [PricingTool].[ProposedPrices]
            WHERE [Id] BETWEEN @lo AND @hi AND [PricingRunId] = @rid
            OPTION (LOOP JOIN);
            SET @rows = @@ROWCOUNT; SET @total += @rows;
        END
    END
    FETCH NEXT FROM runs INTO @rid;
END
CLOSE runs; DEALLOCATE runs;
SELECT @total;", ct, ("@cutoff", DbType.DateTime2, cutoff));

    public Task<int> DeleteRunsBeforeDateAsync(DateTime cutoff, CancellationToken ct = default)
        // Deleting a run cascades to its ProposedPrices (and their votes); detach any outcomes first so
        // the cascade doesn't trip the non-cascading PriceChangeOutcome FK. In the orchestrator's
        // proposals→runs order the proposals are already gone, so this is a no-op there, but it keeps the
        // method self-safe if called standalone.
        => ExecuteBatchedDeleteAsync(KeepNewestRunsSql + @"
UPDATE o SET o.[ProposedPriceId] = NULL
FROM [PricingTool].[PriceChangeOutcomes] o
    INNER JOIN [PricingTool].[ProposedPrices] p ON p.[Id] = o.[ProposedPriceId]
    INNER JOIN [PricingTool].[PricingRuns] r ON r.[Id] = p.[PricingRunId]
WHERE r.[StartedUtc] < @cutoff AND NOT EXISTS (SELECT 1 FROM @keep k WHERE k.[RunId] = r.[Id]);
DECLARE @rows int = 1; DECLARE @total int = 0;
WHILE @rows > 0
BEGIN
    DELETE TOP (50000) r FROM [PricingTool].[PricingRuns] r
    WHERE r.[StartedUtc] < @cutoff AND NOT EXISTS (SELECT 1 FROM @keep k WHERE k.[RunId] = r.[Id]);
    SET @rows = @@ROWCOUNT; SET @total += @rows;
END
SELECT @total;", ct, ("@cutoff", DbType.DateTime2, cutoff));

    private async Task<int> ExecuteBatchedDeleteAsync(
        string commandText, CancellationToken ct, params (string Name, DbType Type, object Value)[] parameters)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // Each DELETE commits on its own, so a timeout only stops the pass; the next run carries on from there.
        cmd.CommandTimeout = RetentionTimeoutSeconds;
        cmd.CommandText = commandText;
        foreach (var (name, type, value) in parameters)
            AddParam(cmd, name, type, value);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, DbType type, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.DbType = type;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private async Task WriteAsync(string destination, DataTable table, SqlBulkCopyOptions options, CancellationToken ct)
    {
        if (table.Rows.Count == 0) return;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var bulk = new SqlBulkCopy(conn, options, null)
        {
            DestinationTableName = destination,
            BulkCopyTimeout = CopyTimeoutSeconds,
            BatchSize = BatchSize,
        };
        foreach (DataColumn col in table.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(table, ct);
    }

    // --- DataTable builders. Decimals are rounded to the destination column scale so SqlBulkCopy
    //     (which does not round) never trips on an over-scale value mid-run. The bounded ratio columns
    //     are also kept inside their PRECISION (see RoundOrNull / RoundClamped): SqlBulkCopy fails the
    //     whole copy on one value that doesn't fit, and these are derived from source prices and costs
    //     that are occasionally junk. ---

    /// <summary>Internal (not private) so a test can pin the range guards on the snapshot's ratio columns.</summary>
    internal static DataTable BuildSnapshotTable(IReadOnlyCollection<DailySnapshot> rows)
    {
        var t = new DataTable();
        t.Columns.Add("LayerId", typeof(int));
        t.Columns.Add("SnapshotDate", typeof(DateTime));
        t.Columns.Add("Sku", typeof(string));
        t.Columns.Add("ProductName", typeof(string)).AllowDBNull = true;
        AddNullableDecimals(t, "OldPrice", "AnchorPrice", "CurrentPrice", "CurrentDiscountPct", "Pptcv", "GrossMargin");
        t.Columns.Add("ActiveDiscountIds", typeof(string)).AllowDBNull = true;
        t.Columns.Add("LocalWarehouseStock", typeof(int));
        t.Columns.Add("SupplierWarehouseStock", typeof(int));
        t.Columns.Add("SupplierId", typeof(int)).AllowDBNull = true;
        t.Columns.Add("SupplierName", typeof(string)).AllowDBNull = true;
        t.Columns.Add("BrandId", typeof(int)).AllowDBNull = true;
        t.Columns.Add("Brand", typeof(string)).AllowDBNull = true;
        foreach (var w in new[] { "7", "30", "90" })
        {
            t.Columns.Add("Qty" + w, typeof(int));
            t.Columns.Add("Net" + w, typeof(decimal));
        }

        foreach (var r in rows)
        {
            t.Rows.Add(
                r.LayerId, r.SnapshotDate, r.Sku, Box(r.ProductName),
                Box(Round(r.OldPrice, 2)), Box(Round(r.AnchorPrice, 2)), Box(Round(r.CurrentPrice, 2)),
                Box(RoundOrNull(r.CurrentDiscountPct, 9, 6)), Box(Round(r.Pptcv, 4)), Box(RoundOrNull(r.GrossMargin, 9, 4)),
                Box(r.ActiveDiscountIds),
                r.LocalWarehouseStock, r.SupplierWarehouseStock,
                Box(r.SupplierId), Box(r.SupplierName), Box(r.BrandId), Box(r.Brand),
                r.Qty7, Round(r.Net7, 2),
                r.Qty30, Round(r.Net30, 2),
                r.Qty90, Round(r.Net90, 2));
        }
        return t;
    }

    /// <summary>
    /// Internal (not private) so a test can assert the column list still covers every persisted
    /// ProposedPrice property: a property added to the entity but forgotten here is written as its
    /// CLR default on every run, silently and with green tests.
    /// </summary>
    internal static DataTable BuildProposalTable(IReadOnlyCollection<ProposedPrice> proposals)
    {
        var t = new DataTable();
        t.Columns.Add("Id", typeof(long));
        t.Columns.Add("PricingRunId", typeof(long));
        t.Columns.Add("LayerId", typeof(int));
        t.Columns.Add("Sku", typeof(string));
        t.Columns.Add("ProductId", typeof(int)).AllowDBNull = true;
        t.Columns.Add("ProductName", typeof(string)).AllowDBNull = true;
        t.Columns.Add("Gtin", typeof(string)).AllowDBNull = true;
        t.Columns.Add("PriceBandId", typeof(int)).AllowDBNull = true;
        t.Columns.Add("SupplierId", typeof(int)).AllowDBNull = true;
        t.Columns.Add("SupplierName", typeof(string)).AllowDBNull = true;
        t.Columns.Add("BrandId", typeof(int)).AllowDBNull = true;
        t.Columns.Add("Brand", typeof(string)).AllowDBNull = true;
        AddNullableDecimals(t, "RawWeightedPrice", "Pptcv", "Elasticity", "ElasticityStdError");
        t.Columns.Add("OldPrice", typeof(decimal));
        t.Columns.Add("AnchorPrice", typeof(decimal));
        t.Columns.Add("CurrentPrice", typeof(decimal));
        t.Columns.Add("ActiveDiscountIds", typeof(string)).AllowDBNull = true;
        t.Columns.Add("ProposedPriceValue", typeof(decimal));
        t.Columns.Add("ChangePct", typeof(decimal));
        t.Columns.Add("HasChange", typeof(bool));
        t.Columns.Add("ReasonCodes", typeof(string));
        t.Columns.Add("GuardrailFlags", typeof(string));
        t.Columns.Add("Status", typeof(int));
        t.Columns.Add("DeadStockStage", typeof(byte));
        t.Columns.Add("IsNewProduct", typeof(bool));
        t.Columns.Add("SkipReason", typeof(string)).AllowDBNull = true;
        t.Columns.Add("ReviewedBy", typeof(string)).AllowDBNull = true;
        t.Columns.Add("ReviewedUtc", typeof(DateTime)).AllowDBNull = true;
        t.Columns.Add("PushedUtc", typeof(DateTime)).AllowDBNull = true;

        foreach (var p in proposals)
        {
            t.Rows.Add(
                p.Id, p.PricingRunId, p.LayerId, p.Sku, Box(p.ProductId), Box(p.ProductName), Box(p.Gtin), Box(p.PriceBandId),
                Box(p.SupplierId), Box(p.SupplierName), Box(p.BrandId), Box(p.Brand),
                Box(Round(p.RawWeightedPrice, 4)), Box(Round(p.Pptcv, 4)),
                Box(Round(p.Elasticity, 6)), Box(Round(p.ElasticityStdError, 6)),
                Round(p.OldPrice, 2), Round(p.AnchorPrice, 2), Round(p.CurrentPrice, 2), Box(p.ActiveDiscountIds), Round(p.ProposedPriceValue, 2),
                RoundClamped(p.ChangePct, 9, 4), p.HasChange,
                p.ReasonCodes, p.GuardrailFlags, (int)p.Status, (byte)p.DeadStockStage, p.IsNewProduct,
                Box(p.SkipReason), Box(p.ReviewedBy), Box(p.ReviewedUtc), Box(p.PushedUtc));
        }
        return t;
    }

    private static DataTable BuildVoteTable(IReadOnlyCollection<AlgorithmVoteRecord> votes)
    {
        var t = new DataTable();
        t.Columns.Add("ProposedPriceId", typeof(long));
        t.Columns.Add("AlgorithmCode", typeof(string));
        t.Columns.Add("SuggestedPrice", typeof(decimal));
        t.Columns.Add("Confidence", typeof(decimal));
        t.Columns.Add("BandWeight", typeof(int));
        t.Columns.Add("EffectiveWeight", typeof(decimal));
        t.Columns.Add("ReasonCode", typeof(string));
        t.Columns.Add("ReasonText", typeof(string));

        foreach (var v in votes)
        {
            t.Rows.Add(
                v.ProposedPriceId, v.AlgorithmCode,
                Round(v.SuggestedPrice, 4), Round(v.Confidence, 4),
                v.BandWeight, Round(v.EffectiveWeight, 4),
                v.ReasonCode, v.ReasonText);
        }
        return t;
    }

    private static DataTable BuildElasticityTable(IReadOnlyCollection<SkuElasticity> rows)
    {
        var t = new DataTable();
        t.Columns.Add("LayerId", typeof(int));
        t.Columns.Add("Sku", typeof(string));
        t.Columns.Add("Coefficient", typeof(decimal));
        t.Columns.Add("StandardError", typeof(decimal));
        t.Columns.Add("Intercept", typeof(decimal));
        t.Columns.Add("R2", typeof(decimal));
        t.Columns.Add("ObservationCount", typeof(int));
        t.Columns.Add("DistinctPricePoints", typeof(int));
        t.Columns.Add("PriceCv", typeof(decimal));
        t.Columns.Add("IsUsable", typeof(bool));
        t.Columns.Add("FittedAtUtc", typeof(DateTime));

        foreach (var r in rows)
        {
            t.Rows.Add(
                r.LayerId, r.Sku,
                Round(r.Coefficient, 6), Round(r.StandardError, 6), Round(r.Intercept, 6), Round(r.R2, 6),
                r.ObservationCount, r.DistinctPricePoints, Round(r.PriceCv, 6),
                r.IsUsable, r.FittedAtUtc);
        }
        return t;
    }

    private static DataTable BuildDeadStockTable(IReadOnlyCollection<SkuDeadStockState> rows)
    {
        var t = new DataTable();
        t.Columns.Add("LayerId", typeof(int));
        t.Columns.Add("Sku", typeof(string));
        t.Columns.Add("ZeroSaleStreakDays", typeof(int));
        t.Columns.Add("PrevStreakDays", typeof(int));
        t.Columns.Add("DeadObservedDays", typeof(int));
        t.Columns.Add("PrevDeadObservedDays", typeof(int));
        t.Columns.Add("LastSnapshotDate", typeof(DateTime));
        t.Columns.Add("PrevSnapshotDate", typeof(DateTime));
        t.Columns.Add("MarkdownTargetPct", typeof(decimal)).AllowDBNull = true;
        t.Columns.Add("MarkdownCaughtUpStreak", typeof(int)).AllowDBNull = true;
        t.Columns.Add("BelowFloorTunnelPrice", typeof(decimal)).AllowDBNull = true;

        foreach (var r in rows)
            t.Rows.Add(r.LayerId, r.Sku, r.ZeroSaleStreakDays, r.PrevStreakDays,
                r.DeadObservedDays, r.PrevDeadObservedDays, r.LastSnapshotDate.Date, r.PrevSnapshotDate.Date,
                Box(r.MarkdownTargetPct), Box(r.MarkdownCaughtUpStreak), Box(r.BelowFloorTunnelPrice));
        return t;
    }

    private static DataTable BuildTunnelPriceTable(IReadOnlyCollection<SkuTunnelPriceUpdate> rows)
    {
        var t = new DataTable();
        t.Columns.Add("Sku", typeof(string));
        t.Columns.Add("BelowFloorTunnelPrice", typeof(decimal)).AllowDBNull = true;

        foreach (var r in rows)
            t.Rows.Add(r.Sku, Box(r.TunnelPrice));
        return t;
    }

    private static DataTable BuildMarkdownPacingTable(IReadOnlyCollection<SkuMarkdownPacingUpdate> rows)
    {
        var t = new DataTable();
        t.Columns.Add("Sku", typeof(string));
        t.Columns.Add("MarkdownTargetPct", typeof(decimal)).AllowDBNull = true;
        t.Columns.Add("MarkdownCaughtUpStreak", typeof(int)).AllowDBNull = true;

        foreach (var r in rows)
            t.Rows.Add(r.Sku, Box(r.TargetPct), Box(r.CaughtUpStreak));
        return t;
    }

    private static void AddNullableDecimals(DataTable t, params string[] names)
    {
        foreach (var n in names)
            t.Columns.Add(n, typeof(decimal)).AllowDBNull = true;
    }

    private static decimal Round(decimal value, int scale) => Math.Round(value, scale, MidpointRounding.AwayFromZero);
    private static decimal? Round(decimal? value, int scale) => value is null ? null : Math.Round(value.Value, scale, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Rounds to the column's scale, and writes NULL when the result still doesn't fit a
    /// decimal(<paramref name="precision"/>, <paramref name="scale"/>) column. For the snapshot's source ratios
    /// (discount, gross margin), which only get that large when a price or cost behind them is junk — and a
    /// ratio measured off a junk price means nothing, so NULL is the honest value to keep. Live 2026-09-24:
    /// SKU 15512795mo (a 705,190 MKD display) carried a 464 MKD shelf price on both MK platforms, a
    /// CurrentDiscountPct of −1518.8 against a column that holds ±999.999999, and every MK run failed on it.
    /// </summary>
    private static decimal? RoundOrNull(decimal? value, int precision, int scale)
    {
        if (Round(value, scale) is not decimal rounded) return null;
        return Math.Abs(rounded) <= MaxMagnitude(precision, scale) ? rounded : null;
    }

    /// <summary>
    /// Rounds to the column's scale and clamps into decimal(<paramref name="precision"/>, <paramref name="scale"/>),
    /// for a NOT NULL ratio that can't fall back to NULL. A proposal's ChangePct only leaves its column's range
    /// when the current price is junk (a few cents against a real floor), and a pinned extreme still sorts to the
    /// top of the review, which is where such a row belongs.
    /// </summary>
    private static decimal RoundClamped(decimal value, int precision, int scale)
    {
        var max = MaxMagnitude(precision, scale);
        return Math.Clamp(Round(value, scale), -max, max);
    }

    /// <summary>The largest magnitude a decimal(precision, scale) column holds — (9,6) is 999.999999.</summary>
    private static decimal MaxMagnitude(int precision, int scale)
    {
        var whole = 1m;
        for (var i = 0; i < precision - scale; i++) whole *= 10m;
        return whole - new decimal(1, 0, 0, false, (byte)scale);
    }

    private static object Box(decimal? v) => (object?)v ?? DBNull.Value;
    private static object Box(int? v) => (object?)v ?? DBNull.Value;
    private static object Box(DateTime? v) => (object?)v ?? DBNull.Value;
    private static object Box(string? v) => (object?)v ?? DBNull.Value;
}
