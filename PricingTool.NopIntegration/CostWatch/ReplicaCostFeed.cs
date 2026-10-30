using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PricingTool.Agent;
using PricingTool.Agent.Storage;
using PricingTool.Core.Services;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;

namespace PricingTool.NopIntegration.CostWatch;

public sealed record CostFeedResult(int Countries, int Refreshed, int SkusRead, int Observations, long ReplicaMs, string? Note);

/// <summary>
/// The cost watcher's feed from the replica (read-only). The replica's ProductPricing is rebuilt in full when costs
/// refresh (on 30 Oct 2026 all 1.7–2.0M rows per country were created between 06:00 and 06:18 UTC), so a watch first
/// reads only MAX(CreatedOnUtc) per country (about 0.1 s). Only after a refresh does it read PPTCV, in one join through
/// a temp table on the ProductCode index, for the SKUs whose price can fall under their floor and matter: in local
/// stock or sold in the last 30 days (supplier stock alone covers nearly every SKU: 507k of 511k on GjirafaMall), with a
/// margin within <see cref="CostWatcherOptions.WatchHeadroomPp"/> of the band floor. The first version read every SKU
/// near its floor with any stock, 397,651 costs in 296 s; this set is about 18k SKUs. A cost that moved from the run's
/// becomes a <see cref="CostObservation"/>.
/// </summary>
public sealed class ReplicaCostFeed
{
    private readonly string? _replica;
    private readonly AgentDbContext _db;
    private readonly PricingToolDbContext _tool;
    private readonly BandConfigProvider _bands;
    private readonly CostWatcherOptions _options;
    private readonly AgentOptions _agent;
    private readonly ILogger<ReplicaCostFeed> _logger;
    private readonly TimeProvider _clock;

