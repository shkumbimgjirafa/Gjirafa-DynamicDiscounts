using Microsoft.EntityFrameworkCore;
using PricingTool.Core.Domain;
using PricingTool.Data.Entities;

namespace PricingTool.Data.Services;

/// <summary>
/// Persists every dataset pull into DailySnapshots (architecture rule 3) and derives the
/// no-movement aging counters the algorithms need from that history.
/// </summary>
public class SnapshotService
{
    private readonly PricingToolDbContext _db;
    private readonly IBulkWriteService _bulk;

    public SnapshotService(PricingToolDbContext db, IBulkWriteService bulk)
    {
        _db = db;
        _bulk = bulk;
    }

    /// <summary>
    /// Stores one day's pull. Re-pulls on the same UTC date replace that date's snapshot
    /// (latest pull wins); proposals keep their own copy of inputs so past runs stay auditable.
    /// Written via <see cref="BulkWriteService"/> — a full-catalog pull is ~680k rows, which EF
    /// cannot insert in reasonable time.
    /// </summary>
    public async Task<int> SaveSnapshotAsync(
        int layerId, IReadOnlyList<SnapshotRow> rows, DateTime snapshotDate, CancellationToken ct = default)
    {
        var date = snapshotDate.Date;

        await _bulk.DeleteSnapshotsForDateAsync(layerId, date, ct);

        var entities = rows.Select(r => new DailySnapshot
        {
            LayerId = layerId,
            SnapshotDate = date,
            Sku = r.Sku,
            ProductName = r.ProductName,
            OldPrice = r.OldPrice,
            AnchorPrice = r.AnchorPrice,
            CurrentPrice = r.CurrentPrice,
            CurrentDiscountPct = r.CurrentDiscountPct,
            ActiveDiscountIds = r.ActiveDiscountIds,
            Pptcv = r.Pptcv,
            GrossMargin = r.GrossMargin,
            LocalWarehouseStock = r.LocalWarehouseStock,
            SupplierWarehouseStock = r.SupplierWarehouseStock,
            SupplierId = r.SupplierId,
            SupplierName = r.SupplierName,
            BrandId = r.BrandId,
            Brand = r.Brand,
            Qty7 = r.Qty7, Net7 = r.Net7,
            Qty30 = r.Qty30, Net30 = r.Net30,
            Qty90 = r.Qty90, Net90 = r.Net90,
        }).ToList();

        await _bulk.BulkInsertSnapshotsAsync(entities, ct);

        // Roll the day's snapshots up into the per-(layer, date) KPI row the dashboard reads. Computed
        // from the rows just inserted (the same four aggregates HomeController used to GROUP BY for),
        // and never retention-trimmed — so the earliest rows preserve the true pre-tool baseline after
        // old snapshots are deleted. Upsert keeps a same-UTC-date re-pull idempotent.
        await _bulk.UpsertDailyKpiAsync(BuildDailyKpi(layerId, date, entities), ct);

        // Runs on its own connection — not in a transaction with the insert above. A crash in between
        // leaves the day's snapshots written but the streak un-advanced; a same-date re-pull self-heals
        // (recompute from PrevStreakDays), otherwise the next new-day run undercounts that one day. This
        // matches the run's existing non-transactional design, and the floor guardrail bounds the impact.
        await UpdateDeadStockCountersAsync(layerId, rows, date, ct);
        return entities.Count;
    }

