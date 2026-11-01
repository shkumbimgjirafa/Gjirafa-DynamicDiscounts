using Microsoft.EntityFrameworkCore;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Chat;
using PricingTool.Agent.Operations;
using PricingTool.Agent.Storage;
using PricingTool.Data;
using PricingTool.NopIntegration;

namespace PricingTool.Web.Services;

/// <summary>
/// Runs a chat turn (<see cref="AgentChat.AskAsync"/>) in the background and returns at once: the agents read data first,
/// so a turn takes from half a minute to a few minutes, and the page shouldn't hang on it. The person's message is stored
/// as the turn starts; the Chat tab refreshes until the answer lands. One turn at a time per process.
///
/// A turn on the Chat tab that records a person's effective margin target for today or the coming week (a margin-target rule)
/// then starts Run week for those shops, one after the other (the user's request, 1 Nov): Alpha reviews the band × category floors
/// and targets with the target in view, the engine prices the shop, the checker checks, the prices go to the sandbox, and each
/// result with its price list is posted in the thread.
/// </summary>
public class ChatTurnLauncher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ChatTurnLauncher> _logger;
    private readonly RunWeekLauncher _runWeek;
    private int _running;
    private long _movedTo;

    public ChatTurnLauncher(IServiceScopeFactory scopeFactory, ILogger<ChatTurnLauncher> logger, RunWeekLauncher runWeek)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _runWeek = runWeek;
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
            ChatTurn? turn = null;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                turn = await scope.ServiceProvider.GetRequiredService<AgentChat>().AskAsync(conversationId, text, author, CancellationToken.None, topic);
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
            if (turn is not null && topic is null)
            {
                try { await RunWeekForMarginTargetsAsync(turn, author); }
                catch (Exception ex) { _logger.LogError(ex, "Starting Run week after a margin target failed."); }
            }
        });
        return true;
    }

    /// <summary>Gjirafa50 first: its run is the shorter one, so its prices come back first.</summary>
    private static readonly int[] RunOrder = { 4, 1 };

    private async Task RunWeekForMarginTargetsAsync(ChatTurn turn, string author)
    {
        var ruleIds = turn.Added.Where(m => m.RuleId is not null).Select(m => m.RuleId!.Value).ToList();
        if (ruleIds.Count == 0) return;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var rules = (await db.ChatRules.AsNoTracking().Where(r => ruleIds.Contains(r.Id) && r.Kind == ChatRuleKinds.MarginTarget).ToListAsync())
            .Where(r => r.ActiveOn(today, today.AddDays(FloorTargetAgent.MarginTargetDaysAhead)) && r.LayerId is 1 or 4).ToList();
        var layers = RunOrder.Where(l => rules.Any(r => r.LayerId == l)).ToList();
        if (layers.Count == 0) return;

        var names = await scope.ServiceProvider.GetRequiredService<PricingToolDbContext>().Layers.AsNoTracking()
            .Where(l => layers.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.DisplayName);
        var chat = scope.ServiceProvider.GetRequiredService<AgentChat>();
        var shops = string.Join(", then ", layers.Select(l => names.GetValueOrDefault(l, $"layer {l}")));
        var first = await _runWeek.TryStartAsync(layers, author, (sp, week) => PostResultAsync(sp, week, names));
        await chat.PostAsync("code", first is null
            ? "The margin targets are recorded, but a Run week is already running: press Run week on the Run tab for each shop when it has finished."
            : $"Run week #{first} started for {shops}: for each shop, Alpha reviews the floors and targets by band × category with the margin target in view, " +
              "the engine prices the shop, the run checker holds what it flags, and the rest goes to the shop sandbox. Each result and its price list " +
              "(Sku, ProposedPrice, StartDate, EndDate, AlgorithmUsed, NewMargin) is posted here; the Run tab shows the timeline as it goes.");
    }

    private static async Task PostResultAsync(IServiceProvider sp, RunWeek week, IReadOnlyDictionary<int, string> names)
    {
        var db = sp.GetRequiredService<AgentDbContext>();
        var steps = await db.RunWeekSteps.AsNoTracking().Where(s => s.RunWeekId == week.Id).ToListAsync();
        string? Detail(string step) => steps.LastOrDefault(s => s.Step == step)?.Detail;
        var lines = new List<string> { $"**Run week #{week.Id}, {names.GetValueOrDefault(week.LayerId, $"layer {week.LayerId}")}: {week.Status}.** {week.Outcome}" };
        if (Detail("alpha") is { } alpha) lines.Add($"- Alpha: {alpha}");
        if (Detail("engine") is { } engine) lines.Add($"- Engine: {engine}");
        if (Detail("check") is { } check) lines.Add($"- Run checker: {check}");
        if (Detail("report") is { } report) lines.Add($"- On the shelf: {report}");
        if (Detail(RunWeekPriceList.Step) is { } list) lines.Add($"- Price list: {list}");
        await sp.GetRequiredService<AgentChat>().PostAsync("code", string.Join("\n", lines));
    }
}
