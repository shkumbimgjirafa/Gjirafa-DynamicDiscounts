using Microsoft.EntityFrameworkCore;
using PricingTool.Core.Domain;
using PricingTool.Data;
using PricingTool.Data.Services;

namespace PricingTool.Tests;

/// <summary>
/// The 15-day (360h) cadence: the scheduler gate must space runs a true cadence apart (anchored on the
/// last run, not re-based on "today"), and the dead-stock aging counters must advance in calendar days so
/// a slower cadence doesn't slow the markdown/stage progression.
/// </summary>
public class CadenceTests
{
    private static PricingToolDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PricingToolDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ScheduleInfo Info(int cadenceHours, DateTime? lastRun, int hour = 3) =>
        new(new TimeOnly(hour, 0), cadenceHours, lastRun);

    // ---- Scheduler gate (ScheduleService.ComputeNextRun) --------------------------------------------

    [Fact]
    public void FifteenDayCadence_AnchoredOnLastRun_NextIsFifteenDaysLater()
    {
        // The 2026-07-17 anchor: next scheduled run must be 2026-08-01 at the run time, not ~2 days later
        // (the old now.Date-anchored grid collapsed any cadence above 24h to roughly daily).
        var info = Info(360, new DateTime(2026, 7, 17, 3, 0, 0));
        var next = ScheduleService.ComputeNextRun(info, new DateTime(2026, 7, 20, 12, 0, 0));
        Assert.Equal(new DateTime(2026, 8, 1, 3, 0, 0), next);
    }

    [Fact]
    public void FifteenDayCadence_NotDueBeforeSlot_DueOnSlot()
    {
        var info = Info(360, new DateTime(2026, 7, 17, 3, 0, 0));

        Assert.Null(ScheduleService.DueSlot(info, new DateTime(2026, 7, 20, 12, 0, 0)));  // 3 days after anchor
        Assert.Null(ScheduleService.DueSlot(info, new DateTime(2026, 7, 31, 23, 59, 0))); // day before the slot
        Assert.Null(ScheduleService.DueSlot(info, new DateTime(2026, 8, 1, 2, 59, 0)));   // slot day, before run time
        // Exactly the slot: due, and the served slot is 8/1 03:00.
        Assert.Equal(new DateTime(2026, 8, 1, 3, 0, 0), ScheduleService.DueSlot(info, new DateTime(2026, 8, 1, 3, 0, 0)));
        Assert.Equal(new DateTime(2026, 8, 1, 3, 0, 0), ScheduleService.DueSlot(info, new DateTime(2026, 8, 1, 6, 0, 0)));
    }

    [Fact]
    public void DailyCadence_StillSpacesOneDayApart_Regression()
    {
        var info = Info(24, new DateTime(2026, 7, 17, 3, 0, 0));
        Assert.Equal(new DateTime(2026, 7, 18, 3, 0, 0), ScheduleService.ComputeNextRun(info, new DateTime(2026, 7, 17, 6, 0, 0)));
        Assert.Null(ScheduleService.DueSlot(info, new DateTime(2026, 7, 18, 2, 59, 0)));                       // not yet
        Assert.Equal(new DateTime(2026, 7, 18, 3, 0, 0), ScheduleService.DueSlot(info, new DateTime(2026, 7, 18, 3, 0, 0)));
    }

    [Fact]
    public void LongOutage_ProducesSingleCatchUp_ServingTheCurrentPeriod()
    {
        // Down for ~7 weeks (missed several 15-day slots). One catch-up fires, serving the CURRENT period's
        // slot (7/16), then it is NOT due again until the next slot (7/31) — no run-per-missed-slot storm.
        var stale = Info(360, new DateTime(2026, 6, 1, 3, 0, 0));
        var now = new DateTime(2026, 7, 20, 12, 0, 0);
        var slot = ScheduleService.DueSlot(stale, now);
        Assert.Equal(new DateTime(2026, 7, 16, 3, 0, 0), slot); // 6/1 + 3·15d = 7/16, the most recent slot <= now

        // After stamping that slot, the next tick the same day is NOT due; next run is a full cadence out.
        var afterStamp = Info(360, slot);
        Assert.Null(ScheduleService.DueSlot(afterStamp, now));
        Assert.Equal(new DateTime(2026, 7, 31, 3, 0, 0), ScheduleService.ComputeNextRun(afterStamp, now));
    }

