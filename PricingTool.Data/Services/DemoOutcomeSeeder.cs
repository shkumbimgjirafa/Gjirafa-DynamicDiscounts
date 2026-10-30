using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PricingTool.Data.Entities;

namespace PricingTool.Data.Services;

/// <summary>
/// DEMO-ONLY: makes the "Did the bet pay off?" dashboard section non-empty without any manual
/// clicking. On first demo boot (idempotent) it runs one pricing run to create proposals, marks a
/// deterministic, intent-balanced slice of them Approved-&gt;Pushed with push dates in the recent past,
/// then evaluates so their outcomes mature against the backfilled snapshot history.
///
/// Illustrative only: demo sales data is not causally responsive to price, so the verdicts are a
/// realistic-looking spread, not a real measurement.
/// </summary>
public class DemoOutcomeSeeder
{
    /// <summary>Max changed proposals pushed per intent — enough to fill all three tiles without flooding.</summary>
    public const int SamplePerIntent = 20;

    private readonly PricingToolDbContext _db;
    private readonly PricingRunOrchestrator _orchestrator;
    private readonly OutcomeEvaluationService _outcomes;
    private readonly ILogger<DemoOutcomeSeeder> _logger;

    public DemoOutcomeSeeder(
        PricingToolDbContext db, PricingRunOrchestrator orchestrator,
        OutcomeEvaluationService outcomes, ILogger<DemoOutcomeSeeder> logger)
    {
        _db = db;
        _orchestrator = orchestrator;
        _outcomes = outcomes;
        _logger = logger;
    }

    /// <summary>
    /// Idempotent one-time demo seed: run a pricing run, push a slice of its changes into the past,
    /// then grade them. No-op once any outcome (or any pushed proposal) already exists.
    /// </summary>
    public async Task EnsureSeededAsync(CancellationToken ct = default)
    {
        var layers = await _db.Layers.AsNoTracking()
            .Where(l => l.IsActive).OrderBy(l => l.SortOrder).ToListAsync(ct);

        foreach (var layer in layers)
        {
            if (await _db.PriceChangeOutcomes.AnyAsync(o => o.LayerId == layer.Id, ct)) continue;
            if (await _db.ProposedPrices.AnyAsync(p => p.LayerId == layer.Id && p.Status == ProposalStatus.Pushed, ct)) continue;

            try
            {
                // A pure snapshot backfill leaves zero proposals — one run creates them (and the
                // dashboard's "Last run"). Its trailing evaluation grades nothing yet (nothing pushed).
                var run = await _orchestrator.ExecuteRunAsync("demo-seed", isOnDemand: true, layer.Id, ct);

                var pushed = await ApplyDemoPushesAsync(_db, run.Id, ct);
                if (pushed > 0)
                {
                    await _outcomes.EvaluateAsync(run, ct);
                    _logger.LogInformation("Demo mode: pushed {Count} historical proposals for layer {Layer} and graded their outcomes.", pushed, layer.DisplayName);
                }
            }
            catch (InvalidOperationException)
            {
                // A run is already in progress (e.g. Web + Engine racing on first boot) — retry next boot.
            }
            catch (Exception ex)
            {
                // Best-effort, illustrative-only seeding must NEVER take down app startup.
                _logger.LogWarning(ex, "Demo outcome seeding failed for layer {Layer}; its \"Did the bet pay off?\" section will be empty until the next successful boot.", layer.DisplayName);
            }
        }
    }

    /// <summary>
    /// Marks a deterministic, intent-balanced slice of a run's CHANGED proposals as Approved-&gt;Pushed,
    /// dating each push 9..22 days back so both the pre (&lt;= D0) and the +7d post window fall inside the
    /// 35-day backfill. Idempotent per run (no-op if that run already has pushed proposals). Returns the
    /// number pushed.
    /// </summary>
    public static async Task<int> ApplyDemoPushesAsync(PricingToolDbContext db, long runId, CancellationToken ct = default)
    {
        if (await db.ProposedPrices.AnyAsync(p => p.PricingRunId == runId && p.Status == ProposalStatus.Pushed, ct))
            return 0;

        var changed = await db.ProposedPrices
            .Where(p => p.PricingRunId == runId && p.HasChange && p.Status == ProposalStatus.Pending)
            .OrderBy(p => p.Sku)
            .ToListAsync(ct);

        // Up to N per intent so MarginCapture / VolumeStimulation / Clearance tiles all populate.
        var selected = changed
            .GroupBy(p => OutcomeEvaluationService.IntentOf(p.ChangePct, p.ReasonCodes))
            .SelectMany(g => g.Take(SamplePerIntent))
            .OrderBy(p => p.Sku)
            .ToList();

        var today = DateTime.UtcNow.Date;
        var now = DateTime.UtcNow;
        var i = 0;
        foreach (var p in selected)
        {
            p.Status = ProposalStatus.Pushed;
            p.ReviewedBy = "demo-seed";
            p.ReviewedUtc = now;
            p.PushedUtc = today.AddDays(-(9 + i % 14)); // spread across 9..22 days ago, deterministically
            i++;
        }

        await db.SaveChangesAsync(ct);
        return selected.Count;
    }
}
