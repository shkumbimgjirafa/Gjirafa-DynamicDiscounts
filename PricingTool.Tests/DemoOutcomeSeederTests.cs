using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PricingTool.Core.Options;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;

namespace PricingTool.Tests;

public class DemoOutcomeSeederTests
{
    private static PricingToolDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PricingToolDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task ApplyDemoPushes_PushesIntentBalancedSlice_IntoThePast_ThenOutcomesMature()
    {
        using var db = NewDb();
        var today = DateTime.UtcNow.Date;
        var run = SeedRun(db);

        // One changed proposal per intent (price up = MarginCapture; down = Volume; down+dead-stock = Clearance).
        AddChangedProposal(db, run.Id, "SKU-UP", changePct: 15m, reason: "DISCOUNT_WASTED");
        AddChangedProposal(db, run.Id, "SKU-DN", changePct: -15m, reason: "VELOCITY_FORECAST");
        AddChangedProposal(db, run.Id, "SKU-DEAD", changePct: -25m, reason: "DEAD_STOCK_MARKDOWN");
        foreach (var sku in new[] { "SKU-UP", "SKU-DN", "SKU-DEAD" }) AddHistory(db, sku, today);
        db.SaveChanges();

        var pushed = await DemoOutcomeSeeder.ApplyDemoPushesAsync(db, run.Id);

        Assert.Equal(3, pushed);
        var pushedProps = await db.ProposedPrices.AsNoTracking()
            .Where(p => p.Status == ProposalStatus.Pushed).ToListAsync();
        Assert.Equal(3, pushedProps.Count);
        // every push is dated in the recent past so its pre/post windows fall inside the backfill
        Assert.All(pushedProps, p =>
        {
            Assert.NotNull(p.PushedUtc);
            Assert.True(p.PushedUtc!.Value.Date < today && p.PushedUtc.Value.Date >= today.AddDays(-22));
        });

        // Grading runs against the historical snapshots and matures every pushed change.
        await new OutcomeEvaluationService(db, new AuditService(db), NullLogger<OutcomeEvaluationService>.Instance,
                Options.Create(new PricingEngineOptions()))
            .EvaluateAsync(run);

        var outcomes = await db.PriceChangeOutcomes.AsNoTracking().ToListAsync();
        Assert.Equal(3, outcomes.Count);
        Assert.All(outcomes, o => Assert.NotEqual(OutcomeVerdict.Pending, o.Verdict));
        Assert.Contains(outcomes, o => o.Intent == ChangeIntent.MarginCapture);
        Assert.Contains(outcomes, o => o.Intent == ChangeIntent.VolumeStimulation);
        Assert.Contains(outcomes, o => o.Intent == ChangeIntent.Clearance);
    }

    [Fact]
    public async Task ApplyDemoPushes_IsIdempotentPerRun()
    {
        using var db = NewDb();
        var run = SeedRun(db);
        AddChangedProposal(db, run.Id, "SKU-DN", changePct: -15m, reason: "VELOCITY_FORECAST");
        db.SaveChanges();

        var first = await DemoOutcomeSeeder.ApplyDemoPushesAsync(db, run.Id);
        var second = await DemoOutcomeSeeder.ApplyDemoPushesAsync(db, run.Id);

        Assert.Equal(1, first);
        Assert.Equal(0, second); // second pass finds the run already pushed — no duplicates
        Assert.Equal(1, await db.ProposedPrices.CountAsync(p => p.Status == ProposalStatus.Pushed));
    }

    [Fact]
    public async Task ApplyDemoPushes_OnlySelectsChangedProposals()
    {
        using var db = NewDb();
        var run = SeedRun(db);
        AddChangedProposal(db, run.Id, "SKU-CHANGED", changePct: -15m, reason: "VELOCITY_FORECAST");
        // an unchanged proposal must never be pushed
        db.ProposedPrices.Add(new ProposedPrice
        {
            PricingRunId = run.Id, Sku = "SKU-FLAT", OldPrice = 100m, CurrentPrice = 100m,
            ProposedPriceValue = 100m, ChangePct = 0m, HasChange = false, Status = ProposalStatus.Pending,
        });
        db.SaveChanges();

        var pushed = await DemoOutcomeSeeder.ApplyDemoPushesAsync(db, run.Id);

        Assert.Equal(1, pushed);
        Assert.False(await db.ProposedPrices.AnyAsync(p => p.Sku == "SKU-FLAT" && p.Status == ProposalStatus.Pushed));
    }

    private static PricingRun SeedRun(PricingToolDbContext db)
    {
        var run = new PricingRun { StartedUtc = DateTime.UtcNow, Status = RunStatus.Succeeded };
        db.PricingRuns.Add(run);
        db.SaveChanges();
        return run;
    }

    private static void AddChangedProposal(
        PricingToolDbContext db, long runId, string sku, decimal changePct, string reason) =>
        db.ProposedPrices.Add(new ProposedPrice
        {
            PricingRunId = runId, Sku = sku, OldPrice = 100m, CurrentPrice = 100m,
            ProposedPriceValue = 100m + changePct, ChangePct = changePct, HasChange = true,
            ReasonCodes = reason, Status = ProposalStatus.Pending,
        });

    // 30 days of snapshots ending yesterday, with rising units so a pre/post window yields a clear verdict.
    private static void AddHistory(PricingToolDbContext db, string sku, DateTime today)
    {
        for (var offset = 30; offset >= 1; offset--)
        {
            var qty7 = 7 + (30 - offset);
            db.DailySnapshots.Add(new DailySnapshot
            {
                SnapshotDate = today.AddDays(-offset), Sku = sku,
                Qty7 = qty7, Net7 = qty7 * 100m, Pptcv = 50m,
            });
        }
    }
}
