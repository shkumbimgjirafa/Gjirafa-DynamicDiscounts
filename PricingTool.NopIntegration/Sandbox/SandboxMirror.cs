using System.Data;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PricingTool.Agent.Operations;
using PricingTool.Agent.Storage;
using PricingTool.Agent.Tools;
using PricingTool.Data;

namespace PricingTool.NopIntegration.Sandbox;

/// <summary>Where the mirror reads the live shop: the read-only replica's shop databases, or in tests a schema standing in
/// for one. <see cref="TablePrefix"/> turns a shop database into the prefix of its tables.</summary>
public sealed record MirrorSource(string ConnectionString, Func<string, string> TablePrefix)
{
    public static MirrorSource Replica(string connectionString) => new(connectionString, db => $"[{db}].dbo.");
}

/// <summary>What one mirror of a layer did, with each replica query's time.</summary>
public sealed record MirrorResult(string Db, long RunId, int Products, int LiveDiscounts, int Inserted, int Updated, int MappingsAdded,
    int ProductsAdded, int FlagsMerged, int EndDated, int TierRefreshed, int TierInserted, int TierOurs, int Deleted,
    IReadOnlyList<(string Query, long Ms)> Replica, long Ms)
{
    public long ReplicaMs => Replica.Sum(q => q.Ms);

    public string Describe() =>
        $"{Db} for run #{RunId}'s {Products:N0} products: {LiveDiscounts:N0} live discounts not ours ({Inserted:N0} new, {Updated:N0} changed), " +
        $"{MappingsAdded:N0} product mappings and {ProductsAdded:N0} products added, {FlagsMerged:N0} discount flags merged, " +
        $"{EndDated:N0} end-dated (left the live shop); regular prices: {TierRefreshed:N0} refreshed, {TierInserted:N0} inserted, " +
        $"{TierOurs:N0} left as the publisher wrote them; {Deleted:N0} mirrored discounts ended over {SandboxMirror.RetentionDays} days ago deleted; " +
        $"replica {ReplicaMs / 1000.0:0.0} s ({string.Join(", ", Replica.Select(q => $"{q.Query} {q.Ms / 1000.0:0.0} s"))}), in all {Ms / 1000.0:0.0} s";
}

/// <summary>
/// Keeps the shop sandbox true to the live shop (Prompt 10 part B), so each day's publish to it measures what would reach the
/// live shelf. For a layer's store and the products of its latest engine run, read from the replica (read-only, NOLOCK, narrow
/// columns, the run's products in a temp table as the engine's own source query does) and streamed into LocalDB:
/// <list type="bullet">
/// <item>the live discounts that aren't ours (live now or starting within <see cref="AheadDays"/> days) are inserted or updated,
/// with their product mappings, the products the sandbox lacks, and the products' discount flags (merged: a store is never
/// dropped). Live ids aren't kept for new rows: the sandbox's own discounts already use ids the live shop will hand out, so
/// <c>MirrorMap</c> maps a live id to its sandbox row (the seeded rows kept their live ids and map to themselves);</item>
/// <item>a mirrored discount that has left the live shop is end-dated as the live shop has it (or now, if it's gone), never deleted;</item>
/// <item>the store's newest regular price is refreshed for every product the publisher hasn't written (no applied, un-rolled-back
/// regular-price audit).</item>
/// </list>
/// Never touches a row named <c>ai-</c> or a regular price the publisher wrote, and writes only to the shop_sandbox_ schemas
/// (refused unless Publisher:Target is Sandbox). Retention: mirrored discounts ended more than <see cref="RetentionDays"/> days ago
/// that no publish audit references are deleted with their mappings (they are copies; the sandbox only needs what can still
/// hold a price).
/// </summary>
public sealed class SandboxMirror : ISandboxMirror
{
    public const int AheadDays = 2, RetentionDays = 14, ReplicaTargetSeconds = 30, DeleteBatch = 20000;
    public const string MapTable = "MirrorMap";
    private const string OurPrefix = "ai-";

