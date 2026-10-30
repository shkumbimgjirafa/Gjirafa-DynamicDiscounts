using Microsoft.EntityFrameworkCore;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Checker;
using PricingTool.Agent.Storage;
using PricingTool.Data;

namespace PricingTool.Agent.Operations;

public sealed record WaitingDecision(AgentDecision Decision, string BandName);

public sealed record PublishProblem(PublishBatch Batch, IReadOnlyList<(string Kind, int Count)> Exceptions)
{
    /// <summary>Stopped (a switch), failed, or rolled back because prices did not read back: a person should look.
    /// A rollback after a clean read-back was asked for by a person (e.g. a canary) and a published batch with a few
    /// exceptions (e.g. a cheaper team deal) are listed but don't count as waiting.</summary>
    public bool NeedsAPerson => Batch.Status is "Stopped" or "Failed" || Batch.Status == "RolledBack" && Batch.RoundTripMismatches > 0;
}

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

    /// <summary>Daily-cycle steps that failed and haven't succeeded since on the same day.</summary>
    public IReadOnlyList<DailyCycleStep> FailedSteps { get; init; } = Array.Empty<DailyCycleStep>();

    /// <summary>Band × categories paused after an automatic rollback, until a person releases them.</summary>
    public IReadOnlyList<CategoryPause> Pauses { get; init; } = Array.Empty<CategoryPause>();

    /// <summary>Campaign briefs held on a cap a person may lift (too many mini-campaigns at once), real passes only.</summary>
    public IReadOnlyList<CampaignBrief> HeldBriefs { get; init; } = Array.Empty<CampaignBrief>();

    public IReadOnlyDictionary<int, string> LayerNames { get; init; } = new Dictionary<int, string>();

    public int WaitingCount => Decisions.Count + BlockedRuns.Count + Publishes.Count(p => p.NeedsAPerson) + FailedSteps.Count + Pauses.Count + HeldBriefs.Count;

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
            .Where(d => (d.Status == DecisionStatus.Held || d.Status == DecisionStatus.Pending)
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
            FailedSteps = failed,
            Pauses = await _ai.CategoryPauses.AsNoTracking().Where(p => p.ReleasedUtc == null).OrderByDescending(p => p.Id).ToListAsync(ct),
            HeldBriefs = await _ai.CampaignBriefs.AsNoTracking().Where(b => b.Status == BriefStatus.Held && !b.IsTest).OrderByDescending(b => b.Id).ToListAsync(ct),
            LayerNames = await _tool.Layers.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.DisplayName, ct),
        };
    }
}
