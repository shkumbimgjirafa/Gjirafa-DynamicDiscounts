using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PricingTool.Core.Options;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;

namespace PricingTool.Tests;

public class OutcomeEvaluationServiceTests
{
    private const decimal Eps = OutcomeEvaluationService.DefaultNeutralBandPct;

    private static PricingToolDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PricingToolDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static OutcomeEvaluationService NewService(PricingToolDbContext db, PricingEngineOptions? options = null) =>
        new(db, new AuditService(db), NullLogger<OutcomeEvaluationService>.Instance,
            Options.Create(options ?? new PricingEngineOptions()));

    // ---------------------------------------------------------------- Grade (pure verdict logic)

    [Fact]
    public void Grade_MarginCapture_ProfitUp_IsWin()
    {
        var (verdict, _) = OutcomeEvaluationService.Grade(
            ChangeIntent.MarginCapture, preUnitsPerDay: 10m, postUnitsPerDay: 9m,
            preGrossProfitPerDay: 100m, postGrossProfitPerDay: 110m, Eps);
        Assert.Equal(OutcomeVerdict.Win, verdict);
    }

    [Fact]
    public void Grade_MarginCapture_ProfitDropped_IsBackfire()
    {
        var (verdict, note) = OutcomeEvaluationService.Grade(
            ChangeIntent.MarginCapture, 10m, 6m, 100m, 88m, Eps);
        Assert.Equal(OutcomeVerdict.Backfire, verdict);
        Assert.NotNull(note);
    }

    [Fact]
    public void Grade_MarginCapture_ProfitFlat_IsNeutral()
    {
        var (verdict, _) = OutcomeEvaluationService.Grade(
            ChangeIntent.MarginCapture, 10m, 10m, 100m, 101m, Eps);
        Assert.Equal(OutcomeVerdict.Neutral, verdict);
    }

    [Fact]
    public void Grade_MarginCapture_NoCost_IsNeutral()
    {
        var (verdict, _) = OutcomeEvaluationService.Grade(
            ChangeIntent.MarginCapture, 10m, 10m, null, null, Eps);
        Assert.Equal(OutcomeVerdict.Neutral, verdict);
    }

    [Fact]
    public void Grade_VolumeStimulation_UnitsUp_ProfitHeld_IsWin()
    {
        var (verdict, _) = OutcomeEvaluationService.Grade(
            ChangeIntent.VolumeStimulation, 10m, 13m, 100m, 100m, Eps);
        Assert.Equal(OutcomeVerdict.Win, verdict);
    }

    [Fact]
    public void Grade_VolumeStimulation_UnitsFlat_IsBackfire_MarginGivenAway()
    {
        var (verdict, note) = OutcomeEvaluationService.Grade(
            ChangeIntent.VolumeStimulation, 10m, 10m, 100m, 80m, Eps);
        Assert.Equal(OutcomeVerdict.Backfire, verdict);
        Assert.Contains("did not lift units", note);
    }

    [Fact]
    public void Grade_VolumeStimulation_UnitsUp_ProfitFell_IsNeutral()
    {
        var (verdict, _) = OutcomeEvaluationService.Grade(
            ChangeIntent.VolumeStimulation, 10m, 13m, 100m, 80m, Eps);
        Assert.Equal(OutcomeVerdict.Neutral, verdict);
    }

    [Fact]
    public void Grade_Clearance_StockMoves_IsWin()
    {
        var (verdict, _) = OutcomeEvaluationService.Grade(
            ChangeIntent.Clearance, 2m, 5m, 10m, 8m, Eps);
        Assert.Equal(OutcomeVerdict.Win, verdict);
    }

    [Fact]
    public void Grade_Clearance_StillStuck_IsBackfire()
    {
        var (verdict, _) = OutcomeEvaluationService.Grade(
            ChangeIntent.Clearance, 5m, 4m, 20m, 14m, Eps);
        Assert.Equal(OutcomeVerdict.Backfire, verdict);
    }

