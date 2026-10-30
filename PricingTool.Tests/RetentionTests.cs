using Microsoft.EntityFrameworkCore;
using PricingTool.Core.Options;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;

namespace PricingTool.Tests;

/// <summary>
/// Step 2 (retention/TTL) + its baseline-pin companion. Drives the batched-delete methods through the
/// EF-backed <see cref="EfBulkWriter"/> double (the production SqlBulkCopy/raw-SQL path can't target the
/// in-memory provider), and asserts the DailyLayerKpi rollup survives a snapshot prune.
/// </summary>
public class RetentionTests
{
    private static PricingToolDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PricingToolDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DailySnapshot Snap(DateTime date, string sku = "SKU-1") => new()
    {
        LayerId = 1, SnapshotDate = date, Sku = sku, Qty7 = 7, Net7 = 50m, Pptcv = 5m,
    };

    [Fact]
    public async Task DeleteSnapshotsBeforeDate_RemovesOlder_KeepsNewer()
    {
        using var db = NewDb();
        db.DailySnapshots.AddRange(
            Snap(new DateTime(2026, 5, 1)),   // 40d before cutoff → deleted
            Snap(new DateTime(2026, 5, 20)),  // on the cutoff boundary → kept (strict <)
            Snap(new DateTime(2026, 6, 1)));  // newer → kept
        await db.SaveChangesAsync();

        var deleted = await new EfBulkWriter(db).DeleteSnapshotsBeforeDateAsync(new DateTime(2026, 5, 20));

        Assert.Equal(1, deleted);
        var remaining = await db.DailySnapshots.AsNoTracking().Select(s => s.SnapshotDate).OrderBy(d => d).ToListAsync();
        Assert.Equal(new[] { new DateTime(2026, 5, 20), new DateTime(2026, 6, 1) }, remaining);
    }

    [Fact]
    public async Task DeleteProposalsBeforeRunDate_RemovesOldRunProposals_AndCascadesVotes_KeepsRecent()
    {
        using var db = NewDb();
        var oldRun = new PricingRun { LayerId = 1, StartedUtc = new DateTime(2026, 5, 1), Status = RunStatus.Succeeded };
        var newRun = new PricingRun { LayerId = 1, StartedUtc = new DateTime(2026, 6, 1), Status = RunStatus.Succeeded };
        db.PricingRuns.AddRange(oldRun, newRun);
        await db.SaveChangesAsync();

        var oldProp = new ProposedPrice { PricingRunId = oldRun.Id, LayerId = 1, Sku = "OLD" };
        oldProp.Votes.Add(new AlgorithmVoteRecord { AlgorithmCode = "SELL_THROUGH" });
        var newProp = new ProposedPrice { PricingRunId = newRun.Id, LayerId = 1, Sku = "NEW" };
        newProp.Votes.Add(new AlgorithmVoteRecord { AlgorithmCode = "SELL_THROUGH" });
        db.ProposedPrices.AddRange(oldProp, newProp);
        await db.SaveChangesAsync();

        var deletedProposals = await new EfBulkWriter(db).DeleteProposalsBeforeRunDateAsync(new DateTime(2026, 5, 20));

        Assert.Equal(1, deletedProposals);
        Assert.Equal(new[] { "NEW" }, await db.ProposedPrices.AsNoTracking().Select(p => p.Sku).ToListAsync());
        // The old proposal's vote must be gone (cascade); the recent one's must remain.
        Assert.Equal(1, await db.AlgorithmVotes.CountAsync());
        // Runs themselves are deleted by a separate call (DeleteRunsBeforeDateAsync) — untouched here.
        Assert.Equal(2, await db.PricingRuns.CountAsync());
    }