    /// <summary>
    /// Rolls the day's snapshot rows into the per-(layer, date) KPI summary the dashboard reads. The
    /// four aggregates mirror <c>HomeController</c>'s old GROUP BY exactly: Σ Net7, Σ Qty7, and — over
    /// SKUs whose cost is known — Σ Net7 (costable revenue) and Σ Pptcv·Qty7 (their cost). Each row is
    /// rounded to its persisted column scale (Net7 2dp, Pptcv 4dp) BEFORE summing, so this runtime rollup
    /// is byte-identical to the migration backfill and the old GROUP BY, which sum the already-rounded
    /// values SqlBulkCopy writes (round-then-sum, not sum-then-round — the two differ on sub-cent input).
    /// </summary>
    private static DailyLayerKpi BuildDailyKpi(int layerId, DateTime date, IReadOnlyList<DailySnapshot> entities) => new()
    {
        LayerId = layerId,
        Date = date,
        Net7 = entities.Sum(s => Math.Round(s.Net7, 2)),
        Qty7 = entities.Sum(s => (long)s.Qty7),
        CostedNet7 = entities.Where(s => s.Pptcv != null).Sum(s => Math.Round(s.Net7, 2)),
        Cost7 = entities.Where(s => s.Pptcv != null).Sum(s => Math.Round(s.Pptcv!.Value, 4) * s.Qty7),
        SkuCount = entities.Count,
    };

