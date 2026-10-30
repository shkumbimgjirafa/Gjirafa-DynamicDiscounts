using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PricingTool.Core.Options;
using PricingTool.Data.Entities;

namespace PricingTool.Data.Services;

/// <summary>
/// Grades pushed price changes by the yardstick matching their intent (see <see cref="ChangeIntent"/>),
/// using a simple per-SKU pre/post comparison of trailing-7d run-rates. Runs once at the end of every
/// pricing run: it opens an in-flight (Pending) outcome for each freshly pushed change and finalises
/// the verdict once the change's post-window has elapsed.
///
/// Measurement is correlational — it does not separate the price move from market drift. That is the
/// accepted v1 trade-off (the entity reserves columns for a later causal upgrade).
/// </summary>
public class OutcomeEvaluationService
{
    /// <summary>Days after the change before its post-window is measured.</summary>
    public const int DefaultWindowDays = 7;

    /// <summary>Relative move (in %) below which a change is judged Neutral rather than Win/Backfire.</summary>
    public const decimal DefaultNeutralBandPct = 3m;

    // Reason codes (emitted by the markdown algorithms) that mark a price cut as a clearance play
    // rather than a volume play. Kept local to avoid a cross-project coupling on the algorithm codes.
    private const string DeadStockMarkdownCode = "DEAD_STOCK_MARKDOWN";
    private const string StockAgingCode = "STOCK_AGING";

    private readonly PricingToolDbContext _db;
    private readonly AuditService _audit;
    private readonly ILogger<OutcomeEvaluationService> _logger;

    private readonly int _windowDays;
    private readonly decimal _neutralBandPct;
    private readonly int _graceDays;

    public OutcomeEvaluationService(
        PricingToolDbContext db, AuditService audit, ILogger<OutcomeEvaluationService> logger,
        IOptions<PricingEngineOptions> options)
    {
        _db = db;
        _audit = audit;
        _logger = logger;
        _windowDays = DefaultWindowDays;
        _neutralBandPct = DefaultNeutralBandPct;
        _graceDays = options.Value.OutcomeMaturityGraceDays;
    }

    /// <summary>
    /// Creates/refreshes outcome rows for every pushed-and-changed proposal and finalises any whose
    /// post-window has matured as of the latest snapshot. Returns the number finalised this pass.
    /// </summary>
    public async Task<int> EvaluateAsync(PricingRun run, CancellationToken ct = default)
    {
        try
        {
            return await EvaluateCoreAsync(run, ct);
        }
        catch
        {
            // A mid-pass failure must not leave half-built outcome rows tracked on the shared scoped
            // DbContext: on the scheduler path ScheduleService saves the same context in a finally,
            // which would otherwise flush these orphans with no audit trail. Discard them.
            foreach (var entry in _db.ChangeTracker.Entries<PriceChangeOutcome>().ToList())
                if (entry.State is EntityState.Added or EntityState.Modified)
                    entry.State = EntityState.Detached;
            throw;
        }
    }

    private async Task<int> EvaluateCoreAsync(PricingRun run, CancellationToken ct)
    {
        var asOf = await _db.DailySnapshots.AsNoTracking()
            .Where(s => s.LayerId == run.LayerId)
            .MaxAsync(s => (DateTime?)s.SnapshotDate, ct);
        if (asOf is null) return 0;

        // Expire stale Pending outcomes first — independent of whether anything was pushed this run
        // (most human-gated runs push nothing, yet stale outcomes must still be cleared).
        var expired = await ExpireStalePendingAsync(run, asOf.Value, ct);

        var pushedRaw = await _db.ProposedPrices.AsNoTracking()
            .Where(p => p.LayerId == run.LayerId && p.Status == ProposalStatus.Pushed && p.PushedUtc != null && p.HasChange)
            .Select(p => new
            {
                p.Id, p.PricingRunId, p.Sku, p.PriceBandId,
                p.CurrentPrice, p.ProposedPriceValue, p.ChangePct, p.ReasonCodes, p.PushedUtc,
            })
            .ToListAsync(ct);
        if (pushedRaw.Count == 0)
        {
            if (expired > 0) await _db.SaveChangesAsync(ct);
            return 0;
        }

        var ids = pushedRaw.Select(p => p.Id).ToList();
        var existing = await _db.PriceChangeOutcomes
            .Where(o => o.ProposedPriceId != null && ids.Contains(o.ProposedPriceId.Value))
            .ToListAsync(ct);
        var byProposal = existing.ToDictionary(o => o.ProposedPriceId!.Value);

        // Batch-load every pushed SKU's snapshot history once, ordered by date, and do the per-proposal
        // pre/post lookups in memory. (Previously this issued two queries PER pushed proposal — an N+1
        // that becomes tens of thousands of round-trips once managers push at full-catalog scale.)
        var pushedSkus = pushedRaw.Select(p => p.Sku).Distinct().ToList();
        // Lower-bound the scan so it never loads a SKU's entire history (years at daily cadence), which
        // would negate retention's benefit and risk OOM at full-catalog scale. Anchor to asOf, not the
        // oldest push: any outcome still gradeable has a push date ≥ asOf − (window+grace) (older ones
        // were just expired above), and its pre-row sits at most a few days before that — so reaching
        // back the full expiry horizon from asOf both stays bounded to the retained window AND can't drop
        // a valid pre/post-row, regardless of how old a still-Pushed proposal is.
        var oldestNeeded = asOf.Value.AddDays(-(_windowDays + _graceDays + 1));
        var historyBySku = (await _db.DailySnapshots.AsNoTracking()
                .Where(s => s.LayerId == run.LayerId && s.SnapshotDate >= oldestNeeded && pushedSkus.Contains(s.Sku))
                .Select(s => new { s.Sku, s.SnapshotDate, s.Qty7, s.Net7, s.Pptcv })
                .ToListAsync(ct))
            .GroupBy(s => s.Sku, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.SnapshotDate).ToList(), StringComparer.OrdinalIgnoreCase);

