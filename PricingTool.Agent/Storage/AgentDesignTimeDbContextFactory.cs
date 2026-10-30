using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PricingTool.Agent.Storage;

/// <summary>
/// Lets `dotnet ef migrations add --project PricingTool.Agent --context AgentDbContext --output-dir Storage/Migrations` run without
/// booting the web app. The connection string here is never used at runtime.
/// </summary>
public class AgentDesignTimeDbContextFactory : IDesignTimeDbContextFactory<AgentDbContext>
{
    public AgentDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AgentDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=PricingToolHackathon;Trusted_Connection=True;",
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", AgentDbContext.Schema))
            .Options;
        return new AgentDbContext(options);
    }
}