    /// <summary>
    /// Per-SKU dead-stock state for the pricing run: the zero-sale streak (consecutive most-recent
    /// snapshot days, including the latest, where the SKU's trailing-7d quantity was zero) plus the
    /// persisted markdown pacing fields the paced dead-stock markdown resumes from. Served from the
    /// running <see cref="SkuDeadStockState"/> row (maintained by <see cref="SaveSnapshotAsync"/> and
    /// the pricing run), so this no longer scans 180 days of history into memory.
    ///
    /// NOTE: the streak is measured in CALENDAR DAYS — each transition advances it by the days elapsed
    /// since the last snapshot (see <see cref="NextDeadStockState"/>), so it means the same thing at any
    /// run cadence (24h → +1/run, 15-day → ~+15/run) and consumers may read it directly as "days".
    ///
    /// The counter is unbounded (the old scan's 180-day window was a query artifact, not a rule) —
    /// bounded in practice by the 0.99 cap and the margin-floor / dead-stock-tunnel guardrail. Key is
    /// case-insensitive to match the DB collation, so the orchestrator's lookup works regardless of
    /// source SKU casing.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, DeadStockStateView>> GetDeadStockStatesAsync(
        int layerId, CancellationToken ct = default)
    {
        return await _db.SkuDeadStockStates
            .Where(s => s.LayerId == layerId)
            .AsNoTracking()
            .ToDictionaryAsync(
                s => s.Sku,
                s => new DeadStockStateView(s.ZeroSaleStreakDays, s.MarkdownTargetPct, s.MarkdownCaughtUpStreak, s.DeadObservedDays, s.BelowFloorTunnelPrice),
                StringComparer.OrdinalIgnoreCase, ct);
    }

    /// <summary>
    /// Folds the day's trailing sales into the per-SKU <see cref="SkuDeadStockState"/> counters: a no-sale
    /// gap extends them by the calendar days elapsed since the last snapshot, any sale within the
    /// gap-covering window resets them. Idempotent on a same-UTC-date re-pull (recomputes from the
    /// day-before value rather than double-counting). Only SKUs present in the day's pull are touched.
    /// </summary>
    private async Task UpdateDeadStockCountersAsync(
        int layerId, IReadOnlyList<SnapshotRow> rows, DateTime date, CancellationToken ct)
    {
        // OrdinalIgnoreCase to match SQL Server's case-insensitive (LayerId, Sku) unique index and the
        // MERGE's case-insensitive join: a SKU whose source casing flips between runs must map to the
        // same counter, not be treated as new and reset. The unique index rules out case-only duplicates,
        // so this key can never collide.
        var existing = await _db.SkuDeadStockStates
            .Where(s => s.LayerId == layerId)
            .AsNoTracking()
            .ToDictionaryAsync(s => s.Sku, StringComparer.OrdinalIgnoreCase, ct);

        var updates = new List<SkuDeadStockState>(rows.Count);
        foreach (var r in rows)
        {
            existing.TryGetValue(r.Sku, out var prev);
            updates.Add(NextDeadStockState(layerId, r, date, prev));
        }
        await _bulk.UpsertDeadStockStateAsync(updates, ct);
    }

    /// <summary>Days between two snapshots, clamped to [1, 366] — the amount the aging counters advance
    /// on a no-sale transition. Floor 1 keeps a same-UTC-date step positive; the 366 ceiling bounds a
    /// legitimate long catch-up (engine outage / a SKU absent from the pull for a while) and neutralises a
    /// never-populated PrevSnapshotDate so it can never explode the streak.</summary>
    private static int GapDays(DateTime to, DateTime from) => Math.Clamp((to - from).Days, 1, 366);

    /// <summary>
    /// Trailing-sales window used to decide extend-vs-reset for the no-sale streak. It must cover the whole
    /// gap since the last snapshot, or a sale early in a multi-day gap would be missed (Qty7 only sees the
    /// last 7 days). Snap the elapsed gap up to the smallest source window {7,14,30,60,90} that covers it,
    /// floored at 7 so the daily cadence keeps its historical Qty7 behaviour (15-day cadence → Qty30).
    /// </summary>
    private static int SalesInGapWindow(SnapshotRow r, int gapDays) =>
        SalesWindows.UnitsCovering(gapDays, r.Qty7, r.Qty14, r.Qty30, r.Qty60, r.Qty90);

    /// <summary>
    /// Pure transition for one SKU's aging counters — see <see cref="UpdateDeadStockCountersAsync"/>.
    /// The counters advance by the CALENDAR DAYS elapsed since the last snapshot (not +1 per run), so they
    /// stay in real days at any run cadence: +1 at the 24h cadence, ~+15 at the 15-day cadence. A no-sale
    /// gap extends both counters; any sale within the gap-covering window resets them. The markdown pacing
    /// fields (written by the pricing run) ride along: carried through while the streak lives, cleared when
    /// a sale resets it — a paced markdown is only meaningful during the very streak that justified it.
    /// <see cref="SkuDeadStockState.BelowFloorTunnelPrice"/> is the ONE field a sale must NOT clear (the
    /// guardrail's tunnel hold exists precisely for a below-floor price that started selling again), so it
    /// rides through unconditionally and only the pricing run sets or clears it.
    /// </summary>
    private static SkuDeadStockState NextDeadStockState(
        int layerId, SnapshotRow r, DateTime date, SkuDeadStockState? prev)
    {
        var sku = r.Sku;

        // First time we've seen this SKU. Count today's observation as one day; treat the "previous"
        // snapshot as the day before so a same-UTC-date re-pull recomputes the same value.
        if (prev is null)
        {
            var firstNoSale = r.Qty7 == 0;
            // DeadObservedDays (the age-stage driver) normally starts fresh. But a SKU the tool only STARTS
            // tracking now may already have sat unsold for a long time — a late onboarding, a re-list, or the
            // initial rollout, where "never backfilled" would wrongly park genuinely old stock at Stage 1. So
            // when a first-seen SKU has NO 90-day sales and the WMS shows genuinely old on-hand stock, seed the
            // stage counter from that real stock age instead of 0; ordinary (recently-selling or freshly-seen)
            // SKUs keep the fresh start. PrevDeadObservedDays trails by one so a same-UTC-date re-pull still
            // recomputes the same seed. (ZeroSaleStreakDays — the pacing clock — is intentionally left fresh.)
            var deadSeed = r.Qty90 == 0 && r.OldestUnitAgeDays is int age && age > 1
                ? age
                : (firstNoSale ? 1 : 0);
            return new SkuDeadStockState
            {
                LayerId = layerId, Sku = sku,
                ZeroSaleStreakDays = firstNoSale ? 1 : 0,
                PrevStreakDays = 0,
                DeadObservedDays = deadSeed,
                PrevDeadObservedDays = deadSeed > 0 ? deadSeed - 1 : 0,
                LastSnapshotDate = date,
                PrevSnapshotDate = date.AddDays(-1),
            };
        }

        // Back-dated pull (older than what's already folded in): leave the counter alone.
        if (date < prev.LastSnapshotDate)
            return prev;

        // Same-day re-pull: recompute from the day-before value using the SAME elapsed gap as the run this
        // replaces (from PrevSnapshotDate), so re-runs don't double-count.
        if (date == prev.LastSnapshotDate)
        {
            var elapsedSame = GapDays(date, prev.PrevSnapshotDate);
            var noSaleSame = SalesInGapWindow(r, elapsedSame) == 0;
            return new SkuDeadStockState
            {
                LayerId = layerId, Sku = sku,
                ZeroSaleStreakDays = noSaleSame ? prev.PrevStreakDays + elapsedSame : 0,
                PrevStreakDays = prev.PrevStreakDays,
                DeadObservedDays = noSaleSame ? prev.PrevDeadObservedDays + elapsedSame : 0,
                PrevDeadObservedDays = prev.PrevDeadObservedDays,
                LastSnapshotDate = date,
                PrevSnapshotDate = prev.PrevSnapshotDate,
                MarkdownTargetPct = noSaleSame ? prev.MarkdownTargetPct : null,
                MarkdownCaughtUpStreak = noSaleSame ? prev.MarkdownCaughtUpStreak : null,
                // Tunnel provenance survives a sale by design — see the field's docs. The pricing run owns
                // when it is set and cleared; the counters must only carry it through untouched.
                BelowFloorTunnelPrice = prev.BelowFloorTunnelPrice,
            };
        }

        // New day: advance by the calendar days elapsed since the last snapshot. Yesterday's current
        // becomes "prev". The stage counter tracks the streak's transition exactly — the only difference is
        // its non-backfilled starting point.
        var elapsed = GapDays(date, prev.LastSnapshotDate);
        var noSale = SalesInGapWindow(r, elapsed) == 0;
        return new SkuDeadStockState
        {
            LayerId = layerId, Sku = sku,
            ZeroSaleStreakDays = noSale ? prev.ZeroSaleStreakDays + elapsed : 0,
            PrevStreakDays = prev.ZeroSaleStreakDays,
            DeadObservedDays = noSale ? prev.DeadObservedDays + elapsed : 0,
            PrevDeadObservedDays = prev.DeadObservedDays,
            LastSnapshotDate = date,
            PrevSnapshotDate = prev.LastSnapshotDate,
            MarkdownTargetPct = noSale ? prev.MarkdownTargetPct : null,
            MarkdownCaughtUpStreak = noSale ? prev.MarkdownCaughtUpStreak : null,
            // Tunnel provenance survives a sale by design — see the field's docs. The pricing run owns
            // when it is set and cleared; the counters must only carry it through untouched.
            BelowFloorTunnelPrice = prev.BelowFloorTunnelPrice,
        };
    }
}

/// <summary>
/// The slice of <see cref="SkuDeadStockState"/> a pricing run consumes per SKU: the zero-sale streak
/// (paced-markdown clock), the dead-stock stage counter (<see cref="DeadObservedDays"/>), the
/// paced-markdown fields (<see cref="MarkdownTargetPct"/> as a PERCENT, converted to a fraction on the
/// SkuContext), and the tunnel provenance marker (<see cref="BelowFloorTunnelPrice"/>). A missing
/// dictionary entry yields the struct default — streak 0, observed-dead 0, no active markdown, no tunnel
/// price — which is exactly the fresh-SKU (Stage 1) semantics.
/// </summary>
public readonly record struct DeadStockStateView(
    int Streak, decimal? MarkdownTargetPct, int? MarkdownCaughtUpStreak, int DeadObservedDays, decimal? BelowFloorTunnelPrice);