        var finalised = 0;
        foreach (var p in pushedRaw)
        {
            byProposal.TryGetValue(p.Id, out var outcome);
            if (outcome is { Verdict: not OutcomeVerdict.Pending }) continue; // already graded

            var d0 = p.PushedUtc!.Value.Date;
            var postDate = d0.AddDays(_windowDays);

            historyBySku.TryGetValue(p.Sku, out var history);

            // "Pre" = the latest snapshot on/before D0 (the 7 days leading up to the change).
            // History is ordered ascending by date, so the last match ≤ D0 is the most recent.
            var preRow = history?.LastOrDefault(s => s.SnapshotDate <= d0);
            if (preRow is null) continue; // no pre-window history to anchor against
            var pre = new SnapPoint(preRow.Qty7, preRow.Net7, preRow.Pptcv);

            if (outcome is null)
            {
                outcome = new PriceChangeOutcome
                {
                    LayerId = run.LayerId,
                    ProposedPriceId = p.Id,
                    Sku = p.Sku,
                    SourceRunId = p.PricingRunId,
                    PriceBandId = p.PriceBandId,
                    AppliedUtc = p.PushedUtc.Value,
                    Direction = p.ChangePct > 0 ? ChangeDirection.Up : ChangeDirection.Down,
                    Intent = IntentOf(p.ChangePct, p.ReasonCodes),
                    OldPrice = p.CurrentPrice, // the live price before the change (what customers were paying)
                    NewPrice = p.ProposedPriceValue,
                    WindowDays = _windowDays,
                    Verdict = OutcomeVerdict.Pending,
                };
                _db.PriceChangeOutcomes.Add(outcome);
            }

            outcome.PreUnitsPerDay = UnitsPerDay(pre.Qty7);
            outcome.PreGrossProfitPerDay = GrossProfitPerDay(pre.Qty7, pre.Net7, pre.Pptcv);
            outcome.PreMarginPct = MarginPct(pre.Qty7, pre.Net7, pre.Pptcv);

            // "Post" = the first snapshot on/after D0 + WindowDays. Null ⇒ window not matured yet.
            var postRow = history?.FirstOrDefault(s => s.SnapshotDate >= postDate);
            if (postRow is null) continue; // still in flight — leave Pending
            var post = new SnapPoint(postRow.Qty7, postRow.Net7, postRow.Pptcv);

            var postCost = post.Pptcv ?? pre.Pptcv; // fall back to the known cost if the later pull lost it
            outcome.PostUnitsPerDay = UnitsPerDay(post.Qty7);
            outcome.PostGrossProfitPerDay = GrossProfitPerDay(post.Qty7, post.Net7, postCost);
            outcome.PostMarginPct = MarginPct(post.Qty7, post.Net7, postCost);

            var (verdict, note) = Grade(
                outcome.Intent,
                outcome.PreUnitsPerDay, outcome.PostUnitsPerDay.Value,
                outcome.PreGrossProfitPerDay, outcome.PostGrossProfitPerDay,
                _neutralBandPct);
            outcome.Verdict = verdict;
            outcome.Note = note;
            outcome.MeasuredUtc = DateTime.UtcNow;
            outcome.MeasuredOnRunId = run.Id;
            finalised++;
        }

        await _db.SaveChangesAsync(ct);

