using Microsoft.EntityFrameworkCore;
using PricingTool.Core.Domain;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;

namespace PricingTool.Tests;

/// <summary>
/// Dead-stock history: spells (<see cref="DeadStockEpisode"/>) opened, carried and closed with a logged reason,
/// the per-country daily pile (<see cref="DeadStockDailyKpi"/>), and the guarantee the backfill relies on — a
/// replay of old run-days produces exactly what the live runs would have written.
/// </summary>
public class DeadStockEpisodeTests
{
    private static readonly DateTime D1 = new(2026, 9, 1);

    private static PricingToolDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PricingToolDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Dictionary<string, DeadSkuToday> Dead(params (string Sku, DeadStockStage Stage, int Stock)[] skus) =>
        skus.ToDictionary(s => s.Sku, s => new DeadSkuToday(s.Stage, s.Stock, 10m, 25m), StringComparer.OrdinalIgnoreCase);

    private static Func<string, DeadStockPullFacts?> Pull(Dictionary<string, DeadStockPullFacts> facts) =>
        sku => facts.TryGetValue(sku, out var f) ? f : null;

    private static DeadStockPullFacts Facts(int stock, int qty7 = 0, int qty14 = 0, int qty30 = 0, int qty60 = 0, int qty90 = 0) =>
        new(stock, qty7, qty14, qty30, qty60, qty90);

    private static readonly Func<string, DeadStockPullFacts?> NoPull = _ => null;

    // ---- spells -----------------------------------------------------------------------------------

    [Fact]
    public void Apply_OpensASpellOnTheFirstDeadDay_AndCarriesIt()
    {
        var (added, _) = DeadStockEpisodeService.Apply(1, D1, new List<DeadStockEpisode>(), Dead(("A", DeadStockStage.Stage1, 4)), NoPull);
        var spell = Assert.Single(added);
        Assert.Equal(D1, spell.EnteredDate);
        Assert.Equal(4, spell.EntryLocalStock);
        Assert.Null(spell.ExitedDate);

        var (added2, removed2) = DeadStockEpisodeService.Apply(1, D1.AddDays(3), new List<DeadStockEpisode> { spell },
            Dead(("A", DeadStockStage.Stage2, 3)), NoPull);
        Assert.Empty(added2);
        Assert.Empty(removed2);
        Assert.Equal(D1, spell.EnteredDate);
        Assert.Equal(D1.AddDays(3), spell.LastSeenDate);
        Assert.Equal(DeadStockStage.Stage2, spell.MaxStage);
        Assert.Equal(3, spell.LastLocalStock);
        Assert.Equal(4, spell.EntryLocalStock);
    }

    [Theory]
    [InlineData(null, DeadStockExitReason.Delisted)]           // not in the pull
    [InlineData(0, DeadStockExitReason.Cleared)]               // stock gone
    [InlineData(5, DeadStockExitReason.Revived)]               // sold (Qty7 = 1 below), stock left
    [InlineData(9, DeadStockExitReason.Restocked)]             // no sale, more units than last seen (5)
    [InlineData(4, DeadStockExitReason.Other)]                 // no sale, no new units, just not flagged any more
    public void Apply_ClosesASpell_WithTheReasonTheExitDayShows(int? stock, DeadStockExitReason expected)
    {
        var spell = Open("A", lastSeen: D1, lastStock: 5);
        var facts = new Dictionary<string, DeadStockPullFacts>();
        if (stock is int s) facts["A"] = Facts(s, qty7: expected == DeadStockExitReason.Revived ? 1 : 0);

        DeadStockEpisodeService.Apply(1, D1.AddDays(1), new List<DeadStockEpisode> { spell }, Dead(), Pull(facts));

        Assert.Equal(D1.AddDays(1), spell.ExitedDate);
        Assert.Equal(expected, spell.ExitReason);
        Assert.Equal(stock, spell.ExitLocalStock);
    }

    [Fact]
    public void ExitReason_SoldMeansSoldSinceLastSeen_UsingTheWindowThatCoversTheGap()
    {
        // Last seen 10 days ago: a sale that only the 14-day window sees still counts…
        var spell = Open("A", lastSeen: D1, lastStock: 5);
        Assert.Equal(DeadStockExitReason.Revived,
            DeadStockEpisodeService.ExitReasonFor(spell, D1.AddDays(10), Facts(5, qty7: 0, qty14: 1, qty90: 1)));
        // …but one day later, a sale only the 90-day window sees is older than the spell's last day: not a revival.
        Assert.Equal(DeadStockExitReason.Other,
            DeadStockEpisodeService.ExitReasonFor(spell, D1.AddDays(1), Facts(5, qty7: 0, qty90: 3)));
    }