    private readonly MirrorSource? _source;
    private readonly PublisherOptions _options;
    private readonly AgentDataSource _tool;
    private readonly AgentDbContext _db;
    private readonly PricingToolDbContext _toolDb;
    private readonly ILogger<SandboxMirror> _logger;
    private readonly TimeProvider _clock;

    public SandboxMirror(IConfiguration config, IOptions<PublisherOptions> options, AgentDataSource tool, AgentDbContext db, PricingToolDbContext toolDb,
        ILogger<SandboxMirror> logger, TimeProvider? clock = null, MirrorSource? source = null)
    {
        var replica = config.GetConnectionString("SourceReadOnly");
        _source = source ?? (string.IsNullOrWhiteSpace(replica) ? null : MirrorSource.Replica(replica));
        _options = options.Value;
        _tool = tool;
        _db = db;
        _toolDb = toolDb;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<(string Status, string Detail)> MirrorAsync(int layerId, long runId, CancellationToken ct = default)
    {
        if (!_options.IsSandbox) return (StepStatus.Skipped, $"Publisher:Target is '{_options.Target}': the mirror only ever writes to the shop sandbox.");
        if (_source is null) return (StepStatus.Skipped, "no replica connection configured (ConnectionStrings:SourceReadOnly): the sandbox isn't mirrored");
        var r = await RunAsync(layerId, runId, ct);
        if (r.Products == 0) return (StepStatus.Warning, $"run #{runId} has no proposals with a product id: nothing mirrored");
        var slow = r.Replica.Where(q => q.Ms > ReplicaTargetSeconds * 1000L).ToList();
        using var self = Process.GetCurrentProcess();
        return (slow.Count > 0 ? StepStatus.Warning : StepStatus.Succeeded,
            r.Describe() + (slow.Count > 0 ? $"; over the {ReplicaTargetSeconds} s target on the replica: {string.Join(", ", slow.Select(q => q.Query))}" : "") +
            $"; this process's working set {self.WorkingSet64 / 1048576:N0} MB, its peak so far {self.PeakWorkingSet64 / 1048576:N0} MB");
    }

    public async Task<MirrorResult> RunAsync(int layerId, long runId, CancellationToken ct = default)
    {
        if (!_options.IsSandbox) throw new InvalidOperationException($"Publisher:Target is '{_options.Target}': the mirror only ever writes to the shop sandbox.");
        var source = _source ?? throw new InvalidOperationException("No replica connection configured (ConnectionStrings:SourceReadOnly).");
        var sw = Stopwatch.StartNew();
        var now = _clock.GetUtcNow().UtcDateTime;
        var layer = await _toolDb.Layers.AsNoTracking().SingleAsync(l => l.Id == layerId, ct);
        var db = layer.OperationalDatabase;
        var shop = _options.ShopFor(db, _tool.ConnectionString);
        var s = shop.Schema;
        if (!s.StartsWith(SandboxSchema.SchemaPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"The mirror writes only to {SandboxSchema.SchemaPrefix}* schemas, not '{s}'.");
        var live = source.TablePrefix(db);
        var columns = SandboxSchema.LoadSnapshot().Where(c => c.Db == db && c.Kind == "column" && c.Table == "Discount" && c.Name is not ("Id" or "AdminComment"))
            .OrderBy(c => c.Ordinal).ToList();
        var cols = columns.Select(c => c.Name).ToList();
        string List(string alias) => string.Join(", ", cols.Select(c => $"{alias}[{c}]"));
        // The same MD5 of a discount's mirrored columns on both sides (the sandbox has the live DDL, so the text is the same).
        string Hash(string alias) => "HASHBYTES('MD5', CONCAT(" + string.Join(", N'|', ", columns.Select(c => c.Type.ToLowerInvariant() switch
        {
            "nvarchar" or "varchar" or "nchar" or "char" => $"ISNULL({alias}[{c.Name}], NCHAR(1))",
            "datetime2" or "datetime" => $"ISNULL(CONVERT(nvarchar(30), {alias}[{c.Name}], 126), NCHAR(1))",
            _ => $"ISNULL(CONVERT(nvarchar(50), {alias}[{c.Name}]), NCHAR(1))",
        })) + "))";

        var products = await _toolDb.ProposedPrices.AsNoTracking().Where(p => p.PricingRunId == runId && p.ProductId != null && p.ProductId > 0)
            .Select(p => p.ProductId!.Value).Distinct().ToListAsync(ct);
        // What the publisher wrote: its regular prices (applied, not rolled back) stay as they are; a discount an audit names is kept.
        var batches = _db.PublishBatches.Where(b => b.Db == db).Select(b => b.Id);
        var ours = await _db.PublishAudits.AsNoTracking()
            .Where(a => batches.Contains(a.BatchId) && a.Applied && a.RolledBackUtc == null && a.ProductId != null
                        && (a.Change == "TierPriceUpdated" || a.Change == "TierPriceInserted"))
            .Select(a => a.ProductId!.Value).Distinct().ToListAsync(ct);
        var referenced = await _db.PublishAudits.AsNoTracking().Where(a => batches.Contains(a.BatchId) && a.DiscountId != null)
            .Select(a => a.DiscountId!.Value).Distinct().ToListAsync(ct);

        await using var local = new SqlConnection(shop.ConnectionString);
        await local.OpenAsync(ct);
        await using var remote = new SqlConnection(source.ConnectionString);
        await remote.OpenAsync(ct);

        await ExecAsync(local, $"""
            IF OBJECT_ID('[{s}].[{MapTable}]') IS NULL
            BEGIN
                CREATE TABLE [{s}].[{MapTable}] (LiveId int NOT NULL CONSTRAINT [PK_{MapTable}] PRIMARY KEY, SandboxId int NOT NULL,
                    FirstSeenUtc datetime2 NOT NULL, LastSeenUtc datetime2 NOT NULL);
                CREATE UNIQUE INDEX [IX_{MapTable}_SandboxId] ON [{s}].[{MapTable}] (SandboxId);
                -- The seeded rows kept their live ids.
                INSERT INTO [{s}].[{MapTable}] (LiveId, SandboxId, FirstSeenUtc, LastSeenUtc)
                SELECT Id, Id, @Now, @Now FROM [{s}].[Discount] WHERE Name NOT LIKE '{OurPrefix}%';
            END
            """, now, ct);
        // Temp tables are made in a batch without parameters: one made inside sp_executesql ends with it.
        await ExecAsync(local, $"""
            SELECT TOP 0 CAST(Id AS int) AS LiveId, {List("")} INTO #md FROM [{s}].[Discount];
            CREATE TABLE #mm (LiveId int NOT NULL, ProductId int NOT NULL);
            SELECT TOP 0 CAST(Id AS int) AS Id, Sku, Deleted, HasDiscountsApplied, HasDiscountsAppliedStoreIds INTO #mp FROM [{s}].[Product];
            SELECT TOP 0 ProductId, Price, OldPrice INTO #mt FROM [{s}].[TierPrice];
            CREATE TABLE #runp (ProductId int NOT NULL PRIMARY KEY);
            CREATE TABLE #ours (ProductId int NOT NULL PRIMARY KEY);
            CREATE TABLE #ref (DiscountId int NOT NULL PRIMARY KEY);
            CREATE TABLE #gl (LiveId int NOT NULL PRIMARY KEY, StartDateUtc datetime2 NULL, EndDateUtc datetime2 NULL, Found bit NOT NULL);
            CREATE TABLE #mh (LiveId int NOT NULL PRIMARY KEY, Hash varbinary(16) NOT NULL);
            """, now, ct);
        await BulkIdsAsync(local, "#runp", products, ct);
        await BulkIdsAsync(local, "#ours", ours, ct);
        await BulkIdsAsync(local, "#ref", referenced, ct);

        // The replica: the run's products in a temp table; the ids of their discounts in the window, found through the mappings
        // (a filter on the whole Discount table took 23 s on GjirafaMall); each discount's hash, and the whole row only where it is
        // new or changed (601,745 whole rows took 25 s to come over); then the mappings, products and regular prices, all streamed
        // straight into LocalDB.
        await ExecAsync(remote, "DROP TABLE IF EXISTS #p, #d, #need; CREATE TABLE #p (ProductId int NOT NULL PRIMARY KEY); " +
                                "CREATE TABLE #d (Id int NOT NULL PRIMARY KEY); CREATE TABLE #need (Id int NOT NULL PRIMARY KEY);", now, ct);
        await BulkIdsAsync(remote, "#p", products, ct);
        var timings = new List<(string, long)>();
        const string Window = "d.StoreId = @Store AND d.Name NOT LIKE 'ai-%' AND (d.EndDateUtc IS NULL OR d.EndDateUtc > @Now) AND (d.StartDateUtc IS NULL OR d.StartDateUtc <= @Ahead)";
        var idsTime = Stopwatch.StartNew();
        await using (var cmd = new SqlCommand($"""
            INSERT INTO #d (Id)
            SELECT DISTINCT dap.Discount_Id FROM {live}Discount_AppliedToProducts dap WITH (NOLOCK)
            JOIN #p ON #p.ProductId = dap.Product_Id
            JOIN {live}Discount d WITH (NOLOCK) ON d.Id = dap.Discount_Id
            WHERE {Window}
            """, remote) { CommandTimeout = 300 })
        {
            cmd.Parameters.AddWithValue("@Store", layer.StoreId);
            AddDates(cmd, now);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        timings.Add(("discount ids", idsTime.ElapsedMilliseconds));
        var liveDiscounts = await StreamAsync(remote, local, "#mh", "discount hashes", timings, $"""
            SELECT d.Id, {Hash("d.")} FROM #d JOIN {live}Discount d WITH (NOLOCK) ON d.Id = #d.Id
            """, layer.StoreId, now, ct);
        var need = new List<int>();
        await using (var cmd = new SqlCommand($"""
            SELECT h.LiveId FROM #mh h
            LEFT JOIN [{s}].[{MapTable}] mm ON mm.LiveId = h.LiveId
            LEFT JOIN [{s}].[Discount] d ON d.Id = mm.SandboxId
            WHERE d.Id IS NULL OR {Hash("d.")} <> h.Hash
            """, local) { CommandTimeout = 600 })
        {
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) need.Add(rd.GetInt32(0));
        }
        if (need.Count > 0)
        {
            await BulkIdsAsync(remote, "#need", need, ct);
            await StreamAsync(remote, local, "#md", "new or changed discounts", timings, $"""
                SELECT d.Id, {List("d.")} FROM #need JOIN {live}Discount d WITH (NOLOCK) ON d.Id = #need.Id
                """, layer.StoreId, now, ct);
        }
        await StreamAsync(remote, local, "#mm", "mappings", timings, $"""
            SELECT dap.Discount_Id, dap.Product_Id FROM #d
            JOIN {live}Discount_AppliedToProducts dap WITH (NOLOCK) ON dap.Discount_Id = #d.Id
            JOIN #p ON #p.ProductId = dap.Product_Id
            """, layer.StoreId, now, ct);
        await StreamAsync(remote, local, "#mp", "products", timings, $"""
            SELECT p.Id, p.Sku, p.Deleted, p.HasDiscountsApplied, p.HasDiscountsAppliedStoreIds FROM {live}Product p WITH (NOLOCK)
            JOIN #p ON #p.ProductId = p.Id
            """, layer.StoreId, now, ct);
        await StreamAsync(remote, local, "#mt", "regular prices", timings, $"""
            SELECT t.ProductId, t.Price, t.OldPrice FROM (
                SELECT tp.ProductId, tp.Price, tp.OldPrice, ROW_NUMBER() OVER (PARTITION BY tp.ProductId ORDER BY tp.Id DESC) AS rn
                FROM {live}TierPrice tp WITH (NOLOCK) JOIN #p ON #p.ProductId = tp.ProductId
                WHERE tp.StoreId = @Store) t
            WHERE t.rn = 1
            """, layer.StoreId, now, ct);

        // Mirrored discounts still live on the sandbox for these products that the live shop no longer has live: how the live shop has them now.
        var gone = new List<int>();
        await using (var cmd = new SqlCommand($"""
            SELECT DISTINCT m.LiveId FROM [{s}].[{MapTable}] m
            JOIN [{s}].[Discount] d ON d.Id = m.SandboxId
            JOIN [{s}].[Discount_AppliedToProducts] dap ON dap.Discount_Id = d.Id
            JOIN #runp r ON r.ProductId = dap.Product_Id
            WHERE {Window} AND NOT EXISTS (SELECT 1 FROM #mh x WHERE x.LiveId = m.LiveId)
            """, local) { CommandTimeout = 600 })
        {
            cmd.Parameters.AddWithValue("@Store", layer.StoreId);
            AddDates(cmd, now);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) gone.Add(rd.GetInt32(0));
        }
        if (gone.Count > 0)
        {
            await ExecAsync(remote, "DROP TABLE IF EXISTS #g; CREATE TABLE #g (LiveId int NOT NULL PRIMARY KEY);", now, ct);
            await BulkIdsAsync(remote, "#g", gone, ct);
            await StreamAsync(remote, local, "#gl", "left the shop", timings, $"""
                SELECT g.LiveId, d.StartDateUtc, d.EndDateUtc, CAST(CASE WHEN d.Id IS NULL THEN 0 ELSE 1 END AS bit)
                FROM #g g LEFT JOIN {live}Discount d WITH (NOLOCK) ON d.Id = g.LiveId
                """, layer.StoreId, now, ct);
        }

        // One transaction for the whole change.
        int inserted, updated, mappings, productsAdded, flags, endDated, tierRefreshed, tierInserted;
        await using (var tx = (SqlTransaction)await local.BeginTransactionAsync(ct))
        {
            updated = await ExecAsync(local, $"""
                UPDATE d SET {string.Join(", ", cols.Select(c => $"[{c}] = m.[{c}]"))}
                FROM [{s}].[Discount] d
                JOIN [{s}].[{MapTable}] mm ON mm.SandboxId = d.Id
                JOIN #md m ON m.LiveId = mm.LiveId
                WHERE d.Name NOT LIKE '{OurPrefix}%' AND EXISTS (SELECT {List("m.")} EXCEPT SELECT {List("d.")});
                """, now, ct, tx);
            await ExecAsync(local, "CREATE TABLE #new (SandboxId int NOT NULL, LiveId int NOT NULL);", now, ct, tx);
            inserted = await ExecAsync(local, $"""
                MERGE [{s}].[Discount] AS t
                USING (SELECT * FROM #md m WHERE NOT EXISTS (SELECT 1 FROM [{s}].[{MapTable}] x WHERE x.LiveId = m.LiveId)) AS src ON 1 = 0
                WHEN NOT MATCHED THEN INSERT ({List("")}) VALUES ({List("src.")})
                OUTPUT inserted.Id, src.LiveId INTO #new (SandboxId, LiveId);
                """, now, ct, tx);
            await ExecAsync(local, $"""
                INSERT INTO [{s}].[{MapTable}] (LiveId, SandboxId, FirstSeenUtc, LastSeenUtc) SELECT LiveId, SandboxId, @Now, @Now FROM #new;
                UPDATE mm SET LastSeenUtc = @Now FROM [{s}].[{MapTable}] mm JOIN #mh m ON m.LiveId = mm.LiveId;
                """, now, ct, tx);
            productsAdded = await ExecAsync(local, $"""
                SET IDENTITY_INSERT [{s}].[Product] ON;
                INSERT INTO [{s}].[Product] (Id, Sku, HasTierPrices, Deleted, HasDiscountsApplied, HasDiscountsAppliedStoreIds)
                SELECT p.Id, p.Sku, 0, p.Deleted, p.HasDiscountsApplied, p.HasDiscountsAppliedStoreIds FROM #mp p
                WHERE NOT EXISTS (SELECT 1 FROM [{s}].[Product] x WHERE x.Id = p.Id);
                SET IDENTITY_INSERT [{s}].[Product] OFF;
                """, now, ct, tx);
            // The flags merge: a store the live shop lists is added, none is ever dropped (the publisher's own store stays).
            flags = await ExecAsync(local, $"""
                UPDATE x SET HasDiscountsApplied = 1, HasDiscountsAppliedStoreIds = u.Ids
                FROM [{s}].[Product] x
                JOIN #mp p ON p.Id = x.Id
                CROSS APPLY (SELECT STRING_AGG(CAST(z.v AS nvarchar(10)), ':') WITHIN GROUP (ORDER BY z.v) AS Ids
                             FROM (SELECT DISTINCT TRY_CAST(value AS int) AS v
                                   FROM STRING_SPLIT(CONCAT(x.HasDiscountsAppliedStoreIds, ':', p.HasDiscountsAppliedStoreIds), ':')
                                   WHERE value <> N'' AND TRY_CAST(value AS int) IS NOT NULL) z) u
                WHERE p.HasDiscountsApplied = 1 AND (x.HasDiscountsApplied = 0 OR ISNULL(x.HasDiscountsAppliedStoreIds, N'') <> ISNULL(u.Ids, N''));
                """, now, ct, tx);
            mappings = await ExecAsync(local, $"""
                INSERT INTO [{s}].[Discount_AppliedToProducts] (Discount_Id, Product_Id)
                SELECT DISTINCT mm.SandboxId, m.ProductId FROM #mm m
                JOIN [{s}].[{MapTable}] mm ON mm.LiveId = m.LiveId
                WHERE EXISTS (SELECT 1 FROM [{s}].[Product] p WHERE p.Id = m.ProductId)
                  AND NOT EXISTS (SELECT 1 FROM [{s}].[Discount_AppliedToProducts] x WHERE x.Discount_Id = mm.SandboxId AND x.Product_Id = m.ProductId);
                """, now, ct, tx);
            endDated = await ExecAsync(local, $"""
                UPDATE d SET StartDateUtc = CASE WHEN g.Found = 1 THEN g.StartDateUtc ELSE d.StartDateUtc END,
                             EndDateUtc = CASE WHEN g.Found = 1 THEN g.EndDateUtc ELSE @Now END
                FROM [{s}].[Discount] d
                JOIN [{s}].[{MapTable}] mm ON mm.SandboxId = d.Id
                JOIN #gl g ON g.LiveId = mm.LiveId
                WHERE d.Name NOT LIKE '{OurPrefix}%'
                  AND (g.Found = 0 OR NOT ((g.EndDateUtc IS NULL OR g.EndDateUtc > @Now) AND (g.StartDateUtc IS NULL OR g.StartDateUtc <= @Ahead)));
                """, now, ct, tx);
            tierRefreshed = await ExecAsync(local, $"""
                UPDATE t SET Price = m.Price, OldPrice = m.OldPrice
                FROM #mt m
                CROSS APPLY (SELECT TOP 1 x.Id FROM [{s}].[TierPrice] x WHERE x.ProductId = m.ProductId AND x.StoreId = @Store ORDER BY x.Id DESC) n
                JOIN [{s}].[TierPrice] t ON t.Id = n.Id
                WHERE NOT EXISTS (SELECT 1 FROM #ours o WHERE o.ProductId = m.ProductId) AND (t.Price <> m.Price OR t.OldPrice <> m.OldPrice);
                """, now, ct, tx, layer.StoreId);
            tierInserted = await ExecAsync(local, $"""
                INSERT INTO [{s}].[TierPrice] (CustomerRoleId, ProductId, StoreId, Quantity, Price, StartDateTimeUtc, EndDateTimeUtc, OldPrice, ProfitMargin)
                SELECT NULL, m.ProductId, @Store, 0, m.Price, NULL, NULL, m.OldPrice, NULL FROM #mt m
                WHERE EXISTS (SELECT 1 FROM [{s}].[Product] p WHERE p.Id = m.ProductId)
                  AND NOT EXISTS (SELECT 1 FROM #ours o WHERE o.ProductId = m.ProductId)
                  AND NOT EXISTS (SELECT 1 FROM [{s}].[TierPrice] x WHERE x.ProductId = m.ProductId AND x.StoreId = @Store);
                """, now, ct, tx, layer.StoreId);
            await ExecAsync(local, $"""
                UPDATE p SET HasTierPrices = 1 FROM [{s}].[Product] p JOIN #mt m ON m.ProductId = p.Id
                WHERE p.HasTierPrices = 0 AND EXISTS (SELECT 1 FROM [{s}].[TierPrice] x WHERE x.ProductId = p.Id);
                """, now, ct, tx, layer.StoreId);
            await tx.CommitAsync(ct);
        }
        var tierOurs = 0;
        await using (var cmd = new SqlCommand("SELECT COUNT(*) FROM #mt m WHERE EXISTS (SELECT 1 FROM #ours o WHERE o.ProductId = m.ProductId)", local))
            tierOurs = (int)(await cmd.ExecuteScalarAsync(ct))!;

        var deleted = await RetireCoreAsync(local, s, RetentionDays, now, ct);

        var result = new MirrorResult(db, runId, products.Count, liveDiscounts, inserted, updated, mappings, productsAdded, flags, endDated,
            tierRefreshed, tierInserted, tierOurs, deleted, timings, sw.ElapsedMilliseconds);
        _logger.LogInformation("Sandbox mirror, layer {Layer}: {Detail}", layerId, result.Describe());
        return result;
    }

    /// <summary>The size valve's trim (<see cref="ISandboxMirror.RetireAsync"/>): the retention with a shorter window.</summary>
    public async Task<int> RetireAsync(int layerId, int days, CancellationToken ct = default)
    {
        if (!_options.IsSandbox) return 0;
        var db = (await _toolDb.Layers.AsNoTracking().SingleAsync(l => l.Id == layerId, ct)).OperationalDatabase;
        var shop = _options.ShopFor(db, _tool.ConnectionString);
        if (!shop.Schema.StartsWith(SandboxSchema.SchemaPrefix, StringComparison.Ordinal)) return 0;
        var batches = _db.PublishBatches.Where(b => b.Db == db).Select(b => b.Id);
        var referenced = await _db.PublishAudits.AsNoTracking().Where(a => batches.Contains(a.BatchId) && a.DiscountId != null)
            .Select(a => a.DiscountId!.Value).Distinct().ToListAsync(ct);
        await using var local = new SqlConnection(shop.ConnectionString);
        await local.OpenAsync(ct);
        var now = _clock.GetUtcNow().UtcDateTime;
        await using (var exists = new SqlCommand($"SELECT OBJECT_ID('[{shop.Schema}].[{MapTable}]')", local))
            if (await exists.ExecuteScalarAsync(ct) is DBNull or null) return 0;
        await ExecAsync(local, "CREATE TABLE #ref (DiscountId int NOT NULL PRIMARY KEY);", now, ct);
        await BulkIdsAsync(local, "#ref", referenced, ct);
        return await RetireCoreAsync(local, shop.Schema, days, now, ct);
    }

    /// <summary>Deletes, in batches, the mirrored copies ended more than <paramref name="days"/> days ago that no audit names
    /// (#ref on the connection), with their mappings and map rows. Never an <c>ai-</c> row.</summary>
    private static async Task<int> RetireCoreAsync(SqlConnection local, string s, int days, DateTime now, CancellationToken ct)
    {
        var deleted = 0;
        while (true)
        {
            await using var tx = (SqlTransaction)await local.BeginTransactionAsync(ct);
            await using var cmd = new SqlCommand($"""
                DROP TABLE IF EXISTS #old;
                SELECT TOP ({DeleteBatch}) mm.SandboxId INTO #old FROM [{s}].[{MapTable}] mm
                JOIN [{s}].[Discount] d ON d.Id = mm.SandboxId
                WHERE d.Name NOT LIKE '{OurPrefix}%' AND d.EndDateUtc < @Cutoff AND NOT EXISTS (SELECT 1 FROM #ref r WHERE r.DiscountId = mm.SandboxId);
                DELETE dap FROM [{s}].[Discount_AppliedToProducts] dap JOIN #old o ON o.SandboxId = dap.Discount_Id;
                DELETE mm FROM [{s}].[{MapTable}] mm JOIN #old o ON o.SandboxId = mm.SandboxId;
                DELETE d FROM [{s}].[Discount] d JOIN #old o ON o.SandboxId = d.Id;
                SELECT COUNT(*) FROM #old;
                """, local, tx) { CommandTimeout = 600 };
            cmd.Parameters.Add("@Cutoff", SqlDbType.DateTime2).Value = now.AddDays(-days);
            var n = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            await tx.CommitAsync(ct);
            deleted += n;
            if (n < DeleteBatch) break;
        }
        return deleted;
    }

    private static void AddDates(SqlCommand cmd, DateTime now)
    {
        cmd.Parameters.Add("@Now", SqlDbType.DateTime2).Value = now;
        cmd.Parameters.Add("@Ahead", SqlDbType.DateTime2).Value = now.AddDays(AheadDays);
        cmd.Parameters.Add("@Cutoff", SqlDbType.DateTime2).Value = now.AddDays(-RetentionDays);
    }

    /// <summary>Runs a batch and returns its row count (or, with <paramref name="scalar"/>, its first value). A batch without parameters
    /// runs as itself, so the temp tables it makes last for the connection.</summary>
    private static async Task<int> ExecAsync(SqlConnection conn, string sql, DateTime now, CancellationToken ct, SqlTransaction? tx = null, int? store = null,
        bool scalar = false)
    {
        await using var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = 600 };
        if (sql.Contains('@')) AddDates(cmd, now);
        if (store is not null) cmd.Parameters.AddWithValue("@Store", store.Value);
        return scalar ? Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) : await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task BulkIdsAsync(SqlConnection conn, string table, IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return;
        var t = new DataTable();
        t.Columns.Add("Id", typeof(int));
        foreach (var id in ids) t.Rows.Add(id);
        using var bulk = new SqlBulkCopy(conn) { DestinationTableName = table, BulkCopyTimeout = 300 };
        await bulk.WriteToServerAsync(t, ct);
    }

    /// <summary>One replica read, streamed row by row into a LocalDB temp table (no rows held in memory); its time is recorded.</summary>
    private static async Task<int> StreamAsync(SqlConnection remote, SqlConnection local, string table, string name, List<(string, long)> timings,
        string sql, int store, DateTime now, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await using var cmd = new SqlCommand(sql, remote) { CommandTimeout = 300 };
        cmd.Parameters.AddWithValue("@Store", store);
        AddDates(cmd, now);
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        using var bulk = new SqlBulkCopy(local, SqlBulkCopyOptions.TableLock, null) { DestinationTableName = table, BulkCopyTimeout = 600, EnableStreaming = true };
        await bulk.WriteToServerAsync(reader, ct);
        timings.Add((name, sw.ElapsedMilliseconds));
        return bulk.RowsCopied;
    }
}
