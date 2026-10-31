using Microsoft.EntityFrameworkCore;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Operations;
using PricingTool.Agent.Storage;
using PricingTool.Web.Models;

namespace PricingTool.Web.Services;

/// <summary>What the agents did, read from their records: the week's numbers, the activity lines of the Chat tab, the
/// campaigns coming up and the latest Run week per layer. Shared by the Run and Chat tabs.</summary>
public class AgentActivity
{
    private readonly AgentDbContext _ai;

    public AgentActivity(AgentDbContext ai) => _ai = ai;

    public async Task<WeekNumbers> WeekAsync(DateTime since)
    {
        var real = _ai.AgentPasses.Where(p => !p.IsTest).Select(p => p.Id);
        var decisions = await _ai.AgentDecisions.AsNoTracking().Where(d => d.CreatedUtc >= since && real.Contains(d.PassId) && d.Action != "keep" && d.Source != "CostWatcher")
            .GroupBy(d => d.Status).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        // The cost watcher holds a SKU again at each cost move: count its holds once per layer and SKU, like the inbox.
        var costHeld = (await _ai.AgentDecisions.AsNoTracking()
                .Where(d => d.CreatedUtc >= since && real.Contains(d.PassId) && d.Source == "CostWatcher" && d.Status == DecisionStatus.Held).ToListAsync())
            .Select(d => (d.LayerId, CostHoldGroup.SkuOf(d))).Distinct().Count();
        var batches = await _ai.PublishBatches.AsNoTracking().Where(b => b.StartedUtc >= since && b.Status == "Published").ToListAsync();
        var calls = await _ai.AiCallLog.AsNoTracking().Where(c => c.CalledAtUtc >= since).ToListAsync();
        var ledger = await _ai.MoneyLedger.AsNoTracking().Where(m => m.Measured30Eur != null).ToListAsync();
        return new WeekNumbers(decisions.GetValueOrDefault(DecisionStatus.Applied), decisions.GetValueOrDefault(DecisionStatus.Held), batches.Count,
            batches.Sum(b => b.Created + b.Updated), calls.Count, calls.Sum(c => c.CostUsd ?? 0m),
            ledger.Count > 0 ? ledger.Sum(m => m.Measured30Eur!.Value) : null, await _ai.MoneyLedger.CountAsync(), costHeld);
    }