    [Fact]
    public void Grade_Clearance_ZeroPre_ThenSells_IsWin()
    {
        var (verdict, _) = OutcomeEvaluationService.Grade(
            ChangeIntent.Clearance, 0m, 3m, null, null, Eps);
        Assert.Equal(OutcomeVerdict.Win, verdict);
    }

    [Fact]
    public void Grade_Clearance_ZeroPre_NeverSells_IsBackfire()
    {
        var (verdict, _) = OutcomeEvaluationService.Grade(
            ChangeIntent.Clearance, 0m, 0m, null, null, Eps);
        Assert.Equal(OutcomeVerdict.Backfire, verdict);
    }

    // --- eps boundary (inclusive/exclusive operators must not drift)

    [Fact]
    public void Grade_MarginCapture_ProfitUpExactlyEps_IsWin()
    {
        var (verdict, _) = OutcomeEvaluationService.Grade(
            ChangeIntent.MarginCapture, 10m, 10m, 100m, 103m, Eps); // +3% == eps → Win (>=)
        Assert.Equal(OutcomeVerdict.Win, verdict);
    }

    [Fact]
    public void Grade_MarginCapture_ProfitUpJustBelowEps_IsNeutral()
    {
        var (verdict, _) = OutcomeEvaluationService.Grade(
            ChangeIntent.MarginCapture, 10m, 10m, 100m, 102.99m, Eps); // +2.99% → Neutral
        Assert.Equal(OutcomeVerdict.Neutral, verdict);
    }

    [Fact]
    public void Grade_MarginCapture_ProfitDownExactlyEps_IsBackfire()
    {
        var (verdict, _) = OutcomeEvaluationService.Grade(
            ChangeIntent.MarginCapture, 10m, 10m, 100m, 97m, Eps); // -3% == -eps → Backfire (<=)
        Assert.Equal(OutcomeVerdict.Backfire, verdict);
    }

    [Fact]
    public void Grade_VolumeStimulation_UnitsUpExactlyEps_IsNotBackfire()
    {
        var (verdict, _) = OutcomeEvaluationService.Grade(
            ChangeIntent.VolumeStimulation, 100m, 103m, 100m, 100m, Eps); // u == eps, not < eps
        Assert.Equal(OutcomeVerdict.Win, verdict);
    }

    [Fact]
    public void Grade_VolumeStimulation_UnitsUpJustBelowEps_IsBackfire()
    {
        var (verdict, _) = OutcomeEvaluationService.Grade(
            ChangeIntent.VolumeStimulation, 100m, 102.99m, 100m, 100m, Eps); // u < eps
        Assert.Equal(OutcomeVerdict.Backfire, verdict);
    }

    [Fact]
    public void Grade_Clearance_UnitsUpExactlyEps_IsWin()
    {
        var (verdict, _) = OutcomeEvaluationService.Grade(
            ChangeIntent.Clearance, 100m, 103m, 10m, 10m, Eps); // u == eps → Win (>=)
        Assert.Equal(OutcomeVerdict.Win, verdict);
    }

    [Fact]
    public void Grade_Clearance_UnitsFlat_IsBackfire()
    {
        var (verdict, _) = OutcomeEvaluationService.Grade(
            ChangeIntent.Clearance, 100m, 100m, 10m, 10m, Eps); // u == 0 → Backfire (<= 0)
        Assert.Equal(OutcomeVerdict.Backfire, verdict);
    }

    // --- RelDeltaPct zero-pre sentinel (0 → something must read as a big positive move)

    [Fact]
    public void Grade_VolumeStimulation_ZeroPreUnits_ThenSells_IsWin()
    {
        var (verdict, _) = OutcomeEvaluationService.Grade(
            ChangeIntent.VolumeStimulation, 0m, 5m, 0m, 20m, Eps);
        Assert.Equal(OutcomeVerdict.Win, verdict);
    }

