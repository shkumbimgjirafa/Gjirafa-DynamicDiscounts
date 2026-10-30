using Microsoft.EntityFrameworkCore;
using PricingTool.Core.Domain;
using PricingTool.Data.Entities;

namespace PricingTool.Data.Services;

/// <summary>What a run saw for a SKU it flagged dead today.</summary>
public readonly record struct DeadSkuToday(DeadStockStage Stage, int LocalStock, decimal? Pptcv, decimal? Price);

/// <summary>What today's pull says about a SKU whose spell may be ending. A SKU absent from the pull is delisted.</summary>
public readonly record struct DeadStockPullFacts(int LocalStock, int Qty7, int Qty14, int Qty30, int Qty60, int Qty90);

/// <summary>One layer's run-day as <see cref="DeadStockEpisodeService.Replay"/> consumes it: the SKUs the run flagged
/// dead, and the pull facts of any other SKU (null when it was not in the pull).</summary>
public sealed record DeadStockLayerDay(
    int LayerId, string CountryCode, DateTime Date,
    IReadOnlyDictionary<string, DeadSkuToday> Dead, Func<string, DeadStockPullFacts?> Pull);

/// <summary>
/// Keeps the dead-stock history: one <see cref="DeadStockEpisode"/> per SKU spell per layer, and one
/// <see cref="DeadStockDailyKpi"/> per country per day. A run calls <see cref="RecordAsync"/> once it has
/// succeeded; the one-off backfill feeds old runs through <see cref="Replay"/>. Both go through the same
/// <see cref="Apply"/> and <see cref="ComputeKpi"/>, so history rebuilt from old runs and history written live
/// are the same thing.
/// </summary>
public class DeadStockEpisodeService
{
    private readonly PricingToolDbContext _db;

    public DeadStockEpisodeService(PricingToolDbContext db) => _db = db;

    /// <summary>
    /// Folds one run into the layer's spells, then rebuilds the country's row for the day. Safe to call again
    /// for the same layer and date (a same-day re-run): what the earlier run did today is undone first.
    /// </summary>
    public async Task RecordAsync(
        int layerId, string countryCode, DateTime date, IReadOnlyDictionary<string, DeadSkuToday> deadToday,
        IReadOnlyList<SnapshotRow> pull, CancellationToken ct = default)
    {
        date = date.Date;
        var candidates = await _db.DeadStockEpisodes
            .Where(e => e.LayerId == layerId && (e.ExitedDate == null || e.ExitedDate == date || e.EnteredDate == date))
            .ToListAsync(ct);

        // Pull facts are only needed for SKUs whose spell may end today; one pass over the pull, no catalogue-sized map.
        var watched = candidates.Select(e => e.Sku).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var facts = new Dictionary<string, DeadStockPullFacts>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in pull)
            if (watched.Contains(r.Sku))
                facts[r.Sku] = new DeadStockPullFacts(r.LocalWarehouseStock, r.Qty7, r.Qty14, r.Qty30, r.Qty60, r.Qty90);

        var (added, removed) = Apply(layerId, date, candidates, deadToday,
            sku => facts.TryGetValue(sku, out var f) ? f : null);
        _db.DeadStockEpisodes.AddRange(added);
        _db.DeadStockEpisodes.RemoveRange(removed);
        await _db.SaveChangesAsync(ct);

        var layerIds = await _db.Layers.AsNoTracking()
            .Where(l => l.CountryCode == countryCode).Select(l => l.Id).ToListAsync(ct);
        var prevDate = await _db.DeadStockDailyKpis.AsNoTracking()
            .Where(k => k.CountryCode == countryCode && k.Date < date)
            .MaxAsync(k => (DateTime?)k.Date, ct);
        var since = prevDate ?? date;
        var episodes = await _db.DeadStockEpisodes.AsNoTracking()
            .Where(e => layerIds.Contains(e.LayerId) && e.EnteredDate <= date && (e.ExitedDate == null || e.ExitedDate > since))
            .ToListAsync(ct);
        var kpi = ComputeKpi(countryCode, date, prevDate, episodes);

