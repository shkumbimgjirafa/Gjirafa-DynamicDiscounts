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