    [Fact]
    public void Grade_MarginCapture_ZeroPreProfit_ThenProfit_IsWin()
    {
        var (verdict, _) = OutcomeEvaluationService.Grade(
            ChangeIntent.MarginCapture, 10m, 10m, 0m, 12m, Eps);
        Assert.Equal(OutcomeVerdict.Win, verdict);
    }

    // ---------------------------------------------------------------- EvaluateAsync (DB wiring)

    [Fact]
    public async Task Evaluate_MaturedDiscount_ThatLiftedUnits_VerdictIsWin()
    {
        using var db = NewDb();
        var d0 = new DateTime(2026, 6, 1);
        var run = SeedRun(db);
        SeedPushedProposal(db, run.Id, "SKU-V", oldPrice: 10m, newPrice: 9m, changePct: -10m,
            reasonCodes: "VELOCITY_FORECAST", pushedUtc: d0);
        SeedSnapshot(db, "SKU-V", d0, qty7: 7, net7: 70m, pptcv: 5m);              // pre: 1/day, €5/day profit
        SeedSnapshot(db, "SKU-V", d0.AddDays(7), qty7: 21, net7: 189m, pptcv: 5m); // post: 3/day, €12/day profit
        db.SaveChanges();

        var finalised = await NewService(db).EvaluateAsync(run);

        Assert.Equal(1, finalised);
        var outcome = await db.PriceChangeOutcomes.AsNoTracking().SingleAsync();
        Assert.Equal(ChangeDirection.Down, outcome.Direction);
        Assert.Equal(ChangeIntent.VolumeStimulation, outcome.Intent);
        Assert.Equal(OutcomeVerdict.Win, outcome.Verdict);
        Assert.Equal(1m, outcome.PreUnitsPerDay);
        Assert.Equal(3m, outcome.PostUnitsPerDay);
        Assert.Equal(5m, outcome.PreGrossProfitPerDay);   // (70 - 5*7)/7
        Assert.Equal(12m, outcome.PostGrossProfitPerDay);  // (189 - 5*21)/7
        Assert.Equal(50m, outcome.PreMarginPct);           // (70 - 5*7)/70 * 100
        Assert.Equal(run.Id, outcome.MeasuredOnRunId);
        Assert.NotNull(outcome.MeasuredUtc);
    }

    [Fact]
    public async Task Evaluate_BeforeWindowMatures_LeavesOutcomePending()
    {
        using var db = NewDb();
        var d0 = new DateTime(2026, 6, 1);
        var run = SeedRun(db);
        SeedPushedProposal(db, run.Id, "SKU-P", oldPrice: 10m, newPrice: 9m, changePct: -10m,
            reasonCodes: "VELOCITY_FORECAST", pushedUtc: d0);
        SeedSnapshot(db, "SKU-P", d0, qty7: 7, net7: 70m, pptcv: 5m); // only the pre snapshot exists
        db.SaveChanges();

        var finalised = await NewService(db).EvaluateAsync(run);

        Assert.Equal(0, finalised);
        var outcome = await db.PriceChangeOutcomes.AsNoTracking().SingleAsync();
        Assert.Equal(OutcomeVerdict.Pending, outcome.Verdict);
        Assert.Equal(1m, outcome.PreUnitsPerDay); // pre is known immediately
        Assert.Null(outcome.PostUnitsPerDay);      // post is not
    }