        var existing = await _db.DeadStockDailyKpis.FirstOrDefaultAsync(k => k.CountryCode == countryCode && k.Date == date, ct);
        if (existing is null)
        {
            _db.DeadStockDailyKpis.Add(kpi);
        }
        else
        {
            kpi.Id = existing.Id;
            _db.Entry(existing).CurrentValues.SetValues(kpi);
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// One layer's run-day. <paramref name="candidates"/> are the layer's open spells plus any an earlier run
    /// opened or closed on <paramref name="date"/>; they are updated in place. Returns the spells to add and the
    /// spells to delete (only ever ones an earlier run opened the same day and this run no longer sees).
    /// </summary>
    public static (List<DeadStockEpisode> Added, List<DeadStockEpisode> Removed) Apply(
        int layerId, DateTime date, IReadOnlyList<DeadStockEpisode> candidates,
        IReadOnlyDictionary<string, DeadSkuToday> deadToday, Func<string, DeadStockPullFacts?> pull)
    {
        date = date.Date;
        var added = new List<DeadStockEpisode>();
        var removed = new List<DeadStockEpisode>();

        // Undo what an earlier run did today, so a re-run lands where a single run would have: a spell it closed
        // today is open again. (A spell it opened today stays a candidate and is either carried or dropped below.)
        var open = new Dictionary<string, DeadStockEpisode>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in candidates.OrderBy(e => e.EnteredDate))
        {
            if (e.ExitedDate == date)
            {
                e.ExitedDate = null;
                e.ExitReason = DeadStockExitReason.None;
                e.ExitLocalStock = null;
                e.ExitQty90 = null;
            }
            if (e.ExitedDate is null)
                open[e.Sku] = e;
        }

        foreach (var (sku, d) in deadToday)
        {
            if (open.Remove(sku, out var e))
            {
                if (e.EnteredDate == date)
                {
                    e.EntryStage = d.Stage;
                    e.EntryLocalStock = d.LocalStock;
                    e.EntryPptcv = d.Pptcv;
                    e.EntryPrice = d.Price;
                    e.MaxStage = d.Stage;
                }
                else if (d.Stage > e.MaxStage)
                {
                    e.MaxStage = d.Stage;
                }
                e.LastSeenDate = date;
                e.LastStage = d.Stage;
                e.LastLocalStock = d.LocalStock;
                e.LastPptcv = d.Pptcv;
                e.LastPrice = d.Price;
                continue;
            }

            added.Add(new DeadStockEpisode
            {
                LayerId = layerId,
                Sku = sku,
                EnteredDate = date,
                EntryStage = d.Stage,
                EntryLocalStock = d.LocalStock,
                EntryPptcv = d.Pptcv,
                EntryPrice = d.Price,
                MaxStage = d.Stage,
                LastSeenDate = date,
                LastStage = d.Stage,
                LastLocalStock = d.LocalStock,
                LastPptcv = d.Pptcv,
                LastPrice = d.Price,
            });
        }

        // Whatever is still open is no longer dead today.
        foreach (var e in open.Values)
        {
            if (e.EnteredDate == date)
            {
                removed.Add(e); // opened by an earlier run today, gone by this one: it never ended a day dead
                continue;
            }
            var f = pull(e.Sku);
            e.ExitedDate = date;
            e.ExitReason = ExitReasonFor(e, date, f);
            e.ExitLocalStock = f?.LocalStock;
            e.ExitQty90 = f?.Qty90;
        }

        return (added, removed);
    }

    /// <summary>Why the spell ended, judged on the exit day's pull. First match wins (see <see cref="DeadStockExitReason"/>).
    /// "Sold" means sold since the SKU was last seen dead: the smallest source window covering that gap, the same
    /// rule the no-sale streak counter uses.</summary>
    public static DeadStockExitReason ExitReasonFor(DeadStockEpisode e, DateTime date, DeadStockPullFacts? facts)
    {
        if (facts is not DeadStockPullFacts f) return DeadStockExitReason.Delisted;
        if (f.LocalStock <= 0) return DeadStockExitReason.Cleared;
        var gap = Math.Clamp((date.Date - e.LastSeenDate.Date).Days, 1, 366);
        if (SalesWindows.UnitsCovering(gap, f.Qty7, f.Qty14, f.Qty30, f.Qty60, f.Qty90) > 0) return DeadStockExitReason.Revived;
        if (f.LocalStock > e.LastLocalStock) return DeadStockExitReason.Restocked;
        return DeadStockExitReason.Other;
    }