    [Fact]
    public void CatchUpBeforeRunTime_DoesNotDoubleRunSameDay()
    {
        // Regression for the outage double-run: the engine recovers at 00:30 (BEFORE the 03:00 run time) on
        // a day whose 8/1 slot was missed. The catch-up must serve the 8/1 slot exactly once and NOT fire a
        // second run at 03:00 the same day.
        var info = Info(360, new DateTime(2026, 7, 17, 3, 0, 0));
        var recover = new DateTime(2026, 8, 2, 0, 30, 0);
        var slot = ScheduleService.DueSlot(info, recover);
        Assert.Equal(new DateTime(2026, 8, 1, 3, 0, 0), slot);   // serves the missed 8/1 slot, on-grid

        var afterStamp = Info(360, slot);
        Assert.Null(ScheduleService.DueSlot(afterStamp, recover));                                  // 00:30: not due again
        Assert.Null(ScheduleService.DueSlot(afterStamp, new DateTime(2026, 8, 2, 3, 0, 0)));        // 03:00 same day: STILL not due
        Assert.Equal(new DateTime(2026, 8, 16, 3, 0, 0), ScheduleService.ComputeNextRun(afterStamp, recover)); // next is a full cadence out
    }

    [Fact]
    public void NeverRun_IsDueImmediately_AndAnchorsOnRunTimeSlot()
    {
        var info = Info(360, lastRun: null);
        var now = new DateTime(2026, 7, 20, 12, 0, 0);
        var slot = ScheduleService.DueSlot(info, now);
        Assert.Equal(new DateTime(2026, 7, 20, 3, 0, 0), slot); // due now, anchored on the most recent run-time slot
        // Once stamped, the next run is one cadence past that anchor.
        Assert.Equal(new DateTime(2026, 8, 4, 3, 0, 0), ScheduleService.ComputeNextRun(Info(360, slot), now));
    }

    [Fact]
    public void RunTimeChange_ShiftsTheNextSlotToTheNewRunTime()
    {
        // A layer last ran at 05:00; a Manager moves the run time to 03:00. The next slot follows the NEW
        // run time (03:00), not the old anchored 05:00 — RunTimeUtc changes take effect (the time-of-day is
        // driven by RunTimeUtc, not frozen on the last run's timestamp). No spurious same-day run.
        var info = Info(360, new DateTime(2026, 7, 17, 5, 0, 0), hour: 3);
        Assert.Equal(new DateTime(2026, 8, 1, 3, 0, 0), ScheduleService.ComputeNextRun(info, new DateTime(2026, 7, 20, 12, 0, 0)));
        Assert.Null(ScheduleService.DueSlot(info, new DateTime(2026, 7, 20, 12, 0, 0)));
    }

    // ---- Calendar-day aging counters (SnapshotService.NextDeadStockState) ---------------------------

    private static SnapshotRow Row(string sku, int qty7, int qty14 = 0, int qty30 = 0, int qty60 = 0, int qty90 = 0, int? oldestUnitAgeDays = null)
        => new() { Sku = sku, Qty7 = qty7, Qty14 = qty14, Qty30 = qty30, Qty60 = qty60, Qty90 = qty90, OldestUnitAgeDays = oldestUnitAgeDays };

    private static async Task<int> SeedLayerAsync(PricingToolDbContext db)
    {
        var layer = new PricingTool.Data.Entities.Layer
        {
            Brand = "GjirafaMall", CountryCode = "KS", DisplayName = "Test — KS",
            OperationalDatabase = "GjirafaMall", StoreId = 2, TranslationCountryId = 1, WarehouseStoreId = 2,
            Currency = "EUR", RunTimeUtc = "03:00", CadenceHours = 24, IsActive = true, SortOrder = 0,
        };
        db.Layers.Add(layer);
        await db.SaveChangesAsync();
        return layer.Id;
    }

    [Fact]
    public async Task FifteenDayGap_AdvancesCountersByFifteenCalendarDays()
    {
        using var db = NewDb();
        var layerId = await SeedLayerAsync(db);
        var snapshots = new SnapshotService(db, new EfBulkWriter(db));

        var d0 = new DateTime(2026, 7, 17);
        await snapshots.SaveSnapshotAsync(layerId, new[] { Row("SKU-D", 0) }, d0);      // first: streak 1
        var d1 = d0.AddDays(15);
        await snapshots.SaveSnapshotAsync(layerId, new[] { Row("SKU-D", 0) }, d1);      // +15 → 16

        var state = await db.SkuDeadStockStates.SingleAsync(s => s.Sku == "SKU-D");
        Assert.Equal(16, state.ZeroSaleStreakDays);
        Assert.Equal(16, state.DeadObservedDays);
        Assert.Equal(d0, state.PrevSnapshotDate); // the snapshot before the latest
    }