    [Fact]
    public async Task Evaluate_DeadStockMarkdown_IsClassifiedAsClearance()
    {
        using var db = NewDb();
        var d0 = new DateTime(2026, 6, 1);
        var run = SeedRun(db);
        SeedPushedProposal(db, run.Id, "SKU-D", oldPrice: 20m, newPrice: 16m, changePct: -20m,
            reasonCodes: "DEAD_STOCK_MARKDOWN", pushedUtc: d0);
        SeedSnapshot(db, "SKU-D", d0, qty7: 0, net7: 0m, pptcv: 8m);              // pre: not selling
        SeedSnapshot(db, "SKU-D", d0.AddDays(7), qty7: 14, net7: 100m, pptcv: 8m); // post: moving now
        db.SaveChanges();

        await NewService(db).EvaluateAsync(run);

        var outcome = await db.PriceChangeOutcomes.AsNoTracking().SingleAsync();
        Assert.Equal(ChangeIntent.Clearance, outcome.Intent);
        Assert.Equal(OutcomeVerdict.Win, outcome.Verdict);
        Assert.Null(outcome.PreMarginPct);          // pre Net7 == 0 → margin undefined
        Assert.Equal(-12m, outcome.PostMarginPct);  // (100 - 8*14)/100 * 100
    }

    [Fact]
    public async Task Evaluate_DoesNotRegrade_AnAlreadyFinalisedOutcome()
    {
        using var db = NewDb();
        var d0 = new DateTime(2026, 6, 1);
        var run = SeedRun(db);
        SeedPushedProposal(db, run.Id, "SKU-V", oldPrice: 10m, newPrice: 9m, changePct: -10m,
            reasonCodes: "VELOCITY_FORECAST", pushedUtc: d0);
        SeedSnapshot(db, "SKU-V", d0, qty7: 7, net7: 70m, pptcv: 5m);
        SeedSnapshot(db, "SKU-V", d0.AddDays(7), qty7: 21, net7: 189m, pptcv: 5m);
        db.SaveChanges();

        var service = NewService(db);
        await service.EvaluateAsync(run);
        var secondPass = await service.EvaluateAsync(run);

        Assert.Equal(0, secondPass); // nothing left to finalise
        Assert.Equal(1, await db.PriceChangeOutcomes.CountAsync()); // no duplicate row
    }

    [Fact]
    public async Task Evaluate_MaturedPriceRise_ThatHeldProfit_IsMarginCaptureWin()
    {
        using var db = NewDb();
        var d0 = new DateTime(2026, 6, 1);
        var run = SeedRun(db);
        SeedPushedProposal(db, run.Id, "SKU-M", oldPrice: 10m, newPrice: 12m, changePct: 20m,
            reasonCodes: "DISCOUNT_WASTED", pushedUtc: d0);
        SeedSnapshot(db, "SKU-M", d0, qty7: 7, net7: 70m, pptcv: 5m);            // pre: €5/day profit
        SeedSnapshot(db, "SKU-M", d0.AddDays(7), qty7: 7, net7: 84m, pptcv: 5m); // post: €7/day (+40%)
        db.SaveChanges();

        await NewService(db).EvaluateAsync(run);

        var outcome = await db.PriceChangeOutcomes.AsNoTracking().SingleAsync();
        Assert.Equal(ChangeDirection.Up, outcome.Direction);
        Assert.Equal(ChangeIntent.MarginCapture, outcome.Intent);
        Assert.Equal(OutcomeVerdict.Win, outcome.Verdict);
    }

    [Fact]
    public async Task Evaluate_PostSnapshotMissingCost_FallsBackToPreCost()
    {
        using var db = NewDb();
        var d0 = new DateTime(2026, 6, 1);
        var run = SeedRun(db);
        SeedPushedProposal(db, run.Id, "SKU-FB", oldPrice: 10m, newPrice: 12m, changePct: 20m,
            reasonCodes: "DISCOUNT_WASTED", pushedUtc: d0);
        SeedSnapshot(db, "SKU-FB", d0, qty7: 7, net7: 70m, pptcv: 5m);
        SeedSnapshot(db, "SKU-FB", d0.AddDays(7), qty7: 7, net7: 84m, pptcv: null); // cost lost in later pull
        db.SaveChanges();

        await NewService(db).EvaluateAsync(run);

        var outcome = await db.PriceChangeOutcomes.AsNoTracking().SingleAsync();
        // Falls back to the pre cost (€5) instead of dropping to a "cost missing" Neutral.
        Assert.Equal(7m, outcome.PostGrossProfitPerDay); // (84 - 5*7)/7
        Assert.Equal(OutcomeVerdict.Win, outcome.Verdict);
    }

