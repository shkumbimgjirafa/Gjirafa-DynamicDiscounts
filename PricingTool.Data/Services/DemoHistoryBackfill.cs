using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PricingTool.Core.Demo;

namespace PricingTool.Data.Services;

/// <summary>
/// Demo mode only: backfills DailySnapshots with the generator's history (default 35 days)
/// so the dashboard trends, baseline comparison and aging streaks all have data on first boot.
/// </summary>
public class DemoHistoryBackfill
{
    private readonly PricingToolDbContext _db;
    private readonly SnapshotService _snapshots;
    private readonly ILogger<DemoHistoryBackfill> _logger;

    public DemoHistoryBackfill(PricingToolDbContext db, SnapshotService snapshots, ILogger<DemoHistoryBackfill> logger)
    {
        _db = db;
        _snapshots = snapshots;
        _logger = logger;
    }

    public async Task EnsureBackfilledAsync(int days = 35, CancellationToken ct = default)
    {
        var generator = new DemoDataGenerator();
        var today = DateTime.UtcNow.Date;

        // Backfill every active layer (same synthetic catalog) so each layer's dashboard has data.
        var layers = await _db.Layers.AsNoTracking()
            .Where(l => l.IsActive).OrderBy(l => l.SortOrder).ToListAsync(ct);

        foreach (var layer in layers)
        {
            if (await _db.DailySnapshots.AnyAsync(s => s.LayerId == layer.Id, ct)) continue;

            for (var offset = days; offset >= 1; offset--)
            {
                var date = today.AddDays(-offset);
                var rows = generator.Generate(date);
                await _snapshots.SaveSnapshotAsync(layer.Id, rows, date, ct);
            }
        }

        _logger.LogInformation("Demo mode: backfilled {Days} days of snapshot history for {Count} layer(s).", days, layers.Count);
    }
}
