using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PricingTool.Core.Abstractions;
using PricingTool.Core.Algorithms;
using PricingTool.Core.Domain;
using PricingTool.Core.Options;
using PricingTool.Core.Services;
using PricingTool.Data.Services;

namespace PricingTool.Data;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the whole pricing stack (DbContext, Core pipeline, all 10 algorithms,
    /// readers, orchestrator, push integration). Shared by Web and Engine.
    /// </summary>
    public static IServiceCollection AddPricingTool(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<PricingEngineOptions>(config.GetSection(PricingEngineOptions.SectionName));

        services.AddDbContext<PricingToolDbContext>(o =>
            o.UseSqlServer(
                config.GetConnectionString("PricingToolDb")
                    ?? throw new InvalidOperationException("Missing connection string 'PricingToolDb'."),
                sql => sql.EnableRetryOnFailure()));

        // Core pipeline — stateless, safe as singletons.
        services.AddSingleton<WeightedScoringService>();
        services.AddSingleton<GuardrailService>();
        services.AddSingleton<RoundingService>();
        services.AddSingleton<PriceCalculator>();
        services.AddSingleton<MarginTargetService>();

        // The 4 pricing algorithms. Per-band enable/disable + weights live in BandAlgorithmSettings.
        // (Velocity family merged into SellThrough; discount-effectiveness and margin-tier retired;
        // new-product protection is now a hard engine rule from the MarkAsNew window, not a voting algorithm.)
        services.AddSingleton<IPricingAlgorithm, SellThroughAlgorithm>();
        services.AddSingleton<IPricingAlgorithm, PriceElasticityHeuristicAlgorithm>();
        services.AddSingleton<IPricingAlgorithm, DeadStockMarkdownAlgorithm>();
        services.AddSingleton<IPricingAlgorithm, CrossDockMarkdownAlgorithm>();

        var useDemoData = config.GetSection(PricingEngineOptions.SectionName).GetValue<bool>("UseDemoData");
        if (useDemoData)
        {
            services.AddScoped<ISourceDataReader, DemoSourceDataReader>();
            services.AddScoped<IElasticitySourceReader, DemoElasticitySourceReader>();
            services.AddScoped<ISkuSalesHistoryReader, DemoSkuSalesHistoryReader>();
            services.AddScoped<ISalesReportProfitReader, DemoSalesReportProfitReader>();
        }
        else
        {
            services.AddScoped<ISourceDataReader, SqlSourceDataReader>();
            services.AddScoped<IElasticitySourceReader, SqlElasticitySourceReader>();
            services.AddScoped<ISkuSalesHistoryReader, SqlSkuSalesHistoryReader>();
            services.AddScoped<ISalesReportProfitReader, SqlSalesReportProfitReader>();
        }

        services.AddScoped<IBulkWriteService, BulkWriteService>();
        services.AddScoped<SnapshotService>();
        services.AddScoped<ICategoryMapReader, HkCategoryMapReader>();
        services.AddScoped<BandConfigProvider>();
        services.AddScoped<BandFloorMirrorService>();
        services.AddScoped<BandMatchService>();
        services.AddScoped<AuditService>();
        services.AddScoped<OutcomeEvaluationService>();
        services.AddScoped<DeadStockEpisodeService>();
        services.AddScoped<ScheduleService>();
        services.AddScoped<DiscountOptimizationImpactService>();
        services.AddScoped<PricingRunOrchestrator>();
        services.AddScoped<ElasticityFitService>();
        services.AddScoped<ApprovedBatchRefreshService>();
        services.AddScoped<ApprovalProfitRefreshService>();
        services.AddScoped<DeadStockWindowService>();
        services.AddScoped<DemoHistoryBackfill>();
        services.AddScoped<DemoOutcomeSeeder>();
        services.AddScoped<IPricePushService, CsvPricePushService>();

        return services;
    }
}