    [Fact]
    public async Task Evaluate_LossMakingClearance_StoresLargeNegativeMargin()
    {
        // Guards the margin-pct column width: a deep markdown with tiny net revenue against a high
        // cost basis yields a margin far beyond decimal(9,4)'s range (the original mapping bug).
        using var db = NewDb();
        var d0 = new DateTime(2026, 6, 1);
        var run = SeedRun(db);
        SeedPushedProposal(db, run.Id, "SKU-LOSS", oldPrice: 20m, newPrice: 16m, changePct: -20m,
            reasonCodes: "DEAD_STOCK_MARKDOWN", pushedUtc: d0);
        SeedSnapshot(db, "SKU-LOSS", d0, qty7: 30, net7: 10m, pptcv: 500m); // (10 - 500*30)/10 * 100
        db.SaveChanges();

        await NewService(db).EvaluateAsync(run);

        var outcome = await db.PriceChangeOutcomes.AsNoTracking().SingleAsync();
        Assert.Equal(-149900m, outcome.PreMarginPct); // 6 integer digits — would overflow decimal(9,4)
    }

    // ---------------------------------------------------------------- retention safety (Step 4)

    [Fact]
    public async Task Evaluate_GradesWithinWindow_EvenWithAncientHistoryBeforeTheScanBound()
    {
        using var db = NewDb();
        var d0 = new DateTime(2026, 6, 1);
        var run = SeedRun(db);
        SeedPushedProposal(db, run.Id, "SKU-V", oldPrice: 10m, newPrice: 9m, changePct: -10m,
            reasonCodes: "VELOCITY_FORECAST", pushedUtc: d0);
        SeedSnapshot(db, "SKU-V", d0.AddDays(-100), qty7: 99, net7: 999m, pptcv: 5m); // ancient → excluded by scan bound
        SeedSnapshot(db, "SKU-V", d0, qty7: 7, net7: 70m, pptcv: 5m);                 // pre
        SeedSnapshot(db, "SKU-V", d0.AddDays(7), qty7: 21, net7: 189m, pptcv: 5m);    // post
        db.SaveChanges();

        var finalised = await NewService(db).EvaluateAsync(run);

        Assert.Equal(1, finalised);
        var outcome = await db.PriceChangeOutcomes.AsNoTracking().SingleAsync(o => o.Sku == "SKU-V");
        Assert.Equal(OutcomeVerdict.Win, outcome.Verdict);
        Assert.Equal(1m, outcome.PreUnitsPerDay);  // pre = the D0 row (7/7), NOT the ancient 99/7
        Assert.Equal(3m, outcome.PostUnitsPerDay);
    }

    [Fact]
    public async Task Evaluate_StalePendingOutcome_PastWindowPlusGrace_IsForcedNeutral()
    {
        using var db = NewDb();
        var asOf = new DateTime(2026, 6, 8);
        var run = SeedRun(db); // LayerId 0
        // A Pending outcome from a prior run, applied longer ago than window(7) + grace(28) before asOf:
        // its anchoring snapshots have aged out, so it can never be graded → terminal Neutral.
        db.PriceChangeOutcomes.Add(new PriceChangeOutcome
        {
            LayerId = 0, Sku = "SKU-STALE",
            AppliedUtc = asOf.AddDays(-(7 + 28) - 1),
            WindowDays = 7, Verdict = OutcomeVerdict.Pending,
            Direction = ChangeDirection.Down, Intent = ChangeIntent.VolumeStimulation,
            OldPrice = 10m, NewPrice = 9m,
        });
        SeedSnapshot(db, "SKU-ANY", asOf, qty7: 1, net7: 10m, pptcv: 5m); // establishes asOf (the method's "now")
        db.SaveChanges();

        await NewService(db).EvaluateAsync(run);

        var outcome = await db.PriceChangeOutcomes.AsNoTracking().SingleAsync(o => o.Sku == "SKU-STALE");
        Assert.Equal(OutcomeVerdict.Neutral, outcome.Verdict);
        Assert.Equal(run.Id, outcome.MeasuredOnRunId);
        Assert.NotNull(outcome.MeasuredUtc);
        Assert.Contains("expired", outcome.Note);
    }