    [Fact]
    public async Task DeleteProposalsBeforeRunDate_DetachesReferencingOutcomes_InsteadOfBlocking()
    {
        using var db = NewDb();
        var oldRun = new PricingRun { LayerId = 1, StartedUtc = new DateTime(2026, 5, 1), Status = RunStatus.Succeeded };
        // A newer run, so the old one is not the layer's newest (which retention always keeps).
        var newRun = new PricingRun { LayerId = 1, StartedUtc = new DateTime(2026, 6, 1), Status = RunStatus.Succeeded };
        db.PricingRuns.AddRange(oldRun, newRun);
        await db.SaveChangesAsync();
        var prop = new ProposedPrice { PricingRunId = oldRun.Id, LayerId = 1, Sku = "OLD" };
        db.ProposedPrices.Add(prop);
        await db.SaveChangesAsync();
        // A graded outcome pointing at the proposal (nullable NoAction FK — would throw FK 547 in prod
        // if the proposal were deleted without detaching it first).
        db.PriceChangeOutcomes.Add(new PriceChangeOutcome
        {
            LayerId = 1, ProposedPriceId = prop.Id, Sku = "OLD",
            AppliedUtc = new DateTime(2026, 5, 1), Verdict = OutcomeVerdict.Win, WindowDays = 7,
        });
        await db.SaveChangesAsync();

        var deleted = await new EfBulkWriter(db).DeleteProposalsBeforeRunDateAsync(new DateTime(2026, 5, 20));

        Assert.Equal(1, deleted);
        Assert.Equal(0, await db.ProposedPrices.CountAsync());
        var surviving = await db.PriceChangeOutcomes.AsNoTracking().SingleAsync();
        Assert.Null(surviving.ProposedPriceId);                  // detached, not orphaned/blocking
        Assert.Equal(OutcomeVerdict.Win, surviving.Verdict);     // outcome survives with its verdict
    }

    [Fact]
    public async Task DeleteRunsBeforeDate_RemovesOlder_KeepsNewer()
    {
        using var db = NewDb();
        db.PricingRuns.AddRange(
            new PricingRun { LayerId = 1, StartedUtc = new DateTime(2026, 5, 1), Status = RunStatus.Succeeded },
            new PricingRun { LayerId = 1, StartedUtc = new DateTime(2026, 6, 1), Status = RunStatus.Succeeded });
        await db.SaveChangesAsync();

        var deleted = await new EfBulkWriter(db).DeleteRunsBeforeDateAsync(new DateTime(2026, 5, 20));

        Assert.Equal(1, deleted);
        Assert.Equal(new[] { new DateTime(2026, 6, 1) },
            await db.PricingRuns.AsNoTracking().Select(r => r.StartedUtc).ToListAsync());
    }

    [Fact]
    public async Task ProposalAndRunRetention_NeverDeletesALayersNewestSucceededRun()
    {
        using var db = NewDb();
        var oldL1 = new PricingRun { LayerId = 1, StartedUtc = new DateTime(2026, 5, 1), Status = RunStatus.Succeeded };
        var newL1 = new PricingRun { LayerId = 1, StartedUtc = new DateTime(2026, 6, 1), Status = RunStatus.Succeeded };
        // Layer 2 stopped running: its only succeeded run is past the cutoff, and a later failed run doesn't count.
        var onlyL2 = new PricingRun { LayerId = 2, StartedUtc = new DateTime(2026, 5, 1), Status = RunStatus.Succeeded };
        var failedL2 = new PricingRun { LayerId = 2, StartedUtc = new DateTime(2026, 5, 2), Status = RunStatus.Failed };
        db.PricingRuns.AddRange(oldL1, newL1, onlyL2, failedL2);
        await db.SaveChangesAsync();
        db.ProposedPrices.AddRange(
            new ProposedPrice { PricingRunId = oldL1.Id, LayerId = 1, Sku = "L1-OLD" },
            new ProposedPrice { PricingRunId = newL1.Id, LayerId = 1, Sku = "L1-NEW" },
            new ProposedPrice { PricingRunId = onlyL2.Id, LayerId = 2, Sku = "L2-ONLY" });
        await db.SaveChangesAsync();

        var writer = new EfBulkWriter(db);
        var cutoff = new DateTime(2026, 5, 20);
        Assert.Equal(1, await writer.DeleteProposalsBeforeRunDateAsync(cutoff));
        Assert.Equal(2, await writer.DeleteRunsBeforeDateAsync(cutoff));        // oldL1 + failedL2

        Assert.Equal(new[] { "L1-NEW", "L2-ONLY" },
            await db.ProposedPrices.AsNoTracking().Select(p => p.Sku).OrderBy(x => x).ToListAsync());
        Assert.Equal(new[] { newL1.Id, onlyL2.Id }.OrderBy(x => x),
            await db.PricingRuns.AsNoTracking().Select(r => r.Id).OrderBy(x => x).ToListAsync());
    }

