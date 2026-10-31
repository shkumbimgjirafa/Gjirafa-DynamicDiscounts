using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PricingTool.Agent.Checker;
using PricingTool.Agent.Operations;
using PricingTool.Agent.Storage;

namespace PricingTool.NopIntegration;

/// <summary>
/// A run's checked prices (<see cref="CheckedExport.CheckedSetAsync"/>) to the shop sandbox, raises included (prompt 07):
/// Run week's publish step and the daily cycle's <c>publish-L&lt;layer&gt;</c> step. Cuts become ai-price discounts, raises
/// the store's regular price (<see cref="NopRegularPriceWriter"/>), each with what the run saw and its ceiling. Refused
/// unless <c>Publisher:Target</c> is Sandbox: the checked push file stays the live path until go-live. The cycle's publish
/// skips a run older than one already on the sandbox (as the export refuses a superseded file).
/// </summary>
public sealed class CheckedPricePublisher : ICheckedPricePublisher
{
    private readonly CheckedExport _checked;
    private readonly NopDiscountWriter _writer;
    private readonly AgentDbContext _db;
    private readonly PublisherOptions _options;

    public CheckedPricePublisher(CheckedExport @checked, NopDiscountWriter writer, AgentDbContext db, IOptions<PublisherOptions> options)
    {
        _checked = @checked;
        _writer = writer;
        _db = db;
        _options = options.Value;
    }

    public static IReadOnlyList<PublishItem> Items(CheckedSet set) =>
        set.Prices.Select(p => new PublishItem(p.Sku, p.Price, p.OldPrice)
        {
            RunCurrentPrice = p.CurrentPrice, RunOldPrice = p.OldPrice, Ceiling = p.Ceiling, ProposalId = p.ProposalId,
        }).ToList();

    public static PublishRequest Request(int layerId, long runId, IReadOnlyList<PublishItem> items) =>
        new(layerId, items) { EngineRunId = runId, RaiseRegularPrices = true };

    /// <summary>The checked set in one line: how many go out and what was held or left out.</summary>
    public static string Chosen(CheckedSet set) =>
        $"{set.Prices.Count:N0} checked prices ({set.HeldByCheck:N0} held by check #{set.Check?.Id}, {set.HoldoutLeftOut:N0} holdout left out" +
        (set.PausedRaisesLeftOut > 0 ? $", {set.PausedRaisesLeftOut:N0} raises in paused categories left out)" : ")");

    /// <summary>A publish's counts in one line: discounts, regular prices, and what was left alone and why: the calculator's own
    /// earlier push (imported by the team) apart from a team deal, and app-only deals (noted, ours written for the web).</summary>
    public static string Describe(PublishOutcome o)
    {
        int Kind(string k) => o.Exceptions.Count(e => e.Kind == k);
        return $"discounts created {o.Created:N0}, updated {o.Updated:N0}, unchanged {o.Unchanged:N0}, ended {o.DiscountsEnded:N0}; " +
               $"regular prices raised {o.TierRaised:N0}" + (o.TierInserted > 0 ? $" (+{o.TierInserted:N0} inserted)" : "") +
               $", of them under a cheaper deal {o.RaisedUnderTeamDeal:N0} (our own earlier push {Kind("raised-under-own-push"):N0}, a team deal {Kind("raised-under-team-deal"):N0}); " +
               $"left to a cheaper deal: our own earlier push {Kind("own-push"):N0}, a team deal {Kind("cheaper-deal"):N0}; " +
               $"cheaper only in the app (ours written for the web) {Kind("app-deal-cheaper"):N0}; " +
               $"skipped as changed since the run {o.ChangedSinceRun:N0}, skipped in all {o.Skipped:N0}" +
               (o.Reconciled.Count > 0 ? $"; interrupted batch {string.Join(", ", o.Reconciled.Select(b => $"#{b}"))} reconciled first" : "");
    }

    public async Task<(string Status, string Detail)> PublishCheckedAsync(int layerId, long runId, CancellationToken ct = default)
    {
        if (!_options.IsSandbox)
            return (StepStatus.Skipped, $"Publisher:Target is '{_options.Target}': the cycle publishes only to the shop sandbox; the checked push file stays the live path.");
        var newer = await _db.PublishBatches.AsNoTracking()
            .Where(b => b.LayerId == layerId && b.Kind == PublishKinds.Regular && b.EngineRunId > runId && b.RolledBackUtc == null
                        && (b.Status == "Published" || b.Status == "Interrupted"))
            .OrderByDescending(b => b.EngineRunId).FirstOrDefaultAsync(ct);
        if (newer is not null)
            return (StepStatus.Skipped, $"run #{newer.EngineRunId} is already on the sandbox (batch #{newer.Id}): the older run #{runId} isn't published over it");
        var set = await _checked.CheckedSetAsync(runId, ct);
        if (set.Check is null) return (StepStatus.Skipped, $"run #{runId} has no check: nothing published");
        if (set.Blocked is { } why) return (StepStatus.Skipped, $"check #{set.Check.Id} blocked run #{runId}: {why}. Nothing published.");
        var items = Items(set);
        if (items.Count == 0) return (StepStatus.Skipped, $"nothing to publish: {Chosen(set)}");

        var o = await _writer.PublishAsync(Request(layerId, runId, items), ct);
        var status = o.Status switch
        {
            "Stopped" => StepStatus.Skipped,
            "Published" when o.RoundTripMismatches == 0 => StepStatus.Succeeded,
            "Published" or "RolledBack" => StepStatus.Warning,
            _ => StepStatus.Failed,
        };
        return (status, $"sandbox batch #{o.BatchId} {o.Status} for run #{runId}: {Chosen(set)}; {Describe(o)}; " +
                        $"round-trip {o.RoundTripChecked - o.RoundTripMismatches:N0} of {o.RoundTripChecked:N0}");
    }
}
