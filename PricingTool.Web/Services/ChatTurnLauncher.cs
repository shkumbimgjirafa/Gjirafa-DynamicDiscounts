using PricingTool.Agent.Chat;
using PricingTool.Agent.Storage;

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
    private long _movedTo;

    public ChatTurnLauncher(IServiceScopeFactory scopeFactory, ILogger<ChatTurnLauncher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>The campaign a new-campaign conversation on the Campaigns tab just put on the calendar (its conversation moved
    /// there), once: the page follows it.</summary>
    public long? TakeMovedTo() => Interlocked.Exchange(ref _movedTo, 0) is var id and > 0 ? id : null;

    /// <summary>The agent the running turn went to ("Nova" or "Alpha"), for the page's "reading the data" line.</summary>
    public string? Answering { get; private set; }

    /// <summary>The running turn's topic (null: the Chat tab), so each page shows "reading the data" only for its own conversation.</summary>
    public string? Topic { get; private set; }

    /// <summary>False when a turn is already running.</summary>
    public bool TryStart(long? conversationId, string text, string author, string? topic = null)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
        Answering = text.TrimStart().StartsWith("@alpha", StringComparison.OrdinalIgnoreCase) ? "Alpha" : "Nova";
        Topic = topic;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var turn = await scope.ServiceProvider.GetRequiredService<AgentChat>().AskAsync(conversationId, text, author, CancellationToken.None, topic);
                if (topic == ChatTopics.NewCampaign && ChatTopics.CalendarIdOf(turn.Added.LastOrDefault()?.Topic) is { } created)
                    Interlocked.Exchange(ref _movedTo, created);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A chat turn failed.");
            }
            finally
            {
                (Answering, Topic) = (null, null);
                Volatile.Write(ref _running, 0);
            }
        });
        return true;
    }
}