    [Fact]
    public async Task FifteenDayGap_SaleWithinGapWindow_ResetsStreak()
    {
        using var db = NewDb();
        var layerId = await SeedLayerAsync(db);
        var snapshots = new SnapshotService(db, new EfBulkWriter(db));

        var d0 = new DateTime(2026, 7, 17);
        await snapshots.SaveSnapshotAsync(layerId, new[] { Row("SKU-D", 0) }, d0);       // streak 1
        // Next run 15 days later: no sale in the last 7 days (Qty7 == 0) but a sale within the gap-covering
        // 30-day window (Qty30 == 3). The widened reset window must catch it and reset the streak.
        var d1 = d0.AddDays(15);
        await snapshots.SaveSnapshotAsync(layerId, new[] { Row("SKU-D", 0, qty14: 0, qty30: 3, qty60: 3, qty90: 3) }, d1);

        var state = await db.SkuDeadStockStates.SingleAsync(s => s.Sku == "SKU-D");
        Assert.Equal(0, state.ZeroSaleStreakDays);
        Assert.Equal(0, state.DeadObservedDays);
    }

    [Fact]
    public async Task FirstSeen_StaleStock_SeedsDeadObservedFromStockAge()
    {
        using var db = NewDb();
        var layerId = await SeedLayerAsync(db);
        var snapshots = new SnapshotService(db, new EfBulkWriter(db));

        // First time the tool sees this SKU: no 90-day sales and WMS says the stock has sat 400 days. The
        // stage counter must seed from that real age (Stage 3), not reset to Stage 1; the pacing clock stays fresh.
        var d0 = new DateTime(2026, 7, 17);
        await snapshots.SaveSnapshotAsync(layerId, new[] { Row("SKU-OLD", 0, oldestUnitAgeDays: 400) }, d0);

        var state = await db.SkuDeadStockStates.SingleAsync(s => s.Sku == "SKU-OLD");
        Assert.Equal(400, state.DeadObservedDays);
        Assert.Equal(399, state.PrevDeadObservedDays); // trails by one so a same-date re-pull is idempotent
        Assert.Equal(1, state.ZeroSaleStreakDays);     // pacing clock is intentionally left fresh
    }

    [Fact]
    public async Task FirstSeen_StaleStock_SameUtcDateRepull_KeepsSeed()
    {
        using var db = NewDb();
        var layerId = await SeedLayerAsync(db);
        var snapshots = new SnapshotService(db, new EfBulkWriter(db));

        var d0 = new DateTime(2026, 7, 17);
        await snapshots.SaveSnapshotAsync(layerId, new[] { Row("SKU-OLD", 0, oldestUnitAgeDays: 400) }, d0);
        await snapshots.SaveSnapshotAsync(layerId, new[] { Row("SKU-OLD", 0, oldestUnitAgeDays: 400) }, d0); // re-pull

        var state = await db.SkuDeadStockStates.SingleAsync(s => s.Sku == "SKU-OLD");
        Assert.Equal(400, state.DeadObservedDays);
    }

    [Fact]
    public async Task FirstSeen_RecentlySold_DoesNotSeedFromStockAge()
    {
        using var db = NewDb();
        var layerId = await SeedLayerAsync(db);
        var snapshots = new SnapshotService(db, new EfBulkWriter(db));

        // Old stock on hand (400d) but it HAS sold within 90 days → not dead, so the stage counter must NOT
        // be seeded from stock age; it stays at the fresh first-no-sale value.
        var d0 = new DateTime(2026, 7, 17);
        await snapshots.SaveSnapshotAsync(layerId, new[] { Row("SKU-SELLS", 0, qty90: 5, oldestUnitAgeDays: 400) }, d0);

        var state = await db.SkuDeadStockStates.SingleAsync(s => s.Sku == "SKU-SELLS");
        Assert.Equal(1, state.DeadObservedDays);
    }

    [Fact]
    public async Task SameUtcDateRepull_AtFifteenDayCadence_IsIdempotent()
    {
        using var db = NewDb();
        var layerId = await SeedLayerAsync(db);
        var snapshots = new SnapshotService(db, new EfBulkWriter(db));

        var d0 = new DateTime(2026, 7, 17);
        await snapshots.SaveSnapshotAsync(layerId, new[] { Row("SKU-D", 0) }, d0);       // streak 1
        var d1 = d0.AddDays(15);
        await snapshots.SaveSnapshotAsync(layerId, new[] { Row("SKU-D", 0) }, d1);       // +15 → 16
        // A "Run now" on the same UTC date must recompute the same 15-day step, not add another 15.
        await snapshots.SaveSnapshotAsync(layerId, new[] { Row("SKU-D", 0) }, d1);

        var state = await db.SkuDeadStockStates.SingleAsync(s => s.Sku == "SKU-D");
        Assert.Equal(16, state.ZeroSaleStreakDays);
    }
}
