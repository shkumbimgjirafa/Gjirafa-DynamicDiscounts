using Microsoft.EntityFrameworkCore;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Checker;
using PricingTool.Agent.Storage;
using PricingTool.Data;

namespace PricingTool.Agent.Operations;

public sealed record WaitingDecision(AgentDecision Decision, string BandName);

/// <summary>A campaign plan waiting for a person, with its brief's name and goal.</summary>
public sealed record PlanWaiting(CampaignPlan Plan, string Campaign, string GoalType, decimal GoalValue);

public sealed record PublishProblem(PublishBatch Batch, IReadOnlyList<(string Kind, int Count)> Exceptions)
{
    /// <summary>Wrote nothing because every SKU it was given was left out with a recorded reason (e.g. the cost watcher's
    /// one-SKU raise under a cheaper team deal). The reasons are listed once per SKU, and a cost-watcher SKU also waits as
    /// its hold, so the batch doesn't count a second time.</summary>
    public bool AllLeftOut => Batch.Status == "Failed" && Batch.Requested > 0 && Batch.Skipped >= Batch.Requested && Exceptions.Count > 0;

    /// <summary>Stopped (a switch), failed, or rolled back because prices did not read back: a person should look.
    /// A rollback after a clean read-back was asked for by a person (e.g. a canary), a published batch with a few
    /// exceptions (e.g. a cheaper team deal) and a batch whose every SKU was left out are listed but don't count as waiting.</summary>
    public bool NeedsAPerson => Batch.Status == "Stopped" || Batch.Status == "Failed" && !AllLeftOut
                                || Batch.Status == "RolledBack" && Batch.RoundTripMismatches > 0;
}

/// <summary>One SKU's cost-watcher holds. The watcher reads every cost move, so the same SKU can be held more than once
/// in a day; it waits for a person once, and "handled" clears all of its holds.</summary>
public sealed record CostHoldGroup(int LayerId, string Sku, AgentDecision Latest, IReadOnlyList<long> Ids)
{
    /// <summary>The SKU a cost-watcher decision is about, from its evidence (<c>{"sku": ...}</c>); its id when there is none.</summary>
    public static string SkuOf(AgentDecision d)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(d.EvidenceRefs ?? "");
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("sku", out var sku) && sku.GetString() is { Length: > 0 } s)
                return s;
        }
        catch (System.Text.Json.JsonException) { }
        return $"#{d.Id}";
    }
}

/// <summary>One SKU the publisher left out or flagged, once over the recent days: on how many days, when last, and why.</summary>
public sealed record PublishSkuLine(string Sku, int Days, DateTime LastUtc, string Detail);

/// <summary>A layer's publish exceptions of one kind counted once per SKU (the same cheaper-deal SKUs come back every
/// day, about 73k rows a day on both KS layers): how many SKUs, how many first seen in the last day, the most days any was
/// seen, and the latest few with their reason.</summary>
public sealed record PublishSkuGroup(int LayerId, string Kind, int Skus, int NewInLastDay, int MostDays, IReadOnlyList<PublishSkuLine> Latest);

/// <summary>What the exceptions inbox shows: only what needs a person (plan section 5, "Exceptions inbox").</summary>
public sealed class InboxSnapshot
{
    /// <summary>Held on a limit a person may lift, or pending in approve-first mode (never test passes).</summary>
    public IReadOnlyList<WaitingDecision> Decisions { get; init; } = Array.Empty<WaitingDecision>();

    /// <summary>The latest check of each recent run when its verdict is block: that run's export and publish are stopped.</summary>
    public IReadOnlyList<AgentRunCheck> BlockedRuns { get; init; } = Array.Empty<AgentRunCheck>();

    /// <summary>The latest check of each recent run when it held prices back (verdict warn): listed, not counted.</summary>
    public IReadOnlyList<AgentRunCheck> RunsWithHeldPrices { get; init; } = Array.Empty<AgentRunCheck>();

    public IReadOnlyList<PublishProblem> Publishes { get; init; } = Array.Empty<PublishProblem>();

    /// <summary>The recent publishes' left-out SKUs, once per layer, kind and SKU: listed, not counted as waiting.</summary>
    public IReadOnlyList<PublishSkuGroup> PublishSkus { get; init; } = Array.Empty<PublishSkuGroup>();

    /// <summary>Daily-cycle steps that failed and haven't succeeded since on the same day.</summary>
    public IReadOnlyList<DailyCycleStep> FailedSteps { get; init; } = Array.Empty<DailyCycleStep>();

    /// <summary>Band × categories paused after an automatic rollback, until a person releases them.</summary>
    public IReadOnlyList<CategoryPause> Pauses { get; init; } = Array.Empty<CategoryPause>();

