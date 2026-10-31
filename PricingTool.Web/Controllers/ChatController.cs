using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Operations;
using PricingTool.Agent.Storage;
using PricingTool.Data;
using PricingTool.Web.Models;
using PricingTool.Web.Services;

namespace PricingTool.Web.Controllers;

/// <summary>
/// The Chat tab: one thread of the agents' work and the chat with them (Nova by default, "@alpha" for Alpha), and beside it
/// what waits for a person, the campaigns coming up, and the rules given in the chat. Messages are sent through
/// <see cref="AgentController.Chat"/>, which runs the turn in the background.
/// </summary>
[Authorize(Roles = "Analyst,Manager")]
public class ChatController : Controller
{
    private readonly PricingToolDbContext _db;
    private readonly AgentDbContext _ai;
    private readonly AgentActivity _activity;
    private readonly ExceptionsInbox _inbox;
    private readonly AgentModeStore _modes;
    private readonly ChatTurnLauncher _launcher;
    private readonly ProblemPassLauncher _problems;

    public ChatController(PricingToolDbContext db, AgentDbContext ai, AgentActivity activity, ExceptionsInbox inbox, AgentModeStore modes, ChatTurnLauncher launcher,
        ProblemPassLauncher problems)
    {
        _db = db;
        _ai = ai;
        _activity = activity;
        _inbox = inbox;
        _modes = modes;
        _launcher = launcher;
        _problems = problems;
    }

    public async Task<IActionResult> Index()
    {
        var since = DateTime.UtcNow.AddDays(-7);
        var messages = await _ai.ChatMessages.AsNoTracking().Where(m => m.CreatedUtc >= since.AddDays(-7)).OrderByDescending(m => m.Id).Take(200).ToListAsync();
        var activity = await _activity.FeedAsync(since, id => Url.Action("Detail", "Campaigns", new { id }), take: 150);
        var thread = messages.Select(m => new ThreadItem(m.CreatedUtc, m, null))
            .Concat(activity.Select(f => new ThreadItem(f.Utc, null, f)))
            .OrderBy(t => t.Utc).ToList();
        var layers = await _db.Layers.AsNoTracking().Where(l => l.IsActive).OrderBy(l => l.SortOrder).ThenBy(l => l.Id).ToListAsync();
        var autonomy = new List<LayerAutonomy>();
        foreach (var l in layers) autonomy.Add(new LayerAutonomy(l.Id, l.DisplayName, await _modes.GetAsync(l.Id)));
        var inbox = await _inbox.LoadAsync();
        return View(new ChatPageModel
        {
            Thread = thread,
            ConversationId = await _ai.ChatMessages.MaxAsync(m => (long?)m.ConversationId),
            TurnRunning = _launcher.IsRunning,
            ProblemsRunning = _problems.IsRunning,
            Answering = _launcher.Answering,
            Inbox = inbox,
            ComingUp = await _activity.ComingUpAsync(DateOnly.FromDateTime(DateTime.Now)),
            Rules = await _ai.ChatRules.AsNoTracking().Where(r => r.RemovedUtc == null).OrderBy(r => r.Id).ToListAsync(),
            Lessons = await _ai.AgentLessons.AsNoTracking().Where(l => l.RetiredUtc == null).OrderByDescending(l => l.Id).ToListAsync(),
            Queries = await _ai.AgentQueryLog.AsNoTracking().OrderByDescending(q => q.Id).Take(25).ToListAsync(),
            Layers = autonomy,
            LayerNames = inbox.LayerNames,
        });
    }
}
