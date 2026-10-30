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
    public DbSet<ExportBatch> ExportBatches => Set<ExportBatch>();
    public DbSet<MoneyLedgerEntry> MoneyLedger => Set<MoneyLedgerEntry>();

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
            e.Property(x => x.Action).HasMaxLength(10);
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

        builder.Entity<ExportBatch>(e =>
        {
            e.ToTable("ExportBatch");
            e.HasKey(x => x.Id);
            e.Property(x => x.FilePath).HasMaxLength(500);
            e.Property(x => x.PushedBy).HasMaxLength(256);
            e.Property(x => x.ExpectedGp30Eur).HasPrecision(18, 2);
            e.HasIndex(x => x.RunId);
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
            e.HasIndex(x => x.BatchId);
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
