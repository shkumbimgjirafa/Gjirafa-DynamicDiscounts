using PricingTool.Agent.Chat;

namespace PricingTool.Web.Services;

/// <summary>
/// Runs a chat turn (<see cref="AgentChat.AskAsync"/>) in the background and returns at once: the agents read data first,
/// so a turn takes from half a minute to a few minutes, and the page shouldn't hang on it. The person's message is stored
/// as the turn starts; the Chat tab refreshes until the answer lands. One turn at a time per process.
/// </summary>
public class ChatTurnLauncher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ChatTurnLauncher> _logger;
    private int _running;

    public ChatTurnLauncher(IServiceScopeFactory scopeFactory, ILogger<ChatTurnLauncher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>The agent the running turn went to ("Nova" or "Alpha"), for the page's "reading the data" line.</summary>
    public string? Answering { get; private set; }

    /// <summary>False when a turn is already running.</summary>
    public bool TryStart(long? conversationId, string text, string author)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
        Answering = text.TrimStart().StartsWith("@alpha", StringComparison.OrdinalIgnoreCase) ? "Alpha" : "Nova";
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<AgentChat>().AskAsync(conversationId, text, author, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A chat turn failed.");
            }
            finally
            {
                Answering = null;
                Volatile.Write(ref _running, 0);
            }
        });
        return true;
    }
}