    [Fact]
    public void Apply_SameDayRerun_LandsWhereASingleRunWould()
    {
        var day = D1.AddDays(1);
        var carried = Open("CARRY", lastSeen: D1, lastStock: 5);
        var closedEarlier = Open("BACK", lastSeen: D1, lastStock: 5);
        var candidates = new List<DeadStockEpisode> { carried, closedEarlier };
        var pull = Pull(new() { ["BACK"] = Facts(5), ["NEW"] = Facts(2) });

        // Run 1 today: BACK is not dead (closes), NEW is dead (opens).
        var (added1, _) = DeadStockEpisodeService.Apply(1, day, candidates, Dead(("CARRY", DeadStockStage.Stage1, 5), ("NEW", DeadStockStage.Stage1, 2)), pull);
        Assert.Equal(day, closedEarlier.ExitedDate);
        var opened = Assert.Single(added1);

        // Run 2 today: BACK is dead again, NEW is not. Same as a single run that saw only the second picture.
        candidates.Add(opened);
        var (added2, removed2) = DeadStockEpisodeService.Apply(1, day, candidates, Dead(("CARRY", DeadStockStage.Stage1, 5), ("BACK", DeadStockStage.Stage1, 5)), pull);
        Assert.Empty(added2);                                  // BACK reopened, not duplicated
        Assert.Null(closedEarlier.ExitedDate);
        Assert.Equal(DeadStockExitReason.None, closedEarlier.ExitReason);
        Assert.Equal(D1.AddDays(-30), closedEarlier.EnteredDate);   // the original spell, entry untouched
        Assert.Same(opened, Assert.Single(removed2));          // NEW never ended a day dead
        Assert.Null(carried.ExitedDate);
    }

    // ---- the country's daily pile ---------------------------------------------------------------------

    [Fact]
    public void ComputeKpi_CountsASkuDeadOnBothBrandsOnce_AndReconcilesTheFlows()
    {
        var day = D1.AddDays(1);
        var episodes = new List<DeadStockEpisode>
        {
            // SHARED: dead on both layers (one warehouse) — one SKU, one set of units, the deeper tier.
            Open("SHARED", lastSeen: day, lastStock: 4, layerId: 1, entered: D1, stage: DeadStockStage.Stage1),
            Open("SHARED", lastSeen: day, lastStock: 4, layerId: 2, entered: D1, stage: DeadStockStage.Stage2),
            Open("MALL-ONLY", lastSeen: day, lastStock: 3, layerId: 1, entered: D1),
            Open("NEW-TODAY", lastSeen: day, lastStock: 2, layerId: 2, entered: day),
            Closed("SOLD-OUT", layerId: 1, entered: D1.AddDays(-5), exited: day, DeadStockExitReason.Cleared),
            Closed("GONE", layerId: 2, entered: D1.AddDays(-5), exited: day, DeadStockExitReason.Delisted),
        };

        var kpi = DeadStockEpisodeService.ComputeKpi("KS", day, D1, episodes);

        Assert.Equal(3, kpi.DeadSkus);                         // SHARED, MALL-ONLY, NEW-TODAY
        Assert.Equal(4 + 3 + 2, kpi.DeadUnits);                // SHARED's 4 units once
        Assert.Equal(90m, kpi.DeadCost);                       // 9 units × PPTCV 10
        Assert.Equal(1, kpi.Stage2Skus);                       // SHARED at its deepest tier
        Assert.Equal(2, kpi.Stage1Skus);
        Assert.Equal(1, kpi.EnteredSkus);                      // NEW-TODAY
        Assert.Equal(2, kpi.LeftSkus);
        Assert.Equal(1, kpi.LeftCleared);
        Assert.Equal(1, kpi.LeftDelisted);

        var prev = DeadStockEpisodeService.ComputeKpi("KS", D1, null, episodes);
        Assert.Equal(prev.DeadSkus + kpi.EnteredSkus - kpi.LeftSkus, kpi.DeadSkus);
    }

    // ---- live path vs replay (what the backfill relies on) -------------------------------------------

