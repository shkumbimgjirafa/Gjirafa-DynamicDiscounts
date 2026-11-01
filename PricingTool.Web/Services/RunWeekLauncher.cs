using PricingTool.Agent.Storage;
using PricingTool.NopIntegration;

namespace PricingTool.Web.Services;

/// <summary>
/// Starts "Run week" (<see cref="RunWeekLoop"/>) in the background for one layer, or several one after the other, and returns at
/// once; the Run tab shows its timeline as it goes. One at a time per process: the loop runs Alpha, the engine and the checker, so a
/// second press waits for the first to finish.
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
    public Task<long?> TryStartAsync(int layerId, string by) => TryStartAsync(new[] { layerId }, by);

    /// <summary>
    /// Run week for each of <paramref name="layerIds"/> in turn (the next starts when the one before has finished, whatever its
    /// outcome). Returns the first one's <c>ai.RunWeek</c> id, or null when one is already running in this process.
    /// <paramref name="finished"/> is called after each, in its own scope.
    /// </summary>
    public async Task<long?> TryStartAsync(IReadOnlyList<int> layerIds, string by, Func<IServiceProvider, RunWeek, Task>? finished = null)
    {
        if (layerIds.Count == 0) return null;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return null;
        long first;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            first = await scope.ServiceProvider.GetRequiredService<RunWeekLoop>().StartAsync(layerIds[0], by);
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
                for (var i = 0; i < layerIds.Count; i++)
                {
                    try
                    {
                        long id;
                        using (var start = _scopeFactory.CreateScope())
                            id = i == 0 ? first : await start.ServiceProvider.GetRequiredService<RunWeekLoop>().StartAsync(layerIds[i], by);
                        using var scope = _scopeFactory.CreateScope();
                        var week = await scope.ServiceProvider.GetRequiredService<RunWeekLoop>().RunAsync(id);
                        if (finished is not null) await finished(scope.ServiceProvider, week);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Run week for layer {Layer} failed.", layerIds[i]);
                    }
                }
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        });
        return first;
    }
}
