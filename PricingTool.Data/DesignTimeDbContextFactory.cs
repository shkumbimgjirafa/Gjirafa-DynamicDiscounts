using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PricingTool.Data;

/// <summary>
/// Lets `dotnet ef migrations add` run against this project without booting the web app.
/// The connection string here is never used at runtime — runtime config comes from appsettings.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PricingToolDbContext>
{
    public PricingToolDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PricingToolDbContext>()
            // Long command timeout so `dotnet ef database update` can run heavy one-time data backfills
            // (e.g. the SkuDeadStockStates seed) without tripping the 30s default. Design-time only.
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=PricingTool;Trusted_Connection=True;",
                sql => sql.CommandTimeout(600))
            .Options;
        return new PricingToolDbContext(options);
    }
}
