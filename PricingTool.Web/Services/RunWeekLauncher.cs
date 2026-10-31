using PricingTool.NopIntegration;

namespace PricingTool.Web.Services;

/// <summary>
/// Starts "Run week" (<see cref="RunWeekLoop"/>) in the background for one layer and returns at once; the Agent tab shows
/// its timeline as it goes. One at a time per process: the loop runs Alpha, the engine and the checker, so a second
/// press waits for the first to finish.
/// </summary>
public class RunWeekLauncher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RunWeekLauncher> _logger;
    private int _running;

    public RunWeekLauncher(IServiceScopeFactory scopeFactory, ILogger<RunWeekLauncher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>The new <c>ai.RunWeek</c> id, or null when one is already running in this process.</summary>
    public async Task<long?> TryStartAsync(int layerId, string by)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return null;
        long id;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            id = await scope.ServiceProvider.GetRequiredService<RunWeekLoop>().StartAsync(layerId, by);
        }
        catch
        {
            Volatile.Write(ref _running, 0);
            throw;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<RunWeekLoop>().RunAsync(id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Run week #{Id} failed.", id);
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        });
        return id;
    }
}
