using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace PricingTool.Data.Services;

/// <summary>
/// Startup database initialization. SQL Server LocalDB automatic instances shut down when idle,
/// and their cold start on the next connection can transiently fail (SQL error 50 /
/// "Local Database Runtime ... process failed to start"). The first connection attempt is what
/// triggers the instance to start, so a short retry lets it warm up instead of crashing the host
/// on boot. Harmless against a full SQL Server too — it just succeeds on the first try.
/// </summary>
public static class DbInitializer
{
    public static async Task MigrateWithRetryAsync(
        PricingToolDbContext db,
        ILogger? logger = null,
        int attempts = 6,
        TimeSpan? delay = null,
        CancellationToken ct = default)
    {
        var wait = delay ?? TimeSpan.FromSeconds(2);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Migrations can include heavy one-time data backfills (e.g. seeding SkuDeadStockStates
                // from the full snapshot history), which blow past the 30s default. Give the migration
                // run a generous command timeout — relational-only, so the in-memory provider is untouched.
                if (db.Database.IsRelational())
                    db.Database.SetCommandTimeout(TimeSpan.FromSeconds(600));
                await db.Database.MigrateAsync(ct);
                if (attempt > 1)
                    logger?.LogInformation("Database reachable on attempt {Attempt}.", attempt);
                return;
            }
            catch (SqlException ex) when (attempt < attempts)
            {
                logger?.LogWarning(
                    "Database not reachable yet (attempt {Attempt}/{Attempts}): {Message} Retrying in {Seconds}s…",
                    attempt, attempts, ex.Message, wait.TotalSeconds);
                await Task.Delay(wait, ct);
            }
        }
    }
}
