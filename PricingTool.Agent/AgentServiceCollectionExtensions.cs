using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.ClaudeCode;
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

        services.AddDbContext<AgentDbContext>(o =>
            o.UseSqlServer(
                config.GetConnectionString("PricingToolDb")
                    ?? throw new InvalidOperationException("Missing connection string 'PricingToolDb'."),
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", AgentDbContext.Schema)
                          .EnableRetryOnFailure()));

        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddScoped<ClaudeCodeClient>();

        services.AddSingleton(new AgentDataSource(config.GetConnectionString("PricingToolDb")!));
        services.AddSingleton<PricingDataReader>();
        services.AddSingleton<AlphaInputBuilder>();
        services.AddScoped<AgentToolLog>();
        services.AddScoped<AgentDataTools>();
        services.AddScoped<QueryDataService>();
        return services;
    }

    /// <summary>Applies the agent layer's own migrations (schema <c>ai</c>). Call after the tool's
    /// migrations, from a startup scope.</summary>
    public static async Task MigrateAgentDbAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        var db = services.GetRequiredService<AgentDbContext>();
        if (db.Database.IsRelational())
            await db.Database.MigrateAsync(ct);
    }
}
