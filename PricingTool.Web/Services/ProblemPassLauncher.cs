using PricingTool.Agent.Operations;

namespace PricingTool.Web.Services;

/// <summary>
/// Runs Alpha's problem pass (<see cref="ProblemDesk"/>) in the background when a person asks for it on the Chat tab: code
/// settles the holds it can, Alpha looks at the rest and posts in the chat. One at a time per process; the daily cycle runs
/// the same pass once a day.
/// </summary>
public class ProblemPassLauncher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProblemPassLauncher> _logger;
    private int _running;

    public ProblemPassLauncher(IServiceScopeFactory scopeFactory, ILogger<ProblemPassLauncher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>False when a pass is already running.</summary>
    public bool TryStart()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ProblemDesk>().RunAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The problem pass failed.");
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        });
        return true;
    }
}