    [Fact]
    public async Task Evaluate_RecentPendingOutcome_WithinWindowPlusGrace_StaysPending()
    {
        using var db = NewDb();
        var asOf = new DateTime(2026, 6, 8);
        var run = SeedRun(db);
        db.PriceChangeOutcomes.Add(new PriceChangeOutcome
        {
            LayerId = 0, Sku = "SKU-FRESH",
            AppliedUtc = asOf.AddDays(-5), // well inside window + grace
            WindowDays = 7, Verdict = OutcomeVerdict.Pending,
            Direction = ChangeDirection.Down, Intent = ChangeIntent.VolumeStimulation,
            OldPrice = 10m, NewPrice = 9m,
        });
        SeedSnapshot(db, "SKU-ANY", asOf, qty7: 1, net7: 10m, pptcv: 5m);
        db.SaveChanges();

        await NewService(db).EvaluateAsync(run);

        var outcome = await db.PriceChangeOutcomes.AsNoTracking().SingleAsync(o => o.Sku == "SKU-FRESH");
        Assert.Equal(OutcomeVerdict.Pending, outcome.Verdict);
    }

    [Fact]
    public async Task Evaluate_PreRowOlderThanThePostWindow_IsStillFound_AndGraded()
    {
        using var db = NewDb();
        var d0 = new DateTime(2026, 6, 1);
        var run = SeedRun(db);
        SeedPushedProposal(db, run.Id, "SKU-G", oldPrice: 10m, newPrice: 9m, changePct: -10m,
            reasonCodes: "VELOCITY_FORECAST", pushedUtc: d0);
        // Pre-row sits 20 days before the push — beyond the old (D0 - 8) scan bound that this fixes, but
        // within the asOf-anchored expiry-horizon bound. Must still anchor the grade.
        SeedSnapshot(db, "SKU-G", d0.AddDays(-20), qty7: 7, net7: 70m, pptcv: 5m);  // pre
        SeedSnapshot(db, "SKU-G", d0.AddDays(7), qty7: 21, net7: 189m, pptcv: 5m);  // post → asOf = d0+7
        db.SaveChanges();

        var finalised = await NewService(db).EvaluateAsync(run);

        Assert.Equal(1, finalised);
        var outcome = await db.PriceChangeOutcomes.AsNoTracking().SingleAsync(o => o.Sku == "SKU-G");
        Assert.Equal(OutcomeVerdict.Win, outcome.Verdict);
        Assert.Equal(1m, outcome.PreUnitsPerDay);  // anchored to the 20-day-old pre-row, not skipped
    }