    /// <summary>
    /// The country's pile on <paramref name="date"/>, deduplicated across its layers, with the flows since
    /// <paramref name="prevDate"/>. <paramref name="episodes"/> must hold every spell of the country's layers that
    /// was open on either date or ended between them.
    /// </summary>
    public static DeadStockDailyKpi ComputeKpi(
        string countryCode, DateTime date, DateTime? prevDate, IEnumerable<DeadStockEpisode> episodes)
    {
        date = date.Date;
        var list = episodes as IReadOnlyCollection<DeadStockEpisode> ?? episodes.ToList();
        static bool OpenOn(DeadStockEpisode e, DateTime d) => e.EnteredDate <= d && (e.ExitedDate is null || e.ExitedDate > d);

        var kpi = new DeadStockDailyKpi { CountryCode = countryCode, Date = date, PrevDate = prevDate?.Date };
        var before = prevDate is DateTime p
            ? list.Where(e => OpenOn(e, p.Date)).Select(e => e.Sku).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in list.Where(e => OpenOn(e, date)).GroupBy(e => e.Sku, StringComparer.OrdinalIgnoreCase))
        {
            now.Add(g.Key);
            // One warehouse per country: take the freshest reading of the SKU's units, its deepest tier.
            var freshest = g.OrderByDescending(e => e.LastSeenDate).ThenByDescending(e => e.LastLocalStock).First();
            var units = Math.Max(0, freshest.LastLocalStock);
            kpi.DeadSkus++;
            kpi.DeadUnits += units;
            kpi.DeadCost += (freshest.LastPptcv ?? 0m) * units;
            switch (g.Max(e => e.LastStage))
            {
                case DeadStockStage.Stage4: kpi.Stage4Skus++; break;
                case DeadStockStage.Stage3: kpi.Stage3Skus++; break;
                case DeadStockStage.Stage2: kpi.Stage2Skus++; break;
                default: kpi.Stage1Skus++; break;
            }
            if (!before.Contains(g.Key)) kpi.EnteredSkus++;
        }
        kpi.DeadCost = Math.Round(kpi.DeadCost, 2);

        foreach (var sku in before)
        {
            if (now.Contains(sku)) continue;
            kpi.LeftSkus++;
            // The spell that ended last decides; a same-day tie goes to the most informative reason.
            var reason = list
                .Where(e => e.ExitedDate is DateTime x && x > prevDate!.Value.Date && x <= date
                            && string.Equals(e.Sku, sku, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.ExitedDate).ThenBy(e => TieRank(e.ExitReason))
                .Select(e => e.ExitReason).FirstOrDefault(DeadStockExitReason.Other);
            switch (reason)
            {
                case DeadStockExitReason.Cleared: kpi.LeftCleared++; break;
                case DeadStockExitReason.Revived: kpi.LeftRevived++; break;
                case DeadStockExitReason.Restocked: kpi.LeftRestocked++; break;
                case DeadStockExitReason.Delisted: kpi.LeftDelisted++; break;
                default: kpi.LeftOther++; break;
            }
        }
        return kpi;
    }

    private static int TieRank(DeadStockExitReason r) => r switch
    {
        DeadStockExitReason.Cleared => 0,
        DeadStockExitReason.Revived => 1,
        DeadStockExitReason.Restocked => 2,
        DeadStockExitReason.Delisted => 3,
        _ => 4,
    };

    /// <summary>
    /// Rebuilds the whole history from a sequence of layer run-days (any order; replayed by date, each country's
    /// row computed once all of that date's layers are folded in, as the last run of a live day leaves it).
    /// Pure: returns every spell and every country-day row, ready to insert.
    /// </summary>
    public static (List<DeadStockEpisode> Episodes, List<DeadStockDailyKpi> Kpis) Replay(IEnumerable<DeadStockLayerDay> days)
    {
        var all = new List<DeadStockEpisode>();
        var open = new Dictionary<int, List<DeadStockEpisode>>();
        var countryLayers = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var lastKpiDate = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var kpis = new List<DeadStockDailyKpi>();

        foreach (var dateGroup in days.GroupBy(d => d.Date.Date).OrderBy(g => g.Key))
        {
            var date = dateGroup.Key;
            // One run-day per layer per date: the last one given wins, as the day's last live run would.
            foreach (var day in dateGroup.GroupBy(d => d.LayerId).Select(g => g.Last()))
            {
                if (!countryLayers.TryGetValue(day.CountryCode, out var ids))
                    countryLayers[day.CountryCode] = ids = new HashSet<int>();
                ids.Add(day.LayerId);

                var layerOpen = open.TryGetValue(day.LayerId, out var lo) ? lo : new List<DeadStockEpisode>();
                var (added, removed) = Apply(day.LayerId, date, layerOpen, day.Dead, day.Pull);
                foreach (var r in removed) all.Remove(r);
                all.AddRange(added);
                open[day.LayerId] = layerOpen.Where(e => e.ExitedDate is null && !removed.Contains(e)).Concat(added).ToList();
            }

            foreach (var country in dateGroup.Select(d => d.CountryCode).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                DateTime? prev = lastKpiDate.TryGetValue(country, out var p) ? p : null;
                var since = prev ?? date;
                var layers = countryLayers[country];
                var relevant = all.Where(e => layers.Contains(e.LayerId) && e.EnteredDate <= date
                                              && (e.ExitedDate is null || e.ExitedDate > since));
                kpis.Add(ComputeKpi(country, date, prev, relevant));
                lastKpiDate[country] = date;
            }
        }
        return (all, kpis);
    }
}
