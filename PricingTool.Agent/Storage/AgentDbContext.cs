using Microsoft.EntityFrameworkCore;

namespace PricingTool.Agent.Storage;

/// <summary>
/// The hackathon build's own tables, in schema <c>ai</c> of the same database as the tool's
/// <see cref="PricingTool.Data.PricingToolDbContext"/>, with their own migrations history
/// (<c>ai.__EFMigrationsHistory</c>). The imported context and its migrations stay untouched.
/// Schema <c>agent</c> is not used here: it holds the read-only views the agents may query.
/// </summary>
public class AgentDbContext : DbContext
{
    public const string Schema = "ai";

    public AgentDbContext(DbContextOptions<AgentDbContext> options) : base(options) { }

    public DbSet<AiCallLogEntry> AiCallLog => Set<AiCallLogEntry>();
    public DbSet<AgentQueryLogEntry> AgentQueryLog => Set<AgentQueryLogEntry>();
    public DbSet<AgentPass> AgentPasses => Set<AgentPass>();
    public DbSet<AgentDecision> AgentDecisions => Set<AgentDecision>();
    public DbSet<TimeSheetTaskType> TimeSheetTaskTypes => Set<TimeSheetTaskType>();
    public DbSet<TimeSheetEntry> TimeSheetEntries => Set<TimeSheetEntry>();
    public DbSet<PublishBatch> PublishBatches => Set<PublishBatch>();
    public DbSet<PublishAudit> PublishAudits => Set<PublishAudit>();
    public DbSet<PublishException> PublishExceptions => Set<PublishException>();
    public DbSet<AgentRunCheck> AgentRunChecks => Set<AgentRunCheck>();
    public DbSet<AgentRunCheckRow> AgentRunCheckRows => Set<AgentRunCheckRow>();
    public DbSet<DailyCycleStep> DailyCycleSteps => Set<DailyCycleStep>();
    public DbSet<RunWeek> RunWeeks => Set<RunWeek>();
    public DbSet<RunWeekStep> RunWeekSteps => Set<RunWeekStep>();
    public DbSet<ExportBatch> ExportBatches => Set<ExportBatch>();
    public DbSet<MoneyLedgerEntry> MoneyLedger => Set<MoneyLedgerEntry>();
    public DbSet<AgentOutcome> AgentOutcomes => Set<AgentOutcome>();
    public DbSet<CategoryPause> CategoryPauses => Set<CategoryPause>();
    public DbSet<CampaignCalendarEntry> CampaignCalendar => Set<CampaignCalendarEntry>();
    public DbSet<CampaignBrief> CampaignBriefs => Set<CampaignBrief>();
    public DbSet<CampaignResult> CampaignResults => Set<CampaignResult>();
    public DbSet<CostObservation> CostObservations => Set<CostObservation>();
    public DbSet<CampaignPlan> CampaignPlans => Set<CampaignPlan>();
    public DbSet<CampaignPlanLine> CampaignPlanLines => Set<CampaignPlanLine>();
    public DbSet<CampaignMessage> CampaignMessages => Set<CampaignMessage>();
    public DbSet<AgentLesson> AgentLessons => Set<AgentLesson>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ChatRule> ChatRules => Set<ChatRule>();
    public DbSet<BacktestSnapshotRecord> BacktestSnapshots => Set<BacktestSnapshotRecord>();

    /// <summary>Seeded task type ids (the user sets their MinutesBefore once measured).</summary>
    public static class TaskTypes
    {
        public const int WeeklyFloorTargetReview = 1;
        public const int CreateAndPushDiscounts = 2;
        public const int PrePublishRunCheck = 3;
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema(Schema);

        builder.Entity<AiCallLogEntry>(e =>
        {
            e.ToTable("AiCallLog");
            e.HasKey(x => x.Id);
            e.Property(x => x.Agent).HasMaxLength(20);
            e.Property(x => x.Purpose).HasMaxLength(200);
            e.Property(x => x.Model).HasMaxLength(50);
            e.Property(x => x.ModelIds).HasMaxLength(200);
            e.Property(x => x.CostUsd).HasPrecision(12, 6);
            e.Property(x => x.Status).HasMaxLength(30);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.Property(x => x.CallTag).HasMaxLength(40);
            e.HasIndex(x => x.CalledAtUtc);
            e.HasIndex(x => x.CallTag);
        });

