using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Operations;
using PricingTool.Agent.Storage;
using PricingTool.Data;
using PricingTool.NopIntegration;
using PricingTool.Web.Models;

namespace PricingTool.Web.Controllers;

/// <summary>
/// The switches a person holds over the agents (plan section 5, Web): the kill switch, the autonomy switch per
/// layer and the publisher's switches, plus the exceptions inbox, which lists only what needs a person.
/// Anyone signed in can look; changing anything is Manager-only and audited (category Agent).
/// </summary>
[Authorize(Roles = "Analyst,Manager")]
public class AgentController : Controller
{
    private readonly PricingToolDbContext _db;
    private readonly AgentDbContext _ai;
    private readonly KillSwitch _killSwitch;
    private readonly AgentModeStore _modes;
    private readonly ExceptionsInbox _inbox;
    private readonly PublisherOptions _publisher;

    public AgentController(PricingToolDbContext db, AgentDbContext ai, KillSwitch killSwitch, AgentModeStore modes,
        ExceptionsInbox inbox, IOptions<PublisherOptions> publisher)
    {
        _db = db;
        _ai = ai;
        _killSwitch = killSwitch;
        _modes = modes;
        _inbox = inbox;
        _publisher = publisher.Value;
    }

    public async Task<IActionResult> Index(long? conversation = null)
    {
        var layers = await _db.Layers.AsNoTracking().Where(l => l.IsActive).OrderBy(l => l.SortOrder).ThenBy(l => l.Id).ToListAsync();
        var autonomy = new List<LayerAutonomy>();
        foreach (var l in layers) autonomy.Add(new LayerAutonomy(l.Id, l.DisplayName, await _modes.GetAsync(l.Id)));
        var inbox = await _inbox.LoadAsync();
        var passes = await _ai.AgentPasses.AsNoTracking().Where(p => !p.IsTest).OrderByDescending(p => p.Id).Take(8).ToListAsync();
        var passIds = passes.Select(p => p.Id).ToList();
        var changed = await _ai.AgentDecisions.AsNoTracking()
            .Where(d => passIds.Contains(d.PassId) && d.Status == DecisionStatus.Applied && d.Action != "keep")
            .GroupBy(d => d.PassId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        var since = DateTime.UtcNow.AddDays(-7);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var chatId = conversation ?? await _ai.ChatMessages.MaxAsync(m => (long?)m.ConversationId);
        return View(new AgentStatusViewModel
        {
            KillSwitch = await _killSwitch.GetAsync(),
            Layers = autonomy,
            Publisher = _publisher,
            Waiting = inbox.WaitingCount,
            RecentPasses = passes,
            Changed = changed,
            LayerNames = inbox.LayerNames,
            Week = await WeekAsync(since),
            Feed = await FeedAsync(since),
            ComingUp = await ComingUpAsync(today),
            Queries = await _ai.AgentQueryLog.AsNoTracking().OrderByDescending(q => q.Id).Take(25).ToListAsync(),
            Lessons = await _ai.AgentLessons.AsNoTracking().Where(l => l.RetiredUtc == null).OrderByDescending(l => l.Id).ToListAsync(),
            ConversationId = chatId,
            Chat = chatId is { } cid ? await _ai.ChatMessages.AsNoTracking().Where(m => m.ConversationId == cid).OrderBy(m => m.Id).ToListAsync() : Array.Empty<ChatMessage>(),
            Conversations = (await _ai.ChatMessages.AsNoTracking().GroupBy(m => m.ConversationId)
                    .Select(g => new { g.Key, Started = g.Min(m => m.CreatedUtc), FirstId = g.Min(m => m.Id) }).OrderByDescending(x => x.Key).Take(8).ToListAsync())
                .Select(x => (x.Key, x.Started, _ai.ChatMessages.AsNoTracking().Where(m => m.Id == x.FirstId).Select(m => m.Text).Single())).ToList(),
            Rules = await _ai.ChatRules.AsNoTracking().Where(r => r.RemovedUtc == null).OrderBy(r => r.Id).ToListAsync(),
            RunWeeks = await RunWeeksAsync(),
            RunWeekRunning = HttpContext.RequestServices.GetRequiredService<Services.RunWeekLauncher>().IsRunning,
            RunWeekLayers = RunWeekLayers,
        });
    }

    private async Task<IReadOnlyList<(PricingTool.Agent.Storage.RunWeek, IReadOnlyList<RunWeekStep>)>> RunWeeksAsync()
    {
        var latest = (await _ai.RunWeeks.AsNoTracking().OrderByDescending(w => w.Id).Take(20).ToListAsync())
            .GroupBy(w => w.LayerId).Select(g => g.First()).OrderByDescending(w => w.Id).ToList();
        var ids = latest.Select(w => w.Id).ToList();
        var steps = await _ai.RunWeekSteps.AsNoTracking().Where(s => ids.Contains(s.RunWeekId)).OrderBy(s => s.Id).ToListAsync();
        return latest.Select(w => (w, (IReadOnlyList<RunWeekStep>)steps.Where(s => s.RunWeekId == w.Id).ToList())).ToList();
    }

    /// <summary>"Run week" for one layer: Alpha's pass → engine run → run checker → sandbox publish → read-back, in the
    /// background (<see cref="PricingTool.NopIntegration.RunWeekLoop"/>). Audited; the kill switch stops it.</summary>
    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RunWeek(int layerId, [FromServices] Services.RunWeekLauncher launcher)
    {
        if (!await _db.Layers.AnyAsync(l => l.Id == layerId)) return NotFound();
        if (!RunWeekLayers.Contains(layerId))
        {
            TempData["Error"] = $"Run week runs only the daily cycle's layers ({string.Join(", ", RunWeekLayers)}): the others have no data loaded here.";
            return RedirectToAction(nameof(Index), null, "run-week");
        }
        var id = await launcher.TryStartAsync(layerId, Who);
        if (id is null)
        {
            TempData["Error"] = "A Run week is already running: wait for it to finish.";
            return RedirectToAction(nameof(Index), null, "run-week");
        }
        _db.AuditLog.Add(new PricingTool.Data.Entities.AuditLogEntry
        {
            LayerId = layerId, TimestampUtc = DateTime.UtcNow, UserName = Who, Category = AgentModeStore.AuditCategory, Action = "Run week started",
            EntityType = "RunWeek", EntityId = id.Value.ToString(), OldValue = null, NewValue = "Running",
        });
        await _db.SaveChangesAsync();
        TempData["Message"] = $"Run week #{id} started for layer {layerId}: Alpha, the engine, the run checker, then the sandbox publish. The timeline below updates as it goes.";
        return RedirectToAction(nameof(Index), null, "run-week");
    }

    /// <summary>A message to the agents: Nova answers, "@alpha" asks Alpha. Takes up to a minute or two (the agents read data).</summary>
    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Chat(long? conversationId, string text, [FromServices] PricingTool.Agent.Chat.AgentChat chat)
    {
        if (string.IsNullOrWhiteSpace(text)) return RedirectToAction(nameof(Index), new { conversation = conversationId });
        var turn = await chat.AskAsync(conversationId is > 0 ? conversationId : null, text, Who, HttpContext.RequestAborted);
        if (turn.Error is not null) TempData["Error"] = turn.Error;
        return Redirect(Url.Action(nameof(Index), new { conversation = turn.ConversationId }) + "#chat");
    }

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveRule(long ruleId)
    {
        var rule = await _ai.ChatRules.SingleOrDefaultAsync(r => r.Id == ruleId && r.RemovedUtc == null);
        if (rule is null) TempData["Error"] = $"Rule #{ruleId} is not active.";
        else
        {
            (rule.RemovedUtc, rule.RemovedBy) = (DateTime.UtcNow, Who);
            await _ai.SaveChangesAsync();
            _db.AuditLog.Add(new PricingTool.Data.Entities.AuditLogEntry
            {
                LayerId = rule.LayerId, TimestampUtc = DateTime.UtcNow, UserName = Who, Category = AgentModeStore.AuditCategory, Action = "Chat rule removed",
                EntityType = nameof(ChatRule), EntityId = rule.Id.ToString(), OldValue = $"{rule.Kind} {rule.Value} {rule.FromDate:yyyy-MM-dd}..{rule.ToDate:yyyy-MM-dd}", NewValue = "removed",
            });
            await _db.SaveChangesAsync();
            TempData["Message"] = $"Rule #{rule.Id} removed: campaign planning no longer applies it.";
        }
        return Redirect(Url.Action(nameof(Index)) + "#rules");
    }

    private async Task<WeekNumbers> WeekAsync(DateTime since)
    {
        var real = _ai.AgentPasses.Where(p => !p.IsTest).Select(p => p.Id);
        var decisions = await _ai.AgentDecisions.AsNoTracking().Where(d => d.CreatedUtc >= since && real.Contains(d.PassId) && d.Action != "keep")
            .GroupBy(d => d.Status).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        var batches = await _ai.PublishBatches.AsNoTracking().Where(b => b.StartedUtc >= since && b.Status == "Published").ToListAsync();
        var calls = await _ai.AiCallLog.AsNoTracking().Where(c => c.CalledAtUtc >= since).ToListAsync();
        var ledger = await _ai.MoneyLedger.AsNoTracking().Where(m => m.Measured30Eur != null).ToListAsync();
        return new WeekNumbers(decisions.GetValueOrDefault(DecisionStatus.Applied), decisions.GetValueOrDefault(DecisionStatus.Held), batches.Count,
            batches.Sum(b => b.Created + b.Updated), calls.Count, calls.Sum(c => c.CostUsd ?? 0m),
            ledger.Count > 0 ? ledger.Sum(m => m.Measured30Eur!.Value) : null, await _ai.MoneyLedger.CountAsync());
    }

    /// <summary>"What they did": both agents' decisions and passes, the cost watcher, the campaign threads and the publishes, newest first.</summary>
    private async Task<IReadOnlyList<FeedItem>> FeedAsync(DateTime since)
    {
        var feed = new List<FeedItem>();
        var real = await _ai.AgentPasses.AsNoTracking().Where(p => !p.IsTest && p.StartedUtc >= since).ToListAsync();
        var realIds = real.Select(p => p.Id).ToList();
        foreach (var p in real.Where(p => p.Source != "CostWatcher"))
            feed.Add(new FeedItem(p.StartedUtc, p.Source, p.LayerId, $"{(p.Source == "Nova" ? "Weekly campaign pass" : "Weekly floor and target pass")} #{p.Id}: {p.Status}",
                p.Summary ?? p.Error, null));
        foreach (var d in await _ai.AgentDecisions.AsNoTracking().Where(d => realIds.Contains(d.PassId) && d.Action != "keep").OrderByDescending(d => d.Id).Take(60).ToListAsync())
            feed.Add(new FeedItem(d.CreatedUtc, d.Source, d.LayerId,
                d.Source == "CostWatcher"
                    ? $"{d.Action} ({d.Status}{(d.Limit is null ? "" : ", " + d.Limit)})"
                    : $"{d.Action} band {d.BandId} {d.Category}: floor {d.CurrentFloorPct:0.#} -> {d.ProposedFloorPct:0.#}%, target {d.CurrentTargetPct:0.#} -> {d.ProposedTargetPct:0.#}% ({d.Status})",
                d.Reason, null));
        var messages = await (from m in _ai.CampaignMessages.AsNoTracking()
                              join b in _ai.CampaignBriefs.AsNoTracking() on m.BriefId equals b.Id
                              where !b.IsTest && m.CreatedUtc >= since
                              orderby m.Id descending
                              select new { m, b.Name, b.LayerId, b.CalendarId }).Take(40).ToListAsync();
        foreach (var x in messages)
            feed.Add(new FeedItem(x.m.CreatedUtc, x.m.From, x.LayerId, $"on {x.Name} (round {x.m.Round})", x.m.Text,
                x.CalendarId is { } cid ? Url.Action("Detail", "Campaigns", new { id = cid }) : null));
        foreach (var b in await _ai.PublishBatches.AsNoTracking().Where(b => b.StartedUtc >= since).OrderByDescending(b => b.Id).Take(20).ToListAsync())
            feed.Add(new FeedItem(b.StartedUtc, "Publisher", b.LayerId,
                $"{b.Kind} batch #{b.Id} to {b.Target}: {b.Status}{(b.RolledBackUtc is null ? "" : ", rolled back")} ({b.Created} created, {b.Updated} updated)", null, null));
        return feed.OrderByDescending(f => f.Utc).Take(80).ToList();
    }

    /// <summary>"Coming up": campaigns starting in the next three weeks with their plan, and the veto window for published ones.</summary>
    private async Task<IReadOnlyList<CampaignRow>> ComingUpAsync(DateOnly today)
    {
        var entries = await _ai.CampaignCalendar.AsNoTracking().Where(c => c.StartDate >= today && c.StartDate <= today.AddDays(21)).OrderBy(c => c.StartDate).ToListAsync();
        var rows = new List<CampaignRow>();
        foreach (var e in entries)
        {
            var brief = await _ai.CampaignBriefs.AsNoTracking().Where(b => b.CalendarId == e.Id && !b.IsTest && b.Status != BriefStatus.Superseded && b.Status != BriefStatus.Rejected)
                .OrderByDescending(b => b.Id).FirstOrDefaultAsync();
            var plan = brief is null ? null : await _ai.CampaignPlans.AsNoTracking().Where(p => p.BriefId == brief.Id).OrderByDescending(p => p.Id).FirstOrDefaultAsync();
            var batch = plan?.PublishBatchId is { } id ? await _ai.PublishBatches.AsNoTracking().SingleOrDefaultAsync(b => b.Id == id) : null;
            rows.Add(new CampaignRow(e, brief, plan, batch, null));
        }
        return rows;
    }

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddLesson(string agent, int? layerId, string text)
    {
        try
        {
            var l = await new PricingTool.Agent.Campaigns.LessonBook(_ai).AddAsync(agent, layerId, text, Who);
            TempData["Message"] = $"Lesson #{l.Id} for {l.Agent}: kept in every pass until a person retires it.";
        }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RetireLesson(long lessonId)
    {
        TempData["Message"] = await new PricingTool.Agent.Campaigns.LessonBook(_ai).RetireAsync(lessonId, Who)
            ? $"Lesson #{lessonId} retired." : $"Lesson #{lessonId} is not active.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Exceptions() => View(await _inbox.LoadAsync());

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetKillSwitch(bool stop)
    {
        await _killSwitch.SetAsync(stop, Who);
        var state = await _killSwitch.GetAsync();
        if (stop)
            TempData["Message"] = "Kill switch pulled: agent calls, Alpha's pass, the run checker and the publisher are stopped.";
        else if (state.Stopped)
            TempData["Error"] = $"Still stopped: {state.Reason}. Releasing here can't override that.";
        else
            TempData["Message"] = "Kill switch released: the agents and the publisher may act again.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetMode(int layerId, AutonomyMode mode)
    {
        if (!await _db.Layers.AnyAsync(l => l.Id == layerId)) return NotFound();
        await _modes.SetAsync(layerId, mode, Who);
        TempData["Message"] = $"Layer {layerId}: autonomy set to {mode}.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(long decisionId, [FromServices] FloorTargetAgent alpha)
    {
        var review = await alpha.ApproveAsync(decisionId, Who);
        TempData[review.Ok ? "Message" : "Error"] = review.Message;
        return RedirectToAction(nameof(Exceptions));
    }

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(long decisionId, [FromServices] FloorTargetAgent alpha)
    {
        var review = await alpha.RejectAsync(decisionId, Who);
        TempData[review.Ok ? "Message" : "Error"] = review.Message;
        return RedirectToAction(nameof(Exceptions));
    }

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReleasePause(long pauseId)
    {
        var pause = await _ai.CategoryPauses.SingleOrDefaultAsync(p => p.Id == pauseId);
        if (pause is null || pause.ReleasedUtc is not null)
        {
            TempData["Error"] = $"Pause #{pauseId} is not active.";
            return RedirectToAction(nameof(Exceptions));
        }
        var now = DateTime.UtcNow;
        pause.ReleasedUtc = now;
        pause.ReleasedBy = Who;
        await _ai.SaveChangesAsync();
        _db.AuditLog.Add(new PricingTool.Data.Entities.AuditLogEntry
        {
            LayerId = pause.LayerId, TimestampUtc = now, UserName = Who, Category = AgentModeStore.AuditCategory, Action = "Category pause released",
            EntityType = nameof(CategoryPause), EntityId = $"{pause.BandId} {pause.Category}", OldValue = "paused", NewValue = "released",
        });
        await _db.SaveChangesAsync();
        TempData["Message"] = $"Band {pause.BandId} × {pause.Category} released: the pricing agent's changes there follow the normal limits again.";
        return RedirectToAction(nameof(Exceptions));
    }

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> HandleCostHold(long decisionId)
    {
        var d = await _ai.AgentDecisions.SingleOrDefaultAsync(x => x.Id == decisionId && x.Source == "CostWatcher" && x.Status == DecisionStatus.Held);
        if (d is null)
        {
            TempData["Error"] = $"Decision #{decisionId} is not a cost-watcher hold.";
            return RedirectToAction(nameof(Exceptions));
        }
        d.Status = DecisionStatus.Handled;
        d.ReviewedBy = Who;
        d.ReviewedUtc = DateTime.UtcNow;
        await _ai.SaveChangesAsync();
        TempData["Message"] = $"Cost-watcher hold #{d.Id} marked handled.";
        return RedirectToAction(nameof(Exceptions));
    }

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApprovePlan(long planId)
    {
        // The web app has no publisher: a held plan is approved here and published from the engine host (approve-plan --publish).
        var r = await HttpContext.RequestServices.GetRequiredService<PricingTool.Agent.Campaigns.CampaignPlanning>().ApproveAsync(planId, Who, publish: false);
        if (r.Error is not null) TempData["Error"] = r.Error;
        else TempData["Message"] = $"Campaign plan #{planId} approved ({r.Status})." +
                                   (r.Status == "accepted" ? $" Publish it from the engine host: PricingTool.AgentTools approve-plan {planId} --publish." : "");
        return RedirectToAction(nameof(Exceptions));
    }

    private string Who => User.Identity?.Name ?? "unknown";

    private IReadOnlyList<int> RunWeekLayers =>
        HttpContext.RequestServices.GetRequiredService<IOptions<PricingTool.Agent.AgentOptions>>().Value.Cycle.Layers;
}