    [Fact]
    public async Task Replay_WritesExactlyWhatTheLiveRunsWould()
    {
        var days = SampleHistory();

        using var db = NewDb();
        db.Layers.AddRange(
            new Layer { Brand = "GjirafaMall", CountryCode = "KS", DisplayName = "Mall KS", OperationalDatabase = "m", Currency = "EUR", RunTimeUtc = "04:30", IsActive = true },
            new Layer { Brand = "Gjirafa50", CountryCode = "KS", DisplayName = "Gj50 KS", OperationalDatabase = "g", Currency = "EUR", RunTimeUtc = "04:15", IsActive = true });
        await db.SaveChangesAsync();
        var service = new DeadStockEpisodeService(db);
        foreach (var d in days.OrderBy(d => d.Date).ThenBy(d => d.LayerId))
            await service.RecordAsync(d.LayerId, d.CountryCode, d.Date, d.Dead, PullRows(d));

        var (episodes, kpis) = DeadStockEpisodeService.Replay(days);

        static string Key(DeadStockEpisode e) =>
            $"{e.LayerId}|{e.Sku}|{e.EnteredDate:d}|{e.LastSeenDate:d}|{e.MaxStage}|{e.LastLocalStock}|{e.ExitedDate:d}|{e.ExitReason}|{e.ExitLocalStock}";
        static string KpiKey(DeadStockDailyKpi k) =>
            $"{k.CountryCode}|{k.Date:d}|{k.PrevDate:d}|{k.DeadSkus}|{k.DeadUnits}|{k.DeadCost}|{k.EnteredSkus}|{k.LeftSkus}|{k.LeftCleared}|{k.LeftRevived}|{k.LeftRestocked}|{k.LeftDelisted}|{k.LeftOther}";
        Assert.Equal(
            (await db.DeadStockEpisodes.AsNoTracking().ToListAsync()).Select(Key).OrderBy(x => x),
            episodes.Select(Key).OrderBy(x => x));
        Assert.Equal(
            (await db.DeadStockDailyKpis.AsNoTracking().ToListAsync()).Select(KpiKey).OrderBy(x => x),
            kpis.Select(KpiKey).OrderBy(x => x));
        Assert.Contains(episodes, e => e.ExitReason == DeadStockExitReason.Cleared);
        Assert.Contains(episodes, e => e.ExitReason == DeadStockExitReason.Revived);
    }

    /// <summary>Four days, two layers of one country: spells that open, carry, clear, revive and re-enter,
    /// a SKU shared by both layers, and a day on which only one layer ran.</summary>
    private static List<DeadStockLayerDay> SampleHistory()
    {
        DeadStockLayerDay Day(int layer, int offset, Dictionary<string, DeadSkuToday> dead, Dictionary<string, DeadStockPullFacts> pull) =>
            new(layer, "KS", D1.AddDays(offset), dead, Pull(pull));
        return new List<DeadStockLayerDay>
        {
            Day(1, 0, Dead(("A", DeadStockStage.Stage1, 5), ("B", DeadStockStage.Stage1, 2), ("S", DeadStockStage.Stage1, 7)),
                new() { ["A"] = Facts(5), ["B"] = Facts(2), ["S"] = Facts(7) }),
            Day(2, 0, Dead(("S", DeadStockStage.Stage2, 7)), new() { ["S"] = Facts(7) }),
            Day(1, 1, Dead(("A", DeadStockStage.Stage1, 5), ("S", DeadStockStage.Stage1, 7)),
                new() { ["A"] = Facts(5), ["B"] = Facts(0), ["S"] = Facts(7) }),                // B cleared
            // day 2: only layer 1 ran; A sold one unit
            Day(1, 2, Dead(("S", DeadStockStage.Stage1, 7)), new() { ["A"] = Facts(4, qty7: 1), ["S"] = Facts(7) }),
            Day(1, 3, Dead(("A", DeadStockStage.Stage1, 4), ("S", DeadStockStage.Stage1, 7)),
                new() { ["A"] = Facts(4), ["S"] = Facts(7) }),                                  // A dead again: a new spell
            Day(2, 3, Dead(), new() { ["S"] = Facts(7) }),                                      // S no longer dead on layer 2
        };
    }

    /// <summary>The pull a live run would carry for a replay day: every SKU the day knows facts for.</summary>
    private static IReadOnlyList<SnapshotRow> PullRows(DeadStockLayerDay d)
    {
        var skus = new[] { "A", "B", "S" };
        return skus.Select(s => d.Pull(s) is DeadStockPullFacts f
                ? new SnapshotRow { Sku = s, LocalWarehouseStock = f.LocalStock, Qty7 = f.Qty7, Qty14 = f.Qty14, Qty30 = f.Qty30, Qty60 = f.Qty60, Qty90 = f.Qty90 }
                : null)
            .Where(r => r is not null).Select(r => r!).ToList();
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private static DeadStockEpisode Open(string sku, DateTime lastSeen, int lastStock, int layerId = 1,
        DateTime? entered = null, DeadStockStage stage = DeadStockStage.Stage1) => new()
    {
        LayerId = layerId, Sku = sku, EnteredDate = entered ?? lastSeen.AddDays(-30), EntryStage = stage, EntryLocalStock = lastStock,
        EntryPptcv = 10m, MaxStage = stage, LastSeenDate = lastSeen, LastStage = stage, LastLocalStock = lastStock, LastPptcv = 10m,
    };

    private static DeadStockEpisode Closed(string sku, int layerId, DateTime entered, DateTime exited, DeadStockExitReason reason)
    {
        var e = Open(sku, lastSeen: exited.AddDays(-1), lastStock: 1, layerId: layerId, entered: entered);
        e.ExitedDate = exited;
        e.ExitReason = reason;
        return e;
    }
}
