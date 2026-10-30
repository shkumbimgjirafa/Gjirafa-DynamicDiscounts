using Microsoft.EntityFrameworkCore;

namespace PricingTool.Data.Services;

/// <summary>
/// The clustered-key (Id) range of one run's proposals and of one layer-day's snapshot. Both are
/// bulk-inserted in one go, so each is a contiguous block of Ids; bounding a whole-run or whole-day read by
/// its block lets SQL Server read it in one sequential pass instead of a key lookup per row (neither table
/// has an index covering the prices/units these views read). Callers keep their original predicates as well,
/// so the bound only ever narrows the scan — correctness never depends on the block being contiguous.
/// </summary>
/// <remarks>
/// The <see cref="IdRange.Rows"/> count is load-bearing, not decoration. A bare MIN/MAX of the clustered key
/// lets SQL Server answer each with a TOP(1) ordered scan of the clustered index, betting the block's rows are
/// spread evenly through the table. They aren't — a run is one block, and the newest sits at the very end — so
/// finding MIN walks every older row first: 24–30s on prod's 137M snapshots / 167M proposals, which timed out
/// the Price Bands page. Asking for COUNT(*) as well means every row of the block must be read, so the plan
/// seeks the (PricingRunId, Sku) / (LayerId, SnapshotDate, Sku) index instead: ~0.1s.
/// </remarks>
public static class BlockRanges
{
    public sealed record IdRange(long Min, long Max, int Rows);

    public static Task<IdRange?> ProposalsAsync(PricingToolDbContext db, long runId, CancellationToken ct = default) =>
        db.ProposedPrices.AsNoTracking()
            .Where(p => p.PricingRunId == runId)
            .GroupBy(p => p.PricingRunId)
            .Select(g => new IdRange(g.Min(p => p.Id), g.Max(p => p.Id), g.Count()))
            .FirstOrDefaultAsync(ct);

    public static Task<IdRange?> SnapshotAsync(PricingToolDbContext db, int layerId, DateTime snapshotDate, CancellationToken ct = default) =>
        db.DailySnapshots.AsNoTracking()
            .Where(s => s.LayerId == layerId && s.SnapshotDate == snapshotDate)
            .GroupBy(s => new { s.LayerId, s.SnapshotDate })
            .Select(g => new IdRange(g.Min(s => s.Id), g.Max(s => s.Id), g.Count()))
            .FirstOrDefaultAsync(ct);
}