    [Fact]
    public async Task Evaluate_StalePending_OnStillPushedProposal_ExpiresOnce_NotRegraded()
    {
        using var db = NewDb();
        var asOf = new DateTime(2026, 6, 8);
        var d0 = asOf.AddDays(-(7 + 28) - 1); // pushed long enough ago to be stale
        var run = SeedRun(db);
        SeedPushedProposal(db, run.Id, "SKU-SP", oldPrice: 10m, newPrice: 9m, changePct: -10m,
            reasonCodes: "VELOCITY_FORECAST", pushedUtc: d0);
        db.SaveChanges();
        var pushedProp = await db.ProposedPrices.SingleAsync(p => p.Sku == "SKU-SP");
        // A pre-existing Pending outcome linked to that STILL-Pushed proposal (the proposal re-enters
        // pushedRaw every run). Expiry must resolve it to Neutral and the grading loop must then skip it.
        db.PriceChangeOutcomes.Add(new PriceChangeOutcome
        {
            LayerId = 0, ProposedPriceId = pushedProp.Id, Sku = "SKU-SP",
            AppliedUtc = d0, WindowDays = 7, Verdict = OutcomeVerdict.Pending,
            Direction = ChangeDirection.Down, Intent = ChangeIntent.VolumeStimulation, OldPrice = 10m, NewPrice = 9m,
        });
        SeedSnapshot(db, "SKU-ANY", asOf, qty7: 1, net7: 10m, pptcv: 5m); // establishes asOf; SKU-SP sold out (no snapshot)
        db.SaveChanges();

        await NewService(db).EvaluateAsync(run);

        var outcome = await db.PriceChangeOutcomes.AsNoTracking().SingleAsync(o => o.Sku == "SKU-SP");
        Assert.Equal(OutcomeVerdict.Neutral, outcome.Verdict);   // expired, not re-opened/re-graded
        Assert.Contains("expired", outcome.Note);
        Assert.Equal(1, await db.PriceChangeOutcomes.CountAsync(o => o.Sku == "SKU-SP")); // no duplicate row
    }

    [Fact]
    public async Task Evaluate_PendingOutcome_ExactlyAtStaleCutoff_StaysPending()
    {
        using var db = NewDb();
        var asOf = new DateTime(2026, 6, 8);
        var run = SeedRun(db);
        db.PriceChangeOutcomes.Add(new PriceChangeOutcome
        {
            LayerId = 0, Sku = "SKU-EDGE",
            AppliedUtc = asOf.AddDays(-(7 + 28)), // exactly the cutoff → strict '<' keeps it Pending
            WindowDays = 7, Verdict = OutcomeVerdict.Pending,
            Direction = ChangeDirection.Down, Intent = ChangeIntent.VolumeStimulation, OldPrice = 10m, NewPrice = 9m,
        });
        SeedSnapshot(db, "SKU-ANY", asOf, qty7: 1, net7: 10m, pptcv: 5m);
        db.SaveChanges();

        await NewService(db).EvaluateAsync(run);

        var outcome = await db.PriceChangeOutcomes.AsNoTracking().SingleAsync(o => o.Sku == "SKU-EDGE");
        Assert.Equal(OutcomeVerdict.Pending, outcome.Verdict); // on-cutoff is retained (strict less-than)
    }

    // ---------------------------------------------------------------- seeding helpers

    private static PricingRun SeedRun(PricingToolDbContext db)
    {
        var run = new PricingRun { StartedUtc = new DateTime(2026, 6, 8), Status = RunStatus.Succeeded };
        db.PricingRuns.Add(run);
        db.SaveChanges();
        return run;
    }

    private static void SeedPushedProposal(
        PricingToolDbContext db, long runId, string sku,
        decimal oldPrice, decimal newPrice, decimal changePct, string reasonCodes, DateTime pushedUtc)
    {
        db.ProposedPrices.Add(new ProposedPrice
        {
            PricingRunId = runId,
            Sku = sku,
            OldPrice = oldPrice,
            CurrentPrice = oldPrice,
            ProposedPriceValue = newPrice,
            ChangePct = changePct,
            HasChange = true,
            ReasonCodes = reasonCodes,
            Status = ProposalStatus.Pushed,
            PushedUtc = pushedUtc,
        });
    }

    private static void SeedSnapshot(
        PricingToolDbContext db, string sku, DateTime date, int qty7, decimal net7, decimal? pptcv)
    {
        db.DailySnapshots.Add(new DailySnapshot
        {
            SnapshotDate = date,
            Sku = sku,
            Qty7 = qty7,
            Net7 = net7,
            Pptcv = pptcv,
        });
    }
}
