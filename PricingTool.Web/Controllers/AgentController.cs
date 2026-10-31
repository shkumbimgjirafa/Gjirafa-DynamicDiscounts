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

    /// <summary>The old Agent page: its parts are on the Run tab (the run, Run week, the switches) and the Chat tab.</summary>
    public IActionResult Index() => RedirectToAction(nameof(RunController.Index), "Run");

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
            return RedirectToAction(nameof(RunController.Index), "Run", null, "run-week");
        }
        var id = await launcher.TryStartAsync(layerId, Who);
        if (id is null)
        {
            TempData["Error"] = "A Run week is already running: wait for it to finish.";
            return RedirectToAction(nameof(RunController.Index), "Run", null, "run-week");
        }
        _db.AuditLog.Add(new PricingTool.Data.Entities.AuditLogEntry
        {
            LayerId = layerId, TimestampUtc = DateTime.UtcNow, UserName = Who, Category = AgentModeStore.AuditCategory, Action = "Run week started",
            EntityType = "RunWeek", EntityId = id.Value.ToString(), OldValue = null, NewValue = "Running",
        });
        await _db.SaveChangesAsync();
        TempData["Message"] = $"Run week #{id} started for layer {layerId}: Alpha, the engine, the run checker, then the sandbox publish. The timeline below updates as it goes.";
        return RedirectToAction(nameof(RunController.Index), "Run", null, "run-week");
    }

    /// <summary>Alpha's problem pass now, in the background: code settles the holds it can, Alpha reports the rest in the chat.</summary>
    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public IActionResult Problems([FromServices] Services.ProblemPassLauncher launcher)
    {
        if (launcher.TryStart())
            TempData["Message"] = "Alpha is looking at what's waiting: code settles what it can first, then Alpha posts what's left and who can fix it here.";
        else
            TempData["Error"] = "Alpha is already looking: its post lands in the thread when it's done.";
        return RedirectToAction(nameof(ChatController.Index), "Chat", null, "bottom");
    }

    /// <summary>A message to the agents: Nova answers, "@alpha" asks Alpha. The turn runs in the background (the agents read
    /// data first, up to a minute or two); the Chat tab shows the message at once and the answer when it lands.</summary>
    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Chat(long? conversationId, string text, [FromServices] Services.ChatTurnLauncher launcher)
    {
        if (string.IsNullOrWhiteSpace(text)) return RedirectToAction(nameof(ChatController.Index), "Chat");
        // One continuous thread: a message continues the newest conversation unless the page says otherwise.
        var conversation = conversationId is > 0 ? conversationId : await _ai.ChatMessages.MaxAsync(m => (long?)m.ConversationId);
        if (!launcher.TryStart(conversation, text, Who))
            TempData["Error"] = "The agents are still answering the last message: send this one when that answer lands.";
        return RedirectToAction(nameof(ChatController.Index), "Chat", null, "bottom");
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
        return RedirectToAction(nameof(ChatController.Index), "Chat", null, "rules");
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
        return RedirectToAction(nameof(ChatController.Index), "Chat", null, "lessons");
    }

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RetireLesson(long lessonId)
    {
        TempData["Message"] = await new PricingTool.Agent.Campaigns.LessonBook(_ai).RetireAsync(lessonId, Who)
            ? $"Lesson #{lessonId} retired." : $"Lesson #{lessonId} is not active.";
        return RedirectToAction(nameof(ChatController.Index), "Chat", null, "lessons");
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
        return Back();
    }

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetMode(int layerId, AutonomyMode mode)
    {
        if (!await _db.Layers.AnyAsync(l => l.Id == layerId)) return NotFound();
        await _modes.SetAsync(layerId, mode, Who);
        TempData["Message"] = $"Layer {layerId}: autonomy set to {mode}.";
        return Back();
    }

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(long decisionId, [FromServices] FloorTargetAgent alpha)
    {
        var review = await alpha.ApproveAsync(decisionId, Who);
        TempData[review.Ok ? "Message" : "Error"] = review.Message;
        return Back();
    }

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(long decisionId, [FromServices] FloorTargetAgent alpha)
    {
        var review = await alpha.RejectAsync(decisionId, Who);
        TempData[review.Ok ? "Message" : "Error"] = review.Message;
        return Back();
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
            return Back();
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
        return Back();
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
            return Back();
        }
        // The SKU waits once however many cost moves held it, so handling it clears all of its holds on the layer.
        var sku = PricingTool.Agent.Operations.CostHoldGroup.SkuOf(d);
        var holds = (await _ai.AgentDecisions.Where(x => x.LayerId == d.LayerId && x.Source == "CostWatcher" && x.Status == DecisionStatus.Held).ToListAsync())
            .Where(x => PricingTool.Agent.Operations.CostHoldGroup.SkuOf(x) == sku).ToList();
        var now = DateTime.UtcNow;
        foreach (var h in holds) (h.Status, h.ReviewedBy, h.ReviewedUtc) = (DecisionStatus.Handled, Who, now);
        await _ai.SaveChangesAsync();
        TempData["Message"] = holds.Count == 1
            ? $"Cost-watcher hold #{d.Id} ({sku}) marked handled."
            : $"{sku}: its {holds.Count} cost-watcher holds marked handled (#{string.Join(", #", holds.Select(h => h.Id))}).";
        return Back();
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
        return Back();
    }

    private string Who => User.Identity?.Name ?? "unknown";

    /// <summary>Back to the page the action was pressed on (the Run tab, the Chat tab or the inbox's details), else the Chat tab.</summary>
    private IActionResult Back()
    {
        var referer = Request.Headers.Referer.ToString();
        if (Uri.TryCreate(referer, UriKind.Absolute, out var uri) && uri.Host == Request.Host.Host && Url.IsLocalUrl(uri.PathAndQuery))
            return Redirect(uri.PathAndQuery);
        return RedirectToAction(nameof(ChatController.Index), "Chat");
    }

    private IReadOnlyList<int> RunWeekLayers =>
        HttpContext.RequestServices.GetRequiredService<IOptions<PricingTool.Agent.AgentOptions>>().Value.Cycle.Layers;
}
