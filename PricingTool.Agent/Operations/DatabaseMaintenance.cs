using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PricingTool.Agent.Tools;

namespace PricingTool.Agent.Operations;

/// <summary>
/// Keeps the hackathon database healthy for 30 days on LocalDB (10 GB cap): a size check, retention of local
/// engine runs, and a daily backup to a folder outside the repo.
/// </summary>
public sealed class DatabaseMaintenance
{
    private readonly AgentDataSource _source;
    private readonly CycleOptions _options;
    private readonly TimeProvider _clock;

    public DatabaseMaintenance(AgentDataSource source, IOptions<AgentOptions> options, TimeProvider? clock = null)
    {
        _source = source;
        _options = options.Value.Cycle;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Used space of the data file in GB, and whether it is past the warning line.</summary>
    public async Task<(decimal UsedGb, bool Warning)> SizeAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT CAST(SUM(FILEPROPERTY(name, 'SpaceUsed')) * 8.0 / 1024 / 1024 AS decimal(9,2)) FROM sys.database_files WHERE type_desc = 'ROWS'", conn);
        var gb = (decimal)(await cmd.ExecuteScalarAsync(ct))!;
        return (gb, gb >= _options.SizeWarningGb);
    }

    /// <summary>
    /// Deletes the proposals and votes of local engine runs beyond the newest <see cref="CycleOptions.KeepRunsPerLayer"/>
    /// per layer, and the daily snapshots older than the oldest kept run's day. Run rows are kept (their audit), and
    /// the imported prod runs carry no local proposals, so they are never touched. Batched like the tool's own
    /// retention: votes first, outcomes detached, 50k rows per delete.
    /// </summary>
    public async Task<(int Proposals, int Snapshots)> RetentionAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SET NOCOUNT ON;
            DECLARE @withProposals TABLE (RunId bigint PRIMARY KEY, LayerId int, StartedUtc datetime2, rn int);
            INSERT INTO @withProposals
            SELECT r.Id, r.LayerId, r.StartedUtc, ROW_NUMBER() OVER (PARTITION BY r.LayerId ORDER BY r.Id DESC)
            FROM PricingTool.PricingRuns r
            WHERE EXISTS (SELECT 1 FROM PricingTool.ProposedPrices p WHERE p.PricingRunId = r.Id);

            DECLARE @drop TABLE (RunId bigint PRIMARY KEY);
            INSERT INTO @drop SELECT RunId FROM @withProposals WHERE rn > @Keep;

            UPDATE o SET o.ProposedPriceId = NULL
            FROM PricingTool.PriceChangeOutcomes o JOIN PricingTool.ProposedPrices p ON p.Id = o.ProposedPriceId
            WHERE p.PricingRunId IN (SELECT RunId FROM @drop);

            DECLARE @total int = 0, @rows int = 1;
            WHILE @rows > 0
            BEGIN
                DELETE TOP (50000) v FROM PricingTool.AlgorithmVotes v
                JOIN PricingTool.ProposedPrices p ON p.Id = v.ProposedPriceId
                WHERE p.PricingRunId IN (SELECT RunId FROM @drop);
                SET @rows = @@ROWCOUNT;
            END
            SET @rows = 1;
            WHILE @rows > 0
            BEGIN
                DELETE TOP (50000) FROM PricingTool.ProposedPrices WHERE PricingRunId IN (SELECT RunId FROM @drop);
                SET @rows = @@ROWCOUNT; SET @total += @rows;
            END

            DECLARE @snaps int = 0;
            SET @rows = 1;
            WHILE @rows > 0
            BEGIN
                DELETE TOP (50000) s FROM PricingTool.DailySnapshots s
                WHERE s.SnapshotDate < (SELECT MIN(CAST(k.StartedUtc AS date)) FROM @withProposals k WHERE k.LayerId = s.LayerId AND k.rn <= @Keep);
                SET @rows = @@ROWCOUNT; SET @snaps += @rows;
            END
            SELECT @total, @snaps;
            """, conn) { CommandTimeout = 1800 };
        cmd.Parameters.AddWithValue("@Keep", _options.KeepRunsPerLayer);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetInt32(0), r.GetInt32(1));
    }

    /// <summary>Publish exceptions are kept this many days: the inbox and the weekly report read 7.</summary>
    public const int ExceptionDays = 14;

    /// <summary>
    /// Deletes publish exceptions older than <see cref="ExceptionDays"/> days, 50k rows per delete. The same SKUs come back every
    /// day (73k rows a day on 31 Oct, about 13 MB), which alone would take the 30-day projection to the 8 GB line; each batch keeps
    /// its own counts on its row.
    /// </summary>
    public async Task<int> ExceptionRetentionAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SET NOCOUNT ON;
            DECLARE @total int = 0, @rows int = 1;
            WHILE @rows > 0
            BEGIN
                DELETE TOP (50000) FROM ai.PublishException WHERE CreatedUtc < @Cutoff;
                SET @rows = @@ROWCOUNT; SET @total += @rows;
            END
            SELECT @total;
            """, conn) { CommandTimeout = 1800 };
        cmd.Parameters.AddWithValue("@Cutoff", _clock.GetUtcNow().UtcDateTime.AddDays(-ExceptionDays));
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>A full backup with checksum into the backup folder; keeps the newest <see cref="CycleOptions.KeepBackups"/>.</summary>
    public async Task<(string Path, long Bytes, int Removed)> BackupAsync(CancellationToken ct = default)
    {
        var dir = _options.BackupDir();
        Directory.CreateDirectory(dir);
        await using var conn = await OpenAsync(ct);
        var db = conn.Database;
        var path = System.IO.Path.Combine(dir, $"{db}-{_clock.GetLocalNow():yyyyMMdd-HHmmss}.bak");
        await using (var cmd = new SqlCommand($"BACKUP DATABASE [{db}] TO DISK = @Path WITH INIT, CHECKSUM, NAME = @Name", conn) { CommandTimeout = 3600 })
        {
            cmd.Parameters.AddWithValue("@Path", path);
            cmd.Parameters.AddWithValue("@Name", $"{db} daily {_clock.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
            await cmd.ExecuteNonQueryAsync(ct);
        }
        var old = new DirectoryInfo(dir).GetFiles($"{db}-*.bak").OrderByDescending(f => f.Name).Skip(_options.KeepBackups).ToList();
        foreach (var f in old) f.Delete();
        return (path, new FileInfo(path).Length, old.Count);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(_source.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
