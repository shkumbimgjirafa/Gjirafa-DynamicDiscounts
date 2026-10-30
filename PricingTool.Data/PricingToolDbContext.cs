using Microsoft.EntityFrameworkCore;
using PricingTool.Data.Entities;

namespace PricingTool.Data;

/// <summary>
/// The tool's OWN database, in the PricingTool schema, fully separate from the live platform
/// databases (which are only ever read through the read-only source connection).
///
/// Identity/authentication is handled by Porta SSO (OpenID Connect); there are no ASP.NET
/// Identity tables. The only auth-related table here is <see cref="Users"/> — the authorization
/// allowlist that maps an authenticated Porta email to an Analyst/Manager role.
/// </summary>
public class PricingToolDbContext : DbContext
{
    public const string Schema = "PricingTool";

    public PricingToolDbContext(DbContextOptions<PricingToolDbContext> options) : base(options) { }

    public DbSet<Layer> Layers => Set<Layer>();
    public DbSet<DailySnapshot> DailySnapshots => Set<DailySnapshot>();
    public DbSet<PricingRun> PricingRuns => Set<PricingRun>();
    public DbSet<ProposedPrice> ProposedPrices => Set<ProposedPrice>();
    public DbSet<PriceChangeOutcome> PriceChangeOutcomes => Set<PriceChangeOutcome>();
    public DbSet<AlgorithmVoteRecord> AlgorithmVotes => Set<AlgorithmVoteRecord>();
    public DbSet<PriceBand> PriceBands => Set<PriceBand>();
    public DbSet<BandAlgorithmSetting> BandAlgorithmSettings => Set<BandAlgorithmSetting>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<ToolSetting> ToolSettings => Set<ToolSetting>();
    public DbSet<SkuOverride> SkuOverrides => Set<SkuOverride>();
    public DbSet<SkuElasticity> SkuElasticities => Set<SkuElasticity>();
    public DbSet<SkuDeadStockState> SkuDeadStockStates => Set<SkuDeadStockState>();
    public DbSet<DailyLayerKpi> DailyLayerKpi => Set<DailyLayerKpi>();
    public DbSet<User> Users => Set<User>();
    public DbSet<ApprovedBatch> ApprovedBatches => Set<ApprovedBatch>();
    public DbSet<ApprovedBatchItem> ApprovedBatchItems => Set<ApprovedBatchItem>();
    public DbSet<Approval> Approvals => Set<Approval>();
    public DbSet<ApprovalItem> ApprovalItems => Set<ApprovalItem>();
    public DbSet<DeadStockWindowLog> DeadStockWindowLogs => Set<DeadStockWindowLog>();
    public DbSet<DeadStockWindowClearedItem> DeadStockWindowClearedItems => Set<DeadStockWindowClearedItem>();
    public DbSet<DiscountTrimRule> DiscountTrimRules => Set<DiscountTrimRule>();
    public DbSet<DeadStockEpisode> DeadStockEpisodes => Set<DeadStockEpisode>();
    public DbSet<DeadStockDailyKpi> DeadStockDailyKpis => Set<DeadStockDailyKpi>();
    public DbSet<PricingRunBandTarget> PricingRunBandTargets => Set<PricingRunBandTarget>();
    public DbSet<BandCategoryRule> BandCategoryRules => Set<BandCategoryRule>();
    public DbSet<PricingRunCategoryRule> PricingRunCategoryRules => Set<PricingRunCategoryRule>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);

        builder.Entity<Layer>(e =>
        {
            e.ToTable("Layers");
            e.HasIndex(x => new { x.Brand, x.CountryCode }).IsUnique();
            e.Property(x => x.Brand).HasMaxLength(64);
            e.Property(x => x.CountryCode).HasMaxLength(8);
            e.Property(x => x.DisplayName).HasMaxLength(128);
            e.Property(x => x.OperationalDatabase).HasMaxLength(128);
            e.Property(x => x.Currency).HasMaxLength(8);
            e.Property(x => x.RunTimeUtc).HasMaxLength(8);
            e.Property(x => x.VatRatePct).HasPrecision(5, 2);
        });

        builder.Entity<DailySnapshot>(e =>
        {
            e.ToTable("DailySnapshots");
            e.HasIndex(x => new { x.LayerId, x.SnapshotDate, x.Sku }).IsUnique();
            e.HasIndex(x => x.Sku);
            e.HasOne<Layer>().WithMany().HasForeignKey(x => x.LayerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.SupplierName).HasMaxLength(100);
            e.Property(x => x.ProductName).HasMaxLength(400);
            e.Property(x => x.Brand).HasMaxLength(200);
            e.Property(x => x.ActiveDiscountIds).HasMaxLength(256);
            e.Property(x => x.SnapshotDate).HasColumnType("date");

            foreach (var p in new[] { nameof(DailySnapshot.OldPrice), nameof(DailySnapshot.AnchorPrice), nameof(DailySnapshot.CurrentPrice) })
                e.Property(p).HasPrecision(18, 2);
            e.Property(x => x.Pptcv).HasPrecision(18, 4);
            e.Property(x => x.GrossMargin).HasPrecision(9, 4);
            e.Property(x => x.CurrentDiscountPct).HasPrecision(9, 6);
            foreach (var p in new[] { nameof(DailySnapshot.Net7), nameof(DailySnapshot.Net30), nameof(DailySnapshot.Net90) })
                e.Property(p).HasPrecision(18, 2);
        });

        builder.Entity<PricingRun>(e =>
        {
            e.ToTable("PricingRuns");
            e.Property(x => x.TriggeredBy).HasMaxLength(256);
            e.Property(x => x.ErrorMessage).HasMaxLength(4000);
            e.HasIndex(x => x.StartedUtc);
            e.HasIndex(x => new { x.LayerId, x.Status });
            e.HasOne<Layer>().WithMany().HasForeignKey(x => x.LayerId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ProposedPrice>(e =>
        {
            e.ToTable("ProposedPrices");
            e.HasOne(x => x.PricingRun).WithMany(r => r.Proposals).HasForeignKey(x => x.PricingRunId);
            e.HasIndex(x => new { x.PricingRunId, x.Sku }).IsUnique();
            e.HasIndex(x => x.Sku);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.LayerId);
            e.HasOne<Layer>().WithMany().HasForeignKey(x => x.LayerId).OnDelete(DeleteBehavior.Restrict);
            // |ChangePct| as a DB-computed column, indexed with (run, status) so the default
            // "top 500 by change magnitude" view is an index range scan, not a 500k-row sort.
            e.Property(x => x.AbsChangePct).HasPrecision(9, 4).HasComputedColumnSql("ABS([ChangePct])");
            e.HasIndex(x => new { x.PricingRunId, x.Status, x.AbsChangePct });
            // Supplier filter on Proposals Review: makes the per-run supplier dropdown (DISTINCT id+name)
            // and the supplier-filtered listing/count an index-only range scan instead of a full-run scan.
            e.HasIndex(x => new { x.PricingRunId, x.SupplierId }).IncludeProperties(x => x.SupplierName);
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.Gtin).HasMaxLength(64);
            e.Property(x => x.SupplierName).HasMaxLength(100);
            e.Property(x => x.ProductName).HasMaxLength(400);
            e.Property(x => x.Brand).HasMaxLength(200);
            e.Property(x => x.ActiveDiscountIds).HasMaxLength(256);
            e.Property(x => x.OldPrice).HasPrecision(18, 2);
            e.Property(x => x.AnchorPrice).HasPrecision(18, 2);
            e.Property(x => x.CurrentPrice).HasPrecision(18, 2);
            e.Property(x => x.Pptcv).HasPrecision(18, 4);
            e.Property(x => x.RawWeightedPrice).HasPrecision(18, 4);
            e.Property(x => x.Elasticity).HasPrecision(12, 6);
            e.Property(x => x.ElasticityStdError).HasPrecision(12, 6);
            e.Property(x => x.ProposedPriceValue).HasPrecision(18, 2);
            e.Property(x => x.EngineProposedPrice).HasPrecision(18, 2);
            e.Property(x => x.ManualOverrideBy).HasMaxLength(256);
            e.Property(x => x.ChangePct).HasPrecision(9, 4);
            e.Property(x => x.ReasonCodes).HasMaxLength(512);
            e.Property(x => x.GuardrailFlags).HasMaxLength(512);
            e.Property(x => x.SkipReason).HasMaxLength(64);
            e.Property(x => x.ReviewedBy).HasMaxLength(256);
        });

        builder.Entity<PriceChangeOutcome>(e =>
        {
            e.ToTable("PriceChangeOutcomes");
            e.HasOne(x => x.ProposedPrice).WithMany().HasForeignKey(x => x.ProposedPriceId);
            // One outcome per pushed proposal. Unique + filtered because the FK is nullable (an
            // outcome can outlive its proposal); matches the unique-natural-key convention on the
            // other tables and gives the application-level dedupe a hard DB backstop.
            e.HasIndex(x => x.ProposedPriceId).IsUnique().HasFilter("[ProposedPriceId] IS NOT NULL");
            e.HasIndex(x => x.Verdict);
            e.HasIndex(x => x.AppliedUtc);
            e.HasIndex(x => x.LayerId);
            e.HasOne<Layer>().WithMany().HasForeignKey(x => x.LayerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.Note).HasMaxLength(512);
            e.Property(x => x.OldPrice).HasPrecision(18, 2);
            e.Property(x => x.NewPrice).HasPrecision(18, 2);
            e.Property(x => x.PreUnitsPerDay).HasPrecision(12, 4);
            e.Property(x => x.PostUnitsPerDay).HasPrecision(12, 4);
            // Margin % is recomputed as (Net7 - cost*Qty7)/Net7; on a loss-making SKU with tiny Net7
            // it can be a large negative number, so it needs far more headroom than a bounded source %.
            e.Property(x => x.PreMarginPct).HasPrecision(18, 4);
            e.Property(x => x.PostMarginPct).HasPrecision(18, 4);
            e.Property(x => x.PreGrossProfitPerDay).HasPrecision(18, 2);
            e.Property(x => x.PostGrossProfitPerDay).HasPrecision(18, 2);
        });

        builder.Entity<AlgorithmVoteRecord>(e =>
        {
            e.ToTable("AlgorithmVotes");
            e.HasOne(x => x.ProposedPrice).WithMany(p => p.Votes).HasForeignKey(x => x.ProposedPriceId);
            e.Property(x => x.AlgorithmCode).HasMaxLength(64);
            e.Property(x => x.SuggestedPrice).HasPrecision(18, 4);
            e.Property(x => x.Confidence).HasPrecision(5, 4);
            e.Property(x => x.EffectiveWeight).HasPrecision(9, 4);
            e.Property(x => x.ReasonCode).HasMaxLength(64);
            e.Property(x => x.ReasonText).HasMaxLength(1024);
            e.HasIndex(x => x.AlgorithmCode);
        });

        builder.Entity<PriceBand>(e =>
        {
            e.ToTable("PriceBands");
            e.HasIndex(x => x.LayerId);
            e.HasOne<Layer>().WithMany().HasForeignKey(x => x.LayerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.MinPrice).HasPrecision(18, 2);
            e.Property(x => x.MaxPrice).HasPrecision(18, 2);
            e.Property(x => x.MarginFloorPct).HasPrecision(9, 4);
            e.Property(x => x.TargetMarginPct).HasPrecision(9, 4);
        });

        builder.Entity<BandCategoryRule>(e =>
        {
            e.ToTable("BandCategoryRules");
            e.HasIndex(x => new { x.LayerId, x.BandId, x.Category }).IsUnique();
            e.HasOne<Layer>().WithMany().HasForeignKey(x => x.LayerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PriceBand>().WithMany().HasForeignKey(x => x.BandId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Category).HasMaxLength(200);
            e.Property(x => x.MarginFloorPct).HasPrecision(9, 4);
            e.Property(x => x.TargetMarginPct).HasPrecision(9, 4);
            e.Property(x => x.UpdatedBy).HasMaxLength(256);
        });

        builder.Entity<PricingRunCategoryRule>(e =>
        {
            e.ToTable("PricingRunCategoryRules");
            // One row per band x category rule a run applied; no FK to PricingRuns (outlives retention), like
            // PricingRunBandTargets.
            e.HasIndex(x => new { x.PricingRunId, x.BandId, x.Category }).IsUnique();
            e.Property(x => x.Category).HasMaxLength(200);
            e.Property(x => x.MarginFloorPct).HasPrecision(9, 4);
            e.Property(x => x.TargetMarginPct).HasPrecision(9, 4);
        });

        builder.Entity<DiscountTrimRule>(e =>
        {
            e.ToTable("DiscountTrimRules");
            e.HasIndex(x => x.LayerId);
            e.HasOne<Layer>().WithMany().HasForeignKey(x => x.LayerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.MinDiscountPct).HasPrecision(9, 4);
            e.Property(x => x.MaxDiscountPct).HasPrecision(9, 4);
            e.Property(x => x.TrimPct).HasPrecision(9, 4);
        });

        builder.Entity<DeadStockEpisode>(e =>
        {
            e.ToTable("DeadStockEpisodes");
            e.HasIndex(x => new { x.LayerId, x.Sku, x.EnteredDate }).IsUnique();
            e.HasIndex(x => new { x.LayerId, x.ExitedDate });
            e.HasOne<Layer>().WithMany().HasForeignKey(x => x.LayerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.EnteredDate).HasColumnType("date");
            e.Property(x => x.LastSeenDate).HasColumnType("date");
            e.Property(x => x.ExitedDate).HasColumnType("date");
            e.Property(x => x.EntryPptcv).HasPrecision(18, 4);
            e.Property(x => x.LastPptcv).HasPrecision(18, 4);
            e.Property(x => x.EntryPrice).HasPrecision(18, 2);
            e.Property(x => x.LastPrice).HasPrecision(18, 2);
        });

        builder.Entity<DeadStockDailyKpi>(e =>
        {
            e.ToTable("DeadStockDailyKpi");
            e.HasIndex(x => new { x.CountryCode, x.Date }).IsUnique();
            e.Property(x => x.CountryCode).HasMaxLength(8);
            e.Property(x => x.Date).HasColumnType("date");
            e.Property(x => x.PrevDate).HasColumnType("date");
            e.Property(x => x.DeadCost).HasPrecision(18, 2);
        });

        builder.Entity<PricingRunBandTarget>(e =>
        {
            e.ToTable("PricingRunBandTargets");
            // One row per band per run. No FK to PricingRuns on purpose (plain column) so the snapshot
            // outlives run retention; a non-unique index would do, but (RunId, BandId) unique also guards
            // against a double-capture. Layer FK kept for consistency (layers are never deleted).
            e.HasIndex(x => new { x.PricingRunId, x.BandId }).IsUnique();
            e.HasOne<Layer>().WithMany().HasForeignKey(x => x.LayerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.MinPrice).HasPrecision(18, 2);
            e.Property(x => x.MaxPrice).HasPrecision(18, 2);
            e.Property(x => x.TargetMarginPct).HasPrecision(9, 4);
            e.Property(x => x.MarginFloorPct).HasPrecision(9, 4);
        });

        builder.Entity<BandAlgorithmSetting>(e =>
        {
            e.ToTable("BandAlgorithmSettings");
            e.HasOne(x => x.PriceBand).WithMany(b => b.AlgorithmSettings).HasForeignKey(x => x.PriceBandId);
            e.HasIndex(x => new { x.PriceBandId, x.AlgorithmCode }).IsUnique();
            e.Property(x => x.AlgorithmCode).HasMaxLength(64);
        });

        builder.Entity<AuditLogEntry>(e =>
        {
            e.ToTable("AuditLog");
            e.HasIndex(x => x.TimestampUtc);
            e.HasIndex(x => x.LayerId);
            e.HasOne<Layer>().WithMany().HasForeignKey(x => x.LayerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.UserName).HasMaxLength(256);
            e.Property(x => x.Category).HasMaxLength(32);
            e.Property(x => x.Action).HasMaxLength(256);
            e.Property(x => x.EntityType).HasMaxLength(128);
            e.Property(x => x.EntityId).HasMaxLength(128);
        });

        builder.Entity<ToolSetting>(e =>
        {
            e.ToTable("ToolSettings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(128);
            e.Property(x => x.Value).HasMaxLength(1024);
            e.Property(x => x.UpdatedBy).HasMaxLength(256);
        });

        builder.Entity<SkuOverride>(e =>
        {
            e.ToTable("SkuOverrides");
            e.HasIndex(x => new { x.LayerId, x.Sku }).IsUnique();
            e.HasOne<Layer>().WithMany().HasForeignKey(x => x.LayerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.Note).HasMaxLength(512);
        });

        builder.Entity<User>(e =>
        {
            e.ToTable("Users");
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.Name).HasMaxLength(256);
            e.Property(x => x.Role).HasMaxLength(32).IsRequired();
        });

        builder.Entity<SkuElasticity>(e =>
        {
            e.ToTable("SkuElasticity");
            e.HasIndex(x => new { x.LayerId, x.Sku }).IsUnique();
            e.HasIndex(x => new { x.LayerId, x.IsUsable });
            e.HasOne<Layer>().WithMany().HasForeignKey(x => x.LayerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.Coefficient).HasPrecision(12, 6);
            e.Property(x => x.StandardError).HasPrecision(12, 6);
            e.Property(x => x.Intercept).HasPrecision(18, 6);
            e.Property(x => x.R2).HasPrecision(9, 6);
            e.Property(x => x.PriceCv).HasPrecision(12, 6);
        });

        builder.Entity<SkuDeadStockState>(e =>
        {
            e.ToTable("SkuDeadStockStates");
            e.HasIndex(x => new { x.LayerId, x.Sku }).IsUnique();
            e.HasOne<Layer>().WithMany().HasForeignKey(x => x.LayerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.LastSnapshotDate).HasColumnType("date");
            e.Property(x => x.PrevSnapshotDate).HasColumnType("date");
            e.Property(x => x.MarkdownTargetPct).HasPrecision(9, 4);
            // A price, so the same precision as every other stored price (ProposedPriceValue et al) — the
            // run compares the marker against the priced result to skip redundant writes, which only works
            // if the value round-trips exactly.
            e.Property(x => x.BelowFloorTunnelPrice).HasPrecision(18, 2);
        });

        builder.Entity<DailyLayerKpi>(e =>
        {
            e.ToTable("DailyLayerKpi");
            e.HasIndex(x => new { x.LayerId, x.Date }).IsUnique();
            e.HasOne<Layer>().WithMany().HasForeignKey(x => x.LayerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Date).HasColumnType("date");
            e.Property(x => x.Net7).HasPrecision(18, 2);
            e.Property(x => x.CostedNet7).HasPrecision(18, 2);
            e.Property(x => x.Cost7).HasPrecision(18, 4);
        });

        builder.Entity<ApprovedBatch>(e =>
        {
            e.ToTable("ApprovedBatches");
            e.HasIndex(x => new { x.LayerId, x.PushedUtc });
            e.HasIndex(x => new { x.LayerId, x.IsActive }); // clearance-lock lookup per run
            e.HasOne<Layer>().WithMany().HasForeignKey(x => x.LayerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Label).HasMaxLength(256);
            e.Property(x => x.Notes).HasMaxLength(1024);
            e.Property(x => x.IsActive).HasDefaultValue(true); // existing/backfilled batches enforce by default
        });

        builder.Entity<ApprovedBatchItem>(e =>
        {
            e.ToTable("ApprovedBatchItems");
            e.HasOne(x => x.ApprovedBatch).WithMany(b => b.Items).HasForeignKey(x => x.ApprovedBatchId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ApprovedBatchId, x.Sku }).IsUnique();
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.ProductName).HasMaxLength(400);
            e.Property(x => x.Brand).HasMaxLength(200);
            e.Property(x => x.Band).HasMaxLength(32);
            e.Property(x => x.OldPrice).HasPrecision(18, 2);
            e.Property(x => x.PrePushPrice).HasPrecision(18, 2);
            e.Property(x => x.PushedPrice).HasPrecision(18, 2);
            e.Property(x => x.Pptcv).HasPrecision(18, 4);
            e.Property(x => x.SoldNet).HasPrecision(18, 2);
            e.Property(x => x.ActiveDiscountIds).HasMaxLength(256);
        });

        builder.Entity<Approval>(e =>
        {
            e.ToTable("Approvals");
            e.HasIndex(x => new { x.LayerId, x.ApprovedUtc });
            e.HasOne<Layer>().WithMany().HasForeignKey(x => x.LayerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.ApprovedBy).HasMaxLength(256);
            foreach (var p in new[] { "Profit7Before", "Profit7After", "Profit30Before", "Profit30After", "Profit90Before", "Profit90After" })
                e.Property<decimal?>(p).HasPrecision(18, 2);
        });

        builder.Entity<ApprovalItem>(e =>
        {
            e.ToTable("ApprovalItems");
            e.HasOne(x => x.Approval).WithMany(a => a.Items).HasForeignKey(x => x.ApprovalId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ApprovalId, x.Sku }).IsUnique();
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.OldPrice).HasPrecision(18, 2);
            e.Property(x => x.NewPrice).HasPrecision(18, 2);
        });

        builder.Entity<DeadStockWindowLog>(e =>
        {
            e.ToTable("DeadStockWindowLogs");
            e.HasIndex(x => x.ApprovedBatchId).IsUnique(); // one sealed window per batch (idempotent seal)
            e.HasIndex(x => new { x.LayerId, x.WindowClosedUtc }); // trend lookup per layer, newest first
            e.HasOne<Layer>().WithMany().HasForeignKey(x => x.LayerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApprovedBatch>().WithMany().HasForeignKey(x => x.ApprovedBatchId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.BatchLabel).HasMaxLength(256);
            e.Property(x => x.Notes).HasMaxLength(1024);
            e.Property(x => x.OpenStockCost).HasPrecision(18, 2);
            e.Property(x => x.SoldNet).HasPrecision(18, 2);
            e.Property(x => x.ClearedStockCost).HasPrecision(18, 2);
        });

        builder.Entity<DeadStockWindowClearedItem>(e =>
        {
            e.ToTable("DeadStockWindowClearedItems");
            e.HasOne(x => x.DeadStockWindowLog).WithMany(l => l.Items).HasForeignKey(x => x.DeadStockWindowLogId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.DeadStockWindowLogId, x.Sku }).IsUnique();
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.ProductName).HasMaxLength(400);
            e.Property(x => x.Brand).HasMaxLength(200);
            e.Property(x => x.Band).HasMaxLength(32);
            e.Property(x => x.PushedPrice).HasPrecision(18, 2);
            e.Property(x => x.Pptcv).HasPrecision(18, 4);
            e.Property(x => x.SoldNet).HasPrecision(18, 2);
        });
    }
}