        if (finalised > 0)
        {
            await _audit.LogAsync(run.TriggeredBy, AuditCategories.Run,
                $"Evaluated {finalised} matured price-change outcome(s)",
                nameof(PriceChangeOutcome), run.Id.ToString(), layerId: run.LayerId, ct: ct);
            _logger.LogInformation("Finalised {Count} price-change outcomes for run {RunId}.", finalised, run.Id);
        }
        return finalised;
    }

    /// <summary>
    /// Forces every Pending outcome whose change has aged past the measurement window + grace to a
    /// terminal Neutral. By that age its anchoring pre/post snapshots have normally either never
    /// materialised (the SKU left the catalog) or aged out of retention, so it is treated as
    /// indeterminate rather than left Pending forever. This is a deliberate give-up cutoff, not a proof
    /// that grading is impossible: a post-snapshot that first appears after the cutoff is not
    /// re-considered (an accepted v1 trade-off). Resolving it stops the forever-Pending re-scan and
    /// clears it from the dashboard's open set. Measured against <paramref name="asOf"/> (the latest
    /// snapshot date) so it advances with the same run cadence that drives retention. Returns the count
    /// expired; the caller owns the SaveChanges.
    /// </summary>
    private async Task<int> ExpireStalePendingAsync(PricingRun run, DateTime asOf, CancellationToken ct)
    {
        var staleCutoff = asOf.AddDays(-(_windowDays + _graceDays));
        var stalePending = await _db.PriceChangeOutcomes
            .Where(o => o.LayerId == run.LayerId
                     && o.Verdict == OutcomeVerdict.Pending
                     && o.AppliedUtc < staleCutoff)
            .ToListAsync(ct);
        foreach (var o in stalePending)
        {
            o.Verdict = OutcomeVerdict.Neutral;
            o.Note = "Measurement window expired (snapshot data aged out)";
            o.MeasuredUtc = DateTime.UtcNow;
            o.MeasuredOnRunId = run.Id;
        }
        if (stalePending.Count > 0)
            _logger.LogInformation(
                "Expired {Count} stale pending outcome(s) to Neutral for run {RunId}.", stalePending.Count, run.Id);
        return stalePending.Count;
    }

    /// <summary>
    /// Verdict logic, pure and side-effect-free so it can be unit-tested directly. Judges by intent:
    /// margin captures on profit retention, volume plays on units (with a profit guard), clearance on
    /// sell-through. <paramref name="neutralBandPct"/> is the relative dead-zone around no change.
    /// </summary>
    public static (OutcomeVerdict Verdict, string? Note) Grade(
        ChangeIntent intent,
        decimal preUnitsPerDay, decimal postUnitsPerDay,
        decimal? preGrossProfitPerDay, decimal? postGrossProfitPerDay,
        decimal neutralBandPct)
    {
        var eps = neutralBandPct;
        var u = RelDeltaPct(preUnitsPerDay, postUnitsPerDay);
        decimal? p = preGrossProfitPerDay is decimal pre && postGrossProfitPerDay is decimal post
            ? RelDeltaPct(pre, post)
            : null;

        switch (intent)
        {
            case ChangeIntent.MarginCapture:
                if (p is null) return (OutcomeVerdict.Neutral, "Profit not measurable (cost missing)");
                if (p >= eps) return (OutcomeVerdict.Win, null);
                if (p <= -eps) return (OutcomeVerdict.Backfire, "Higher price lost more volume than the margin it gained");
                return (OutcomeVerdict.Neutral, null);

            case ChangeIntent.VolumeStimulation:
                if (u < eps) return (OutcomeVerdict.Backfire, "Discount did not lift units — margin given away");
                if (p is null) return (OutcomeVerdict.Win, "Units rose (profit not measurable)");
                if (p >= -eps) return (OutcomeVerdict.Win, null);
                return (OutcomeVerdict.Neutral, "Units rose but gross profit fell");

            case ChangeIntent.Clearance:
                if (preUnitsPerDay == 0m)
                    return postUnitsPerDay > 0m
                        ? (OutcomeVerdict.Win, "Stock began moving")
                        : (OutcomeVerdict.Backfire, "Still not selling");
                if (u >= eps) return (OutcomeVerdict.Win, null);
                if (u <= 0m) return (OutcomeVerdict.Backfire, "Markdown did not move stock");
                return (OutcomeVerdict.Neutral, null);

            default:
                return (OutcomeVerdict.Neutral, null);
        }
    }

    /// <summary>Cohort for a change: price up = margin capture; price down = clearance (dead/aging) or volume.</summary>
    public static ChangeIntent IntentOf(decimal changePct, string reasonCodes)
    {
        if (changePct > 0) return ChangeIntent.MarginCapture;
        var codes = reasonCodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (codes.Contains(DeadStockMarkdownCode) || codes.Contains(StockAgingCode))
            return ChangeIntent.Clearance;
        return ChangeIntent.VolumeStimulation;
    }

    private static decimal UnitsPerDay(int qty7) => qty7 / 7m;

    private static decimal? GrossProfitPerDay(int qty7, decimal net7, decimal? cost) =>
        cost is decimal c ? (net7 - c * qty7) / 7m : null;

    private static decimal? MarginPct(int qty7, decimal net7, decimal? cost) =>
        cost is decimal c && net7 > 0m ? (net7 - c * qty7) / net7 * 100m : null;

    /// <summary>
    /// Relative change pre→post as a percent of |pre|. When pre is zero we can't divide, so we
    /// return a large signed sentinel (a move from nothing to something reads as a big swing).
    /// </summary>
    private static decimal RelDeltaPct(decimal pre, decimal post)
    {
        if (pre == 0m) return post == 0m ? 0m : post > 0m ? 1000m : -1000m;
        return (post - pre) / Math.Abs(pre) * 100m;
    }

    private sealed record SnapPoint(int Qty7, decimal Net7, decimal? Pptcv);
}