        builder.Entity<AgentPass>(e =>
        {
            e.ToTable("AgentPass");
            e.HasKey(x => x.Id);
            e.Property(x => x.Source).HasMaxLength(20);
            e.Property(x => x.Mode).HasMaxLength(20);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.Property(x => x.CallTag).HasMaxLength(40);
            e.HasIndex(x => new { x.LayerId, x.StartedUtc });
        });

        builder.Entity<AgentDecision>(e =>
        {
            e.ToTable("AgentDecision");
            e.HasKey(x => x.Id);
            e.HasOne<AgentPass>().WithMany().HasForeignKey(x => x.PassId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Category).HasMaxLength(200);
            e.Property(x => x.Source).HasMaxLength(20);
            e.Property(x => x.Action).HasMaxLength(30);              // the cost watcher's actions run to 21 characters
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Limit).HasMaxLength(40);
            e.Property(x => x.LimitDetail).HasMaxLength(500);
            e.Property(x => x.Reason).HasMaxLength(2000);
            e.Property(x => x.ReviewedBy).HasMaxLength(256);
            foreach (var p in new[] { nameof(AgentDecision.CurrentFloorPct), nameof(AgentDecision.ProposedFloorPct),
                         nameof(AgentDecision.CurrentTargetPct), nameof(AgentDecision.ProposedTargetPct) })
                e.Property(p).HasPrecision(9, 4);
            e.Property(x => x.Confidence).HasPrecision(5, 4);
            e.HasIndex(x => new { x.LayerId, x.BandId, x.Category, x.CreatedUtc });
            e.HasIndex(x => x.Status);
        });

        builder.Entity<TimeSheetTaskType>(e =>
        {
            e.ToTable("TimeSheetTaskType");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasData(
                new TimeSheetTaskType { Id = TaskTypes.WeeklyFloorTargetReview, Name = "Weekly floor and target review", MinutesBefore = null },
                new TimeSheetTaskType { Id = TaskTypes.CreateAndPushDiscounts, Name = "Create and push the discounts for a run", MinutesBefore = null },
                new TimeSheetTaskType { Id = TaskTypes.PrePublishRunCheck, Name = "Pre-publish check of a pricing run", MinutesBefore = null });
        });

        builder.Entity<TimeSheetEntry>(e =>
        {
            e.ToTable("TimeSheetEntry");
            e.HasKey(x => x.Id);
            e.HasOne<TimeSheetTaskType>().WithMany().HasForeignKey(x => x.TaskTypeId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.UserEmail).HasMaxLength(256);
            e.Property(x => x.What).HasMaxLength(500);
            e.Property(x => x.MinutesAfter).HasPrecision(9, 2);
            e.Property(x => x.Proof).HasMaxLength(500);
            e.Property(x => x.Comment).HasMaxLength(1000);
            e.Property(x => x.Source).HasMaxLength(20);
        });