    public ReplicaCostFeed(IConfiguration config, AgentDbContext db, PricingToolDbContext tool, BandConfigProvider bands, IOptions<CostWatcherOptions> options,
        IOptions<AgentOptions> agent, ILogger<ReplicaCostFeed> logger, TimeProvider? clock = null)
    {
        _replica = config.GetConnectionString("SourceReadOnly");
        _db = db;
        _tool = tool;
        _bands = bands;
        _options = options.Value;
        _agent = agent.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public static string SettingKey(int countryId) => $"CostWatcher.PricingRefreshedUtc.C{countryId}";

    public async Task<CostFeedResult> PollAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_replica)) return new CostFeedResult(0, 0, 0, 0, 0, "no replica connection configured");
        var sw = Stopwatch.StartNew();
        var layers = await _tool.Layers.AsNoTracking().Where(l => _agent.Cycle.Layers.Contains(l.Id)).ToListAsync(ct);
        int refreshed = 0, read = 0, observations = 0;
        await using var conn = new SqlConnection(_replica);
        await conn.OpenAsync(ct);
        foreach (var country in layers.GroupBy(l => l.TranslationCountryId))
        {
            DateTime? latest;
            await using (var cmd = new SqlCommand("SELECT MAX(CreatedOnUtc) FROM GjirafaTranslations.dbo.ProductPricing WHERE CountryId = @C", conn) { CommandTimeout = 60 })
            {
                cmd.Parameters.AddWithValue("@C", country.Key);
                latest = await cmd.ExecuteScalarAsync(ct) is DateTime d ? d : null;
            }
            var key = SettingKey(country.Key);
            var setting = await _tool.ToolSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
            var seen = setting is null ? (DateTime?)null : DateTime.Parse(setting.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (latest is null || seen is { } s0 && latest <= s0) continue;
            refreshed++;

            // Both KS layers share the country's costs: one read for the union of their watched SKUs.
            var watchedByLayer = new Dictionary<int, Dictionary<string, decimal>>();
            foreach (var layer in country) watchedByLayer[layer.Id] = await WatchedAsync(layer, ct);
            var skus = watchedByLayer.Values.SelectMany(w => w.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var costs = skus.Count == 0 ? new Dictionary<string, decimal>() : await CostsAsync(conn, country.Key, skus, ct);
            read += costs.Count;
            foreach (var (layerId, watched) in watchedByLayer)
                foreach (var (sku, old) in watched)
                {
                    if (!costs.TryGetValue(sku, out var cost) || old <= 0 || Math.Abs(100m * (cost - old) / old) < _options.MinRisePct) continue;
                    _db.CostObservations.Add(new CostObservation { LayerId = layerId, Sku = sku, Pptcv = Math.Round(cost, 4), ObservedUtc = latest.Value, Source = "replica" });
                    observations++;
                }
            if (setting is null)
            {
                setting = new ToolSetting { Key = key };
                _tool.ToolSettings.Add(setting);
            }
            setting.Value = latest.Value.ToString("O", CultureInfo.InvariantCulture);
            setting.UpdatedUtc = _clock.GetUtcNow().UtcDateTime;
            setting.UpdatedBy = CostWatcher.Source;
            await _tool.SaveChangesAsync(ct);
        }
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Cost feed: {Refreshed} countries refreshed, {Read} costs read, {Observations} moved, {Ms} ms", refreshed, read, observations, sw.ElapsedMilliseconds);
        return new CostFeedResult(layers.Select(l => l.TranslationCountryId).Distinct().Count(), refreshed, read, observations, sw.ElapsedMilliseconds, null);
    }

    /// <summary>PPTCV per SKU for one country: the SKUs bulk-copied into a temp table (tempdb works on the read-only
    /// replica), then one join on ProductPricing's (ProductCode, CountryId, PPTCV) index.</summary>
    private static async Task<Dictionary<string, decimal>> CostsAsync(SqlConnection conn, int countryId, IReadOnlyCollection<string> skus, CancellationToken ct)
    {
        await using (var create = new SqlCommand("DROP TABLE IF EXISTS #w; CREATE TABLE #w (Sku nvarchar(400) NOT NULL PRIMARY KEY);", conn))
            await create.ExecuteNonQueryAsync(ct);
        var table = new System.Data.DataTable();
        table.Columns.Add("Sku", typeof(string));
        foreach (var sku in skus) table.Rows.Add(sku);
        using (var bulk = new SqlBulkCopy(conn) { DestinationTableName = "#w", BulkCopyTimeout = 120 })
            await bulk.WriteToServerAsync(table, ct);
        var costs = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new SqlCommand("""
            SELECT pp.ProductCode, pp.PPTCV FROM #w w
            JOIN GjirafaTranslations.dbo.ProductPricing pp ON pp.ProductCode = w.Sku AND pp.CountryId = @C
            """, conn) { CommandTimeout = 120 };
        cmd.Parameters.AddWithValue("@C", countryId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            if (!r.IsDBNull(1)) costs.TryAdd(r.GetString(0), Convert.ToDecimal(r.GetDouble(1)));
        return costs;
    }

    /// <summary>SKU → the latest run's cost, for SKUs in local stock or sold in 30 days whose margin is within the headroom of the band floor.</summary>
    private async Task<Dictionary<string, decimal>> WatchedAsync(Layer layer, CancellationToken ct)
    {
        var run = await _tool.PricingRuns.AsNoTracking()
            .Where(r => r.LayerId == layer.Id && (r.Status == RunStatus.Succeeded || r.Status == RunStatus.SucceededWithErrors))
            .OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);
        if (run is null) return new Dictionary<string, decimal>();
        var date = run.StartedUtc.Date;
        var bands = await _bands.GetBandsAsync(layer.Id, ct);
        var rows = await _tool.DailySnapshots.AsNoTracking()
            .Where(s => s.LayerId == layer.Id && s.SnapshotDate == date && s.Pptcv > 0 && s.CurrentPrice > 0 && (s.LocalWarehouseStock > 0 || s.Qty30 > 0))
            .Select(s => new { s.Sku, Cost = s.Pptcv!.Value, Price = s.CurrentPrice!.Value }).ToListAsync(ct);
        var watched = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var band = BandConfigProvider.FindBand(bands, r.Cost);
            if (band is null) continue;
            var margin = 100m * (r.Price - r.Cost) / r.Price;
            if (margin < band.MarginFloorPct + _options.WatchHeadroomPp) watched.TryAdd(r.Sku, r.Cost);
        }
        return watched;
    }
}
