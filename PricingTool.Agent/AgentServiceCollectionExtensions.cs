using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Checker;
using PricingTool.Agent.ClaudeCode;
using PricingTool.Agent.Operations;
using PricingTool.Agent.Storage;
using PricingTool.Agent.Tools;

namespace PricingTool.Agent;

public static class AgentServiceCollectionExtensions
{
    /// <summary>
    /// Registers the agent layer: options (section "Agent"), its own <see cref="AgentDbContext"/> on the
    /// tool's database, and the Claude Code client. Nothing here starts a process or calls Claude until
    /// an agent asks, so the app starts whether or not Claude Code is installed or logged in.
    /// </summary>
    public static IServiceCollection AddPricingAgent(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AgentOptions>(config.GetSection(AgentOptions.SectionName));

        // The agents' own records go to "AgentDb" when it is set (test runs: the test database, see TestConnectionString),
        // else to the tool's database. Their data is always read from the tool's database.
        services.AddDbContext<AgentDbContext>(o =>
            o.UseSqlServer(
                config.GetConnectionString(AgentDbConnectionName) ?? config.GetConnectionString("PricingToolDb")
                    ?? throw new InvalidOperationException("Missing connection string 'PricingToolDb'."),
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", AgentDbContext.Schema)
                          .EnableRetryOnFailure()));

        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddScoped<ClaudeCodeClient>();

        services.AddSingleton(new AgentDataSource(config.GetConnectionString("PricingToolDb")!));
        services.AddSingleton<PricingDataReader>();
        services.AddSingleton<AlphaInputBuilder>();
        services.AddSingleton<IAlphaInputSource>(sp => sp.GetRequiredService<AlphaInputBuilder>());
        services.AddScoped<AgentModeStore>();
        services.AddScoped<KillSwitch>();
        services.AddScoped<IKillSwitch>(sp => sp.GetRequiredService<KillSwitch>());
        services.AddScoped<FloorTargetAgent>();
        services.AddScoped<RunChecker>();
        services.AddScoped<CheckedExport>();
        services.AddScoped<IHoldoutLookup, HoldoutLookup>();
        services.AddScoped<IProposalCategoryLookup, ProposalCategoryLookup>();
        services.AddScoped<DatabaseMaintenance>();
        services.AddScoped<HkRefresher>();
        services.AddScoped<MoneyLedger>();
        services.AddScoped<OutcomeTracker>();
        services.AddScoped<Campaigns.CampaignCalendar>();
        services.AddScoped<Campaigns.CampaignAgent>();
        services.AddScoped<Campaigns.ICampaignCandidateSource, Campaigns.SqlCampaignCandidateSource>();
        services.AddScoped<Campaigns.CampaignPlanning>();
        services.AddScoped<Chat.AgentChat>();
        services.AddScoped<TimeSheetExporter>();
        services.AddScoped<DailyCycle>();
        services.AddScoped<IRunFacts, SqlRunFacts>();
        services.AddScoped<RunReportWriter>();
        services.AddScoped<ProblemDesk>();
        services.AddScoped<ExceptionsInbox>();
        services.AddScoped<AgentToolLog>();
        services.AddScoped<AgentDataTools>();
        services.AddScoped<QueryDataService>();
        return services;
    }

    public const string AgentDbConnectionName = "AgentDb";

    /// <summary>
    /// The test database for demo, backtest and injected-test runs (prompt 05: no mock data in the live DB): the tool's
    /// connection with the catalog <see cref="AgentOptions.TestDatabase"/> (default: the tool's database name + "Test").
    /// It holds only the agents' records (schema <c>ai</c>); the inputs are read from the tool's database.
    /// </summary>
    public static string TestConnectionString(IConfiguration config)
    {
        var cs = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(config.GetConnectionString("PricingToolDb")
            ?? throw new InvalidOperationException("Missing connection string 'PricingToolDb'."));
        var name = config[$"{AgentOptions.SectionName}:{nameof(AgentOptions.TestDatabase)}"];
        cs.InitialCatalog = string.IsNullOrWhiteSpace(name) ? cs.InitialCatalog + "Test" : name;
        return cs.ConnectionString;
    }

    /// <summary>Applies the agent layer's own migrations (schema <c>ai</c>), then ensures the product-map
    /// lookup (<see cref="ProductMapSql"/>) on the data connection. Call after the tool's migrations, from a
    /// startup scope.</summary>
    public static async Task MigrateAgentDbAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        var db = services.GetRequiredService<AgentDbContext>();
        if (!db.Database.IsRelational()) return;
        await db.Database.MigrateAsync(ct);
        // The lookup lives with the data (the tool's database), which isn't always where the agents' own
        // records go (see AgentDbConnectionName), so it's ensured on the data connection, not in a migration.
        await ProductMapSql.EnsureAsync(services.GetRequiredService<AgentDataSource>().ConnectionString, ct);
    }
}