    /// <summary>What both agents, the cost watcher and the publisher did since <paramref name="since"/>, newest first, with
    /// repeats folded (<see cref="Group"/>). <paramref name="campaignLink"/> turns a calendar id into a link.</summary>
    public async Task<IReadOnlyList<FeedItem>> FeedAsync(DateTime since, Func<long, string?> campaignLink, int take = 80)
    {
        var feed = new List<FeedItem>();
        var real = await _ai.AgentPasses.AsNoTracking().Where(p => !p.IsTest && p.StartedUtc >= since).ToListAsync();
        var realIds = real.Select(p => p.Id).ToList();
        foreach (var p in real.Where(p => p.Source != "CostWatcher"))
            feed.Add(new FeedItem(p.StartedUtc, p.Source, p.LayerId, $"{(p.Source == "Nova" ? "Weekly campaign pass" : "Weekly floor and target pass")} #{p.Id}: {p.Status}",
                p.Summary ?? p.Error, null));
        foreach (var d in await _ai.AgentDecisions.AsNoTracking().Where(d => realIds.Contains(d.PassId) && d.Action != "keep").OrderByDescending(d => d.Id).Take(400).ToListAsync())
            feed.Add(d.Source == "CostWatcher"
                ? new FeedItem(d.CreatedUtc, d.Source, d.LayerId, CostWatcherLine(d.Action, d.Status, d.Limit, 1),
                    $"{CostHoldGroup.SkuOf(d)}: {d.Reason}", null, $"cw|{d.Action}|{d.Status}|{d.Limit}")
                : new FeedItem(d.CreatedUtc, d.Source, d.LayerId,
                    $"{d.Action} band {d.BandId} {d.Category}: floor {d.CurrentFloorPct:0.#} -> {d.ProposedFloorPct:0.#}%, target {d.CurrentTargetPct:0.#} -> {d.ProposedTargetPct:0.#}% ({d.Status})",
                    d.Reason, null));
        var messages = await (from m in _ai.CampaignMessages.AsNoTracking()
                              join b in _ai.CampaignBriefs.AsNoTracking() on m.BriefId equals b.Id
                              where !b.IsTest && m.CreatedUtc >= since
                              orderby m.Id descending
                              select new { m, b.Name, b.LayerId, b.CalendarId }).Take(40).ToListAsync();
        foreach (var x in messages)
            feed.Add(new FeedItem(x.m.CreatedUtc, x.m.From, x.LayerId, $"on {x.Name} (round {x.m.Round})", x.m.Text,
                x.CalendarId is { } cid ? campaignLink(cid) : null));
        foreach (var b in await _ai.PublishBatches.AsNoTracking().Where(b => b.StartedUtc >= since).OrderByDescending(b => b.Id).Take(300).ToListAsync())
        {
            var allLeftOut = b.Status == "Failed" && b.Requested > 0 && b.Skipped >= b.Requested;
            var oneSku = b.Kind == "Regular" && b.EngineRunId is null && b.Requested == 1 && b.Status == "Published" && b.RolledBackUtc is null;
            feed.Add(allLeftOut
                ? new FeedItem(b.StartedUtc, "Publisher", b.LayerId, LeftOutLine(1), $"batch #{b.Id}: {b.Requested} SKU asked, {b.Skipped} left out", null, "pub|left-out")
                : oneSku
                ? new FeedItem(b.StartedUtc, "Publisher", b.LayerId, OneSkuLine(1), $"batch #{b.Id}: {b.Created:N0} created, {b.Updated:N0} updated, {b.TierRaised:N0} regular price raised", null, "pub|one-sku")
                : b.Kind == "Expire" && b.Created + b.Updated == 0
                    ? new FeedItem(b.StartedUtc, "Publisher", b.LayerId, $"Expire batch #{b.Id} to {b.Target}: {b.Status}, {b.DiscountsEnded:N0} of our discounts ended", null, null)
                    : new FeedItem(b.StartedUtc, "Publisher", b.LayerId,
                        $"{b.Kind} batch #{b.Id} to {b.Target}: {b.Status}{(b.RolledBackUtc is null ? "" : ", rolled back")} ({b.Created:N0} created, {b.Updated:N0} updated)", null, null));
        }
        return Group(feed.OrderByDescending(f => f.Utc)).Take(take).ToList();
    }

    /// <summary>Folds lines of the same group (same agent, layer and kind, within 30 minutes of the group's newest line) into
    /// one line with the count, at the newest line's place, keeping every reason under "why?". Other lines may fall between them.</summary>
    public static IEnumerable<FeedItem> Group(IEnumerable<FeedItem> newestFirst)
    {
        var slots = new List<List<FeedItem>>();
        var open = new Dictionary<(string, int?, string), List<FeedItem>>();
        foreach (var f in newestFirst)
        {
            if (f.Group is null) { slots.Add(new List<FeedItem> { f }); continue; }
            var key = (f.Who, f.LayerId, f.Group);
            if (open.TryGetValue(key, out var members) && members[0].Utc - f.Utc <= TimeSpan.FromMinutes(30)) { members.Add(f); continue; }
            open[key] = members = new List<FeedItem> { f };
            slots.Add(members);
        }
        foreach (var members in slots)
        {
            var first = members[0];
            if (members.Count == 1) { yield return first; continue; }
            var parts = first.Group!.Split('|');
            var what = parts[0] == "cw" ? CostWatcherLine(parts[1], parts[2], parts[3].Length == 0 ? null : parts[3], members.Count)
                : parts[1] == "one-sku" ? OneSkuLine(members.Count) : LeftOutLine(members.Count);
            var why = string.Join("\n", members.Take(60).Select(f => $"{f.Utc.ToLocalTime():HH:mm} {f.Why}")) + (members.Count > 60 ? $"\n… and {members.Count - 60} more" : "");
            yield return first with { What = what, Why = why, Count = members.Count };
        }
    }