    /// <summary>The cost watcher's holds (a data-error cost jump, a cost above the shelf price, a cheaper team discount), real only.</summary>
    public IReadOnlyList<AgentDecision> CostHolds { get; init; } = Array.Empty<AgentDecision>();

    /// <summary>The cost watcher's holds once per layer and SKU, newest first: what is counted as waiting.</summary>
    public IReadOnlyList<CostHoldGroup> CostHoldSkus => CostHolds
        .GroupBy(d => (d.LayerId, Sku: CostHoldGroup.SkuOf(d)))
        .Select(g => new CostHoldGroup(g.Key.LayerId, g.Key.Sku, g.OrderByDescending(d => d.Id).First(), g.Select(d => d.Id).ToList()))
        .OrderByDescending(g => g.Latest.Id).ToList();

    /// <summary>Campaign briefs held on a cap a person may lift (too many mini-campaigns at once), real passes only.</summary>
    public IReadOnlyList<CampaignBrief> HeldBriefs { get; init; } = Array.Empty<CampaignBrief>();

    /// <summary>Campaign plans waiting for a person: held (e.g. short of the goal they set), or gone ahead as a stretch on it.</summary>
    public IReadOnlyList<PlanWaiting> CampaignPlans { get; init; } = Array.Empty<PlanWaiting>();

    public IReadOnlyDictionary<int, string> LayerNames { get; init; } = new Dictionary<int, string>();

    public int WaitingCount => Decisions.Count + BlockedRuns.Count + Publishes.Count(p => p.NeedsAPerson) + FailedSteps.Count + Pauses.Count + HeldBriefs.Count
                               + CostHoldSkus.Count + CampaignPlans.Count;

    public string LayerName(int layerId) => LayerNames.TryGetValue(layerId, out var n) ? n : $"Layer {layerId}";
}

/// <summary>
/// Collects everything waiting for a person: held and pending decisions, runs the checker blocked, publishes that
/// were stopped, rolled back or failed, and failed daily-cycle steps. One place, so the inbox page, the navigation
/// badge and (later) the Agent tab's "Waiting for you" and notifications count the same things.
/// </summary>
public sealed class ExceptionsInbox
{
    private const int RecentDays = 7;
    private readonly AgentDbContext _ai;
    private readonly PricingToolDbContext _tool;
    private readonly TimeProvider _clock;

