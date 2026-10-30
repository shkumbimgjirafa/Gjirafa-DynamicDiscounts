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

    /// <summary>Seeded task type ids (the user sets their MinutesBefore once measured).</summary>
    public static class TaskTypes
    {
        public const int WeeklyFloorTargetReview = 1;
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
            e.HasData(new TimeSheetTaskType { Id = TaskTypes.WeeklyFloorTargetReview, Name = "Weekly floor and target review", MinutesBefore = null });
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