    /// <summary>A cost-watcher limit in plain words.</summary>
    public static string? LimitWords(string? limit) => limit switch
    {
        "team-discount" => "a cheaper team deal sells under the new floor",
        "cost-above-shelf" => "the new cost is above the shelf price",
        "data-error-cost-jump" or "data-error" => "the cost jump looks like a data error",
        "own-push" => "an earlier push of ours holds the price",
        "protected-deal" => "a cheaper team deal sells under the floor, and a rule you gave protects it",
        null => null,
        _ => limit,
    };

    /// <summary>The cost watcher's line in plain words: what it did to how many SKUs, and why.</summary>
    public static string CostWatcherLine(string action, string status, string? limit, int n)
    {
        var skus = n == 1 ? "1 SKU" : $"{n:N0} SKUs";
        var why = LimitWords(limit);
        return (status, action) switch
        {
            (DecisionStatus.Held, _) => $"Held {skus} for a person after a cost rise{(why is null ? "" : ": " + why)}",
            (DecisionStatus.Applied, "end-team-deal") => $"Ended the team discount that kept {skus} under the floor after a cost rise; the floor-respecting price is on the shelf",
            (DecisionStatus.Applied, "raise-to-floor") => $"Raised {skus} to the floor after a cost rise",
            (DecisionStatus.Applied, "end-our-discount") => $"Ended our discount on {skus} after a cost rise, so the regular price applies",
            (DecisionStatus.Applied, "end-campaign-discount") => $"Ended our campaign discount early on {skus}: the cost rise put it under the campaign's minimum margin",
            _ => $"{char.ToUpperInvariant(action[0])}{action[1..]} on {skus} after a cost rise ({status}{(why is null ? "" : ", " + why)})",
        };
    }

    private static string OneSkuLine(int n) => n == 1
        ? "Published one SKU's price (the cost watcher's raise)"
        : $"Published {n:N0} single SKUs' prices (the cost watcher's raises)";

    private static string LeftOutLine(int n) => n == 1
        ? "A one-SKU publish wrote nothing: the SKU was left out (a cheaper deal or the shelf price)"
        : $"{n:N0} one-SKU publishes wrote nothing: each SKU was left out (a cheaper deal or the shelf price)";

    /// <summary>"Coming up": campaigns starting in the next three weeks with their plan, and the veto window for published ones.</summary>
    public async Task<IReadOnlyList<CampaignRow>> ComingUpAsync(DateOnly today)
    {
        var entries = await _ai.CampaignCalendar.AsNoTracking().Where(c => c.StartDate >= today && c.StartDate <= today.AddDays(21)).OrderBy(c => c.StartDate).ToListAsync();
        var rows = new List<CampaignRow>();
        foreach (var e in entries)
        {
            var brief = await _ai.CampaignBriefs.AsNoTracking().Where(b => b.CalendarId == e.Id && !b.IsTest && b.Status != BriefStatus.Superseded && b.Status != BriefStatus.Rejected)
                .OrderByDescending(b => b.Id).FirstOrDefaultAsync();
            var plan = brief is null ? null : await _ai.CampaignPlans.AsNoTracking().Where(p => p.BriefId == brief.Id).OrderByDescending(p => p.Id).FirstOrDefaultAsync();
            var batch = plan?.PublishBatchId is { } id ? await _ai.PublishBatches.AsNoTracking().SingleOrDefaultAsync(b => b.Id == id) : null;
            rows.Add(new CampaignRow(e, brief, plan, batch, null));
        }
        return rows;
    }

    /// <summary>The latest Run week of a layer, with its steps.</summary>
    public async Task<(RunWeek Week, IReadOnlyList<RunWeekStep> Steps)?> LatestRunWeekAsync(int layerId)
    {
        var week = await _ai.RunWeeks.AsNoTracking().Where(w => w.LayerId == layerId).OrderByDescending(w => w.Id).FirstOrDefaultAsync();
        if (week is null) return null;
        var steps = await _ai.RunWeekSteps.AsNoTracking().Where(s => s.RunWeekId == week.Id).OrderBy(s => s.Id).ToListAsync();
        return (week, steps);
    }
}