    public ExceptionsInbox(AgentDbContext ai, PricingToolDbContext tool, TimeProvider? clock = null)
    {
        _ai = ai;
        _tool = tool;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<InboxSnapshot> LoadAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var since = now.AddDays(-RecentDays);

        var decisions = await _ai.AgentDecisions.AsNoTracking()
            .Where(d => (d.Status == DecisionStatus.Held || d.Status == DecisionStatus.Pending) && d.Source == FloorTargetAgent.Source
                        && _ai.AgentPasses.Any(p => p.Id == d.PassId && !p.IsTest))
            .OrderBy(d => d.LayerId).ThenBy(d => d.BandId).ThenBy(d => d.Category)
            .ToListAsync(ct);
        var bandIds = decisions.Select(d => d.BandId).Distinct().ToList();
        var bandNames = await _tool.PriceBands.AsNoTracking().Where(b => bandIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Name, ct);

        var checks = await _ai.AgentRunChecks.AsNoTracking().Where(c => !c.IsTest && c.StartedUtc >= since).ToListAsync(ct);
        var latestPerRun = checks.GroupBy(c => c.RunId).Select(g => g.OrderByDescending(c => c.Id).First()).OrderByDescending(c => c.Id).ToList();

        var batches = await _ai.PublishBatches.AsNoTracking().Where(b => b.StartedUtc >= since).OrderByDescending(b => b.Id).ToListAsync(ct);
        var batchIds = batches.Select(b => b.Id).ToList();
        var exceptionCounts = (await _ai.PublishExceptions.AsNoTracking().Where(e => batchIds.Contains(e.BatchId))
                .GroupBy(e => new { e.BatchId, e.Kind }).Select(g => new { g.Key.BatchId, g.Key.Kind, Count = g.Count() }).ToListAsync(ct))
            .ToLookup(x => x.BatchId);

        var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
        var steps = await _ai.DailyCycleSteps.AsNoTracking().Where(s => s.CycleDate >= today.AddDays(-1)).ToListAsync(ct);
        var failed = steps.Where(s => s.Status == "Failed"
                                      && !steps.Any(o => o.CycleDate == s.CycleDate && o.Step == s.Step && o.Id > s.Id && o.Status == "Succeeded"))
            .OrderByDescending(s => s.Id).ToList();

        return new InboxSnapshot
        {
            Decisions = decisions.Select(d => new WaitingDecision(d, bandNames.TryGetValue(d.BandId, out var n) ? n : $"band {d.BandId}")).ToList(),
            BlockedRuns = latestPerRun.Where(c => c.Verdict == Severity.Block).ToList(),
            RunsWithHeldPrices = latestPerRun.Where(c => c.Verdict == Severity.Warn).ToList(),
            Publishes = batches
                .Select(b => new PublishProblem(b, exceptionCounts[b.Id].OrderByDescending(x => x.Count).Select(x => (x.Kind, x.Count)).ToList()))
                .Where(p => p.NeedsAPerson || p.Exceptions.Count > 0)
                .ToList(),
            PublishSkus = await PublishSkusAsync(_ai, since, now, 5, ct),
            FailedSteps = failed,
            Pauses = await _ai.CategoryPauses.AsNoTracking().Where(p => p.ReleasedUtc == null).OrderByDescending(p => p.Id).ToListAsync(ct),
            CostHolds = await _ai.AgentDecisions.AsNoTracking()
                .Where(d => d.Status == DecisionStatus.Held && d.Source == "CostWatcher" && _ai.AgentPasses.Any(p => p.Id == d.PassId && !p.IsTest))
                .OrderByDescending(d => d.Id).ToListAsync(ct),
            HeldBriefs = await _ai.CampaignBriefs.AsNoTracking().Where(b => b.Status == BriefStatus.Held && !b.IsTest).OrderByDescending(b => b.Id).ToListAsync(ct),
            // The latest plan of each brief only, and of the campaign's latest brief: an older held plan a newer plan or a
            // re-brief replaced waits for nobody.
            CampaignPlans = await (from p in _ai.CampaignPlans.AsNoTracking()
                                   join b in _ai.CampaignBriefs.AsNoTracking() on p.BriefId equals b.Id
                                   where !p.IsTest && (p.Status == PlanStatus.Held || p.WaitsForPerson)
                                         && !_ai.CampaignPlans.Any(n => n.BriefId == p.BriefId && n.Id > p.Id)
                                         && !(b.CalendarId != null && _ai.CampaignBriefs.Any(n => n.CalendarId == b.CalendarId && n.Id > b.Id && !n.IsTest
                                                                                               && n.Status != BriefStatus.Rejected))
                                   orderby p.Id descending
                                   select new PlanWaiting(p, b.Name, b.GoalType, b.GoalValue)).ToListAsync(ct),
            LayerNames = await _tool.Layers.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.DisplayName, ct),
        };
    }

    /// <summary>The publish exceptions since <paramref name="since"/>, once per layer, kind and SKU (counted in the database),
    /// with the <paramref name="sample"/> latest SKUs of each group and their latest reason. Shared with the weekly report.</summary>
    public static async Task<IReadOnlyList<PublishSkuGroup>> PublishSkusAsync(AgentDbContext ai, DateTime since, DateTime now, int sample, CancellationToken ct = default)
    {
        var skus = await (from e in ai.PublishExceptions
                          join b in ai.PublishBatches on e.BatchId equals b.Id
                          where b.StartedUtc >= since
                          group e by new { b.LayerId, e.Kind, e.Sku } into g
                          select new
                          {
                              g.Key.LayerId, g.Key.Kind, g.Key.Sku, First = g.Min(x => x.CreatedUtc), Last = g.Max(x => x.CreatedUtc), LastId = g.Max(x => x.Id),
                              Days = g.Select(x => x.CreatedUtc.Date).Distinct().Count(),
                          }).ToListAsync(ct);
        var groups = skus.GroupBy(s => (s.LayerId, s.Kind))
            .Select(g => (g.Key, Skus: g.Count(), New: g.Count(s => s.First >= now.AddDays(-1)), Most: g.Max(s => s.Days),
                Top: g.OrderByDescending(s => s.Last).ThenBy(s => s.Sku, StringComparer.Ordinal).Take(sample).ToList()))
            .ToList();
        var ids = groups.SelectMany(g => g.Top.Select(t => t.LastId)).ToList();
        var details = await ai.PublishExceptions.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Detail, ct);
        return groups.OrderBy(g => g.Key.LayerId).ThenByDescending(g => g.Skus)
            .Select(g => new PublishSkuGroup(g.Key.LayerId, g.Key.Kind, g.Skus, g.New, g.Most,
                g.Top.Select(t => new PublishSkuLine(t.Sku, t.Days, t.Last, details.GetValueOrDefault(t.LastId, ""))).ToList()))
            .ToList();
    }
}