    [Fact]
    public async Task SnapshotRetention_NeverDeletesALayersNewestDay()
    {
        using var db = NewDb();
        db.DailySnapshots.AddRange(
            Snap(new DateTime(2026, 5, 1)),
            Snap(new DateTime(2026, 6, 1)),
            new DailySnapshot { LayerId = 2, SnapshotDate = new DateTime(2026, 4, 1), Sku = "SKU-1" },
            new DailySnapshot { LayerId = 2, SnapshotDate = new DateTime(2026, 5, 1), Sku = "SKU-1" });  // layer 2's newest day
        await db.SaveChangesAsync();

        Assert.Equal(2, await new EfBulkWriter(db).DeleteSnapshotsBeforeDateAsync(new DateTime(2026, 5, 20)));

        var left = await db.DailySnapshots.AsNoTracking().OrderBy(s => s.LayerId).ThenBy(s => s.SnapshotDate)
            .Select(s => $"{s.LayerId}:{s.SnapshotDate:yyyy-MM-dd}").ToListAsync();
        Assert.Equal(new[] { "1:2026-06-01", "2:2026-05-01" }, left);
    }

    [Fact]
    public async Task DailyLayerKpi_SurvivesSnapshotRetention_PreservingTheBaseline()
    {
        using var db = NewDb();
        // 100 days of snapshots AND their matching KPI rollup rows, oldest first.
        var start = new DateTime(2026, 1, 1);
        for (var i = 0; i < 100; i++)
        {
            var date = start.AddDays(i);
            db.DailySnapshots.Add(Snap(date));
            db.DailyLayerKpi.Add(new DailyLayerKpi
            {
                LayerId = 1, Date = date, Net7 = 50m, Qty7 = 7, CostedNet7 = 50m, Cost7 = 35m, SkuCount = 1,
            });
        }
        await db.SaveChangesAsync();
        var baselineBefore = await db.DailyLayerKpi.AsNoTracking().OrderBy(k => k.Date).Select(k => k.Date).FirstAsync();

        // Prune all snapshots older than the last 35 days.
        await new EfBulkWriter(db).DeleteSnapshotsBeforeDateAsync(start.AddDays(65));

        // Snapshots were trimmed, but the KPI rollup (and therefore the earliest = pre-tool baseline) is intact.
        Assert.True(await db.DailySnapshots.CountAsync() < 100);
        Assert.Equal(100, await db.DailyLayerKpi.CountAsync());
        var baselineAfter = await db.DailyLayerKpi.AsNoTracking().OrderBy(k => k.Date).Select(k => k.Date).FirstAsync();
        Assert.Equal(baselineBefore, baselineAfter);
        Assert.Equal(start, baselineAfter);
    }

    [Fact]
    public void OutcomeExpiry_IsAlignedAtOrBeyondTheRetentionWindow_ByDefault()
    {
        var o = new PricingEngineOptions();
        // Expiry fires at window + grace; it must be ≥ the retention window so we never force-Neutral
        // an outcome whose anchoring snapshots still exist.
        Assert.True(OutcomeEvaluationService.DefaultWindowDays + o.OutcomeMaturityGraceDays >= o.SnapshotRetentionDays);
    }
}