        builder.Entity<DailyCycleStep>(e =>
        {
            e.ToTable("DailyCycleStep");
            e.HasKey(x => x.Id);
            e.Property(x => x.Step).HasMaxLength(40);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Detail).HasMaxLength(2000);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.HasIndex(x => new { x.CycleDate, x.Step });
        });

        builder.Entity<RunWeek>(e =>
        {
            e.ToTable("RunWeek");
            e.HasKey(x => x.Id);
            e.Property(x => x.StartedBy).HasMaxLength(200);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Outcome).HasMaxLength(2000);
            e.Property(x => x.AiCostUsd).HasPrecision(12, 6);
            e.HasIndex(x => new { x.LayerId, x.StartedUtc });
        });
        builder.Entity<RunWeekStep>(e =>
        {
            e.ToTable("RunWeekStep");
            e.HasKey(x => x.Id);
            e.Property(x => x.Step).HasMaxLength(40);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Detail).HasMaxLength(2000);
            e.Property(x => x.Why).HasMaxLength(4000);
            e.HasIndex(x => x.RunWeekId);
        });

        builder.Entity<ExportBatch>(e =>
        {
            e.ToTable("ExportBatch");
            e.HasKey(x => x.Id);
            e.Property(x => x.FilePath).HasMaxLength(500);
            e.Property(x => x.PushedBy).HasMaxLength(256);
            e.Property(x => x.ExpectedGp30Eur).HasPrecision(18, 2);
            e.HasIndex(x => x.RunId);
        });

        builder.Entity<AgentOutcome>(e =>
        {
            e.ToTable("AgentOutcome");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.DecisionId).IsUnique();
            e.Property(x => x.Category).HasMaxLength(200);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Verdict).HasMaxLength(20);
            e.Property(x => x.Action).HasMaxLength(30);
            e.Property(x => x.Evidence).HasMaxLength(2000);
            foreach (var p in new[] { nameof(AgentOutcome.GpPerDayBefore), nameof(AgentOutcome.GpPerDayAfter), nameof(AgentOutcome.UnitsPerDayBefore),
                         nameof(AgentOutcome.UnitsPerDayAfter), nameof(AgentOutcome.ExpectedGpPerDayAfter) })
                e.Property(p).HasPrecision(18, 2);
            foreach (var p in new[] { nameof(AgentOutcome.HoldoutGpRatio), nameof(AgentOutcome.HoldoutUnitsRatio) })
                e.Property(p).HasPrecision(12, 4);
            foreach (var p in new[] { nameof(AgentOutcome.GpVsExpectedPct), nameof(AgentOutcome.UnitsVsExpectedPct) })
                e.Property(p).HasPrecision(9, 2);
        });

        builder.Entity<CampaignCalendarEntry>(e =>
        {
            e.ToTable("CampaignCalendar");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.LayerId, x.StartDate });
            e.HasIndex(x => new { x.LayerId, x.SourceCampaignId }).IsUnique().HasFilter("[SourceCampaignId] IS NOT NULL");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Type).HasMaxLength(10);
            e.Property(x => x.ScopeKind).HasMaxLength(20);
            e.Property(x => x.ScopeValue).HasMaxLength(200);
            e.Property(x => x.GoalType).HasMaxLength(20);
            e.Property(x => x.GoalValue).HasPrecision(18, 2);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.CreatedBy).HasMaxLength(100);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.Context).HasMaxLength(2000);
            e.Property(x => x.GoalSetBy).HasMaxLength(200);
            e.Property(x => x.Representative).HasDefaultValue(true);
        });

        builder.Entity<CampaignBrief>(e =>
        {
            e.ToTable("CampaignBrief");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.PassId);
            e.HasIndex(x => x.CalendarId);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.ScopeKind).HasMaxLength(20);
            e.Property(x => x.ScopeValue).HasMaxLength(200);
            e.Property(x => x.GoalType).HasMaxLength(20);
            e.Property(x => x.GoalValue).HasPrecision(18, 2);
            e.Property(x => x.MinMarginPct).HasPrecision(9, 2);
            e.Property(x => x.MaxDepthPct).HasPrecision(9, 2);
            e.Property(x => x.Exclusions).HasMaxLength(4000);
            e.Property(x => x.Rationale).HasMaxLength(2000);
            e.Property(x => x.EvidenceRefs).HasMaxLength(4000);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Limit).HasMaxLength(40);
            e.Property(x => x.LimitDetail).HasMaxLength(500);
        });

        builder.Entity<CampaignPlan>(e =>
        {
            e.ToTable("CampaignPlan");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.BriefId);
            e.Property(x => x.Strategy).HasMaxLength(40);
            foreach (var p in new[] { nameof(CampaignPlan.ExpectedUnits), nameof(CampaignPlan.ExpectedNetRevenue), nameof(CampaignPlan.ExpectedGrossProfit),
                         nameof(CampaignPlan.GrossProfitWithoutDiscounts) })
                e.Property(p).HasPrecision(18, 2);
            e.Property(x => x.ExpectedMarginPct).HasPrecision(9, 2);
            e.Property(x => x.AverageDepthPct).HasPrecision(9, 2);
            e.Property(x => x.Feasibility).HasMaxLength(1000);
            e.Property(x => x.GoalVerdict).HasMaxLength(20);
            e.Property(x => x.ReviewedBy).HasMaxLength(256);
            e.Property(x => x.Explanation).HasMaxLength(2000);
            e.Property(x => x.Basis).HasMaxLength(1000);
            e.Property(x => x.Status).HasMaxLength(20);
        });

        builder.Entity<CampaignPlanLine>(e =>
        {
            e.ToTable("CampaignPlanLine");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.PlanId);
            e.Property(x => x.Sku).HasMaxLength(400);
            e.Property(x => x.Category).HasMaxLength(200);
            foreach (var p in new[] { nameof(CampaignPlanLine.RegularPrice), nameof(CampaignPlanLine.CurrentPrice), nameof(CampaignPlanLine.Cost),
                         nameof(CampaignPlanLine.CampaignPrice), nameof(CampaignPlanLine.ExpectedUnits), nameof(CampaignPlanLine.ExpectedGrossProfit) })
                e.Property(p).HasPrecision(18, 4);
            e.Property(x => x.DepthPct).HasPrecision(9, 2);
        });

        builder.Entity<CampaignMessage>(e =>
        {
            e.ToTable("CampaignMessage");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.BriefId);
            e.Property(x => x.From).HasMaxLength(40);
            e.Property(x => x.Text).HasMaxLength(8000);
        });

        builder.Entity<CostObservation>(e =>
        {
            e.ToTable("CostObservation");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ProcessedUtc);
            e.HasIndex(x => new { x.LayerId, x.Sku });
            e.Property(x => x.Sku).HasMaxLength(400);
            e.Property(x => x.Pptcv).HasPrecision(18, 4);
            e.Property(x => x.Source).HasMaxLength(200);
            e.Property(x => x.Outcome).HasMaxLength(40);
        });

        builder.Entity<ChatMessage>(e =>
        {
            e.ToTable("ChatMessage");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ConversationId);
            e.Property(x => x.From).HasMaxLength(20);
            e.Property(x => x.Author).HasMaxLength(256);
            e.Property(x => x.Text).HasMaxLength(8000);
        });

        builder.Entity<BacktestSnapshotRecord>(e =>
        {
            e.ToTable("BacktestSnapshot");
            e.HasKey(x => x.Id);
            e.Property(x => x.CreatedBy).HasMaxLength(256);
        });

        builder.Entity<ChatRule>(e =>
        {
            e.ToTable("ChatRule");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(40);
            e.Property(x => x.Value).HasMaxLength(200);
            e.Property(x => x.Instruction).HasMaxLength(1000);
            e.Property(x => x.CreatedBy).HasMaxLength(256);
            e.Property(x => x.RemovedBy).HasMaxLength(256);
        });

        builder.Entity<AgentLesson>(e =>
        {
            e.ToTable("AgentLesson");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Agent, x.LayerId });
            e.Property(x => x.Agent).HasMaxLength(20);
            e.Property(x => x.Text).HasMaxLength(2000);
            e.Property(x => x.Source).HasMaxLength(20);
            e.Property(x => x.CreatedBy).HasMaxLength(200);
            e.Property(x => x.RetiredBy).HasMaxLength(200);
        });

        builder.Entity<CampaignResult>(e =>
        {
            e.ToTable("CampaignResult");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CalendarId);
            e.Property(x => x.Lesson).HasMaxLength(2000);
        });

        builder.Entity<CategoryPause>(e =>
        {
            e.ToTable("CategoryPause");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.LayerId, x.BandId, x.Category });
            e.Property(x => x.Category).HasMaxLength(200);
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.Property(x => x.ReleasedBy).HasMaxLength(256);
        });

        builder.Entity<MoneyLedgerEntry>(e =>
        {
            e.ToTable("MoneyLedgerEntry");
            e.HasKey(x => x.Id);
            e.Property(x => x.Source).HasMaxLength(30);
            e.Property(x => x.ExpectedEur).HasPrecision(18, 2);
            e.Property(x => x.ExpectedBasis).HasMaxLength(500);
            e.Property(x => x.Measured7Eur).HasPrecision(18, 2);
            e.Property(x => x.Measured30Eur).HasPrecision(18, 2);
            e.Property(x => x.Evidence).HasMaxLength(2000);
            e.HasIndex(x => new { x.Source, x.RefId }).IsUnique();
        });

        builder.Entity<AgentRunCheck>(e =>
        {
            e.ToTable("AgentRunCheck");
            e.HasKey(x => x.Id);
            e.Property(x => x.Verdict).HasMaxLength(10);
            e.Property(x => x.ModelVerdict).HasMaxLength(10);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.Property(x => x.CallTag).HasMaxLength(40);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.HasIndex(x => new { x.RunId, x.StartedUtc });
        });

        builder.Entity<AgentRunCheckRow>(e =>
        {
            e.ToTable("AgentRunCheckRow");
            e.HasKey(x => x.Id);
            e.HasOne<AgentRunCheck>().WithMany().HasForeignKey(x => x.CheckId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Sku).HasMaxLength(400);
            e.Property(x => x.BandName).HasMaxLength(128);
            foreach (var p in new[] { nameof(AgentRunCheckRow.NewPrice), nameof(AgentRunCheckRow.CurrentPrice), nameof(AgentRunCheckRow.Cost), nameof(AgentRunCheckRow.FixPrice) })
                e.Property(p).HasPrecision(18, 4);
            foreach (var p in new[] { nameof(AgentRunCheckRow.MarginPct), nameof(AgentRunCheckRow.FloorPct), nameof(AgentRunCheckRow.ChangePct) })
                e.Property(p).HasPrecision(9, 4);
            e.Property(x => x.Flags).HasMaxLength(300);
            e.Property(x => x.RuleSeverity).HasMaxLength(10);
            e.Property(x => x.ModelSeverity).HasMaxLength(10);
            e.Property(x => x.ModelReason).HasMaxLength(1000);
            e.Property(x => x.FinalSeverity).HasMaxLength(10);
            e.Property(x => x.Detail).HasMaxLength(2000);
            e.HasIndex(x => x.CheckId);
            e.HasIndex(x => x.ProposalId);
        });

        builder.Entity<PublishBatch>(e =>
        {
            e.ToTable("PublishBatch");
            e.HasKey(x => x.Id);
            e.Property(x => x.Db).HasMaxLength(50);
            e.Property(x => x.Target).HasMaxLength(20);
            e.Property(x => x.ShopSchema).HasMaxLength(128);
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.CampaignKey).HasMaxLength(100);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Note).HasMaxLength(2000);
            e.HasIndex(x => new { x.LayerId, x.StartedUtc });
        });

        builder.Entity<PublishAudit>(e =>
        {
            e.ToTable("PublishAudit");
            e.HasKey(x => x.Id);
            e.HasOne<PublishBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Sku).HasMaxLength(400);
            e.Property(x => x.Change).HasMaxLength(30);
            e.Property(x => x.DiscountName).HasMaxLength(400);
            e.Property(x => x.OldDiscountAmount).HasPrecision(18, 4);
            e.Property(x => x.NewDiscountAmount).HasPrecision(18, 4);
            e.Property(x => x.OldStoreIds).HasMaxLength(40);
            e.Property(x => x.NewStoreIds).HasMaxLength(40);
            e.Property(x => x.OldTierPrice).HasPrecision(18, 4);
            e.Property(x => x.OldTierOldPrice).HasPrecision(18, 4);
            e.Property(x => x.NewTierPrice).HasPrecision(18, 4);
            e.Property(x => x.NewTierOldPrice).HasPrecision(18, 4);
            e.HasIndex(x => x.BatchId);
            e.HasIndex(x => x.TierPriceId).HasFilter("[TierPriceId] IS NOT NULL");
        });

        builder.Entity<PublishException>(e =>
        {
            e.ToTable("PublishException");
            e.HasKey(x => x.Id);
            e.HasOne<PublishBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Sku).HasMaxLength(400);
            e.Property(x => x.Kind).HasMaxLength(30);
            e.Property(x => x.Detail).HasMaxLength(1000);
            e.HasIndex(x => x.BatchId);
        });

        builder.Entity<AgentQueryLogEntry>(e =>
        {
            e.ToTable("AgentQueryLog");
            e.HasKey(x => x.Id);
            e.Property(x => x.Agent).HasMaxLength(20);
            e.Property(x => x.CallTag).HasMaxLength(40);
            e.Property(x => x.Tool).HasMaxLength(50);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.HasIndex(x => x.CalledAtUtc);
            e.HasIndex(x => x.CallTag);
        });
    }
}
