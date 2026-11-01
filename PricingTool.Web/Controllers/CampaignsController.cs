using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PricingTool.Agent;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Campaigns;
using PricingTool.Agent.Storage;
using PricingTool.Data;
using PricingTool.NopIntegration;
using PricingTool.Web.Models;
using PricingTool.Web.Services;

namespace PricingTool.Web.Controllers;

/// <summary>
/// The Campaigns tab (plan section 5, Web): six weeks of the calendar and the lists (live, coming up, done); beside them the campaign
/// picked on the calendar, where it stands, and a conversation with Nova about it (a brief there re-plans it), or with none picked,
/// a conversation for planning a new one. The veto window: a campaign published ahead of its start can be stopped (its publish
/// rolled back) until it goes live. Anyone signed in can look; talking, approving and stopping are Manager-only and audited.
/// </summary>
[Authorize(Roles = "Analyst,Manager")]
public class CampaignsController : Controller
{
    private const int DoneDays = 120;
    private const int CalendarWeeks = 6;
    private readonly PricingToolDbContext _db;
    private readonly AgentDbContext _ai;
    private readonly ChatTurnLauncher _launcher;
    private readonly AgentOptions _options;

    public CampaignsController(PricingToolDbContext db, AgentDbContext ai, ChatTurnLauncher launcher, IOptions<AgentOptions> options)
    {
        _db = db;
        _ai = ai;
        _launcher = launcher;
        _options = options.Value;
    }

    /// <param name="id">The campaign picked on the calendar.</param>
    /// <param name="from">The calendar's first week (any day in it); by default this week.</param>
    public async Task<IActionResult> Index(long? id, DateOnly? from)
    {
        // A new campaign planned here moves its conversation to the campaign: follow it there once the turn has landed.
        if (id is null && from is null && _launcher.TakeMovedTo() is { } moved) return RedirectToAction(nameof(Index), null, new { id = moved }, "nova");
        var today = DateOnly.FromDateTime(DateTime.Now);
        static DateOnly Monday(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
        var start = Monday(from ?? today);
        var since = start < today.AddDays(-DoneDays) ? start : today.AddDays(-DoneDays);
        var entries = await _ai.CampaignCalendar.AsNoTracking().Where(c => c.EndDate >= since || c.Id == id)
            .OrderBy(c => c.StartDate).ThenBy(c => c.Id).ToListAsync();
        var ids = entries.Select(e => e.Id).ToList();
        var briefs = (await _ai.CampaignBriefs.AsNoTracking().Where(b => b.CalendarId != null && ids.Contains(b.CalendarId.Value) && !b.IsTest
                          && b.Status != BriefStatus.Superseded && b.Status != BriefStatus.Rejected).ToListAsync())
            .GroupBy(b => b.CalendarId!.Value).ToDictionary(g => g.Key, g => g.MaxBy(b => b.Id)!);
        var briefIds = briefs.Values.Select(b => b.Id).ToList();
        var plans = (await _ai.CampaignPlans.AsNoTracking().Where(p => briefIds.Contains(p.BriefId) && !p.IsTest).ToListAsync())
            .GroupBy(p => p.BriefId).ToDictionary(g => g.Key, g => PlanStatus.Standing(g)!);
        var batchIds = plans.Values.Where(p => p.PublishBatchId != null).Select(p => p.PublishBatchId!.Value).ToList();
        var batches = await _ai.PublishBatches.AsNoTracking().Where(b => batchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id);
        var results = (await _ai.CampaignResults.AsNoTracking().Where(r => ids.Contains(r.CalendarId)).ToListAsync())
            .GroupBy(r => r.CalendarId).ToDictionary(g => g.Key, g => g.MaxBy(r => r.Id)!);

        CampaignRow Row(CampaignCalendarEntry e)
        {
            var brief = briefs.GetValueOrDefault(e.Id);
            var plan = brief is null ? null : plans.GetValueOrDefault(brief.Id);
            var batch = plan?.PublishBatchId is { } id ? batches.GetValueOrDefault(id) : null;
            return new CampaignRow(e, brief, plan, batch, results.GetValueOrDefault(e.Id));
        }
        var all = entries.Select(Row).ToList();
        // A cancelled campaign is off the calendar: listed under "Cancelled", never drawn.
        var rows = all.Where(r => r.Entry.Status != CampaignStatus.Cancelled).ToList();

        // Each week of the calendar: the campaigns on it as bars, each on the first lane free from its first day in the week.
        var weeks = new List<CalendarWeek>();
        for (var w = 0; w < CalendarWeeks; w++)
        {
            var monday = start.AddDays(7 * w);
            var sunday = monday.AddDays(6);
            var laneEnds = new List<int>();
            var bars = new List<CalendarBar>();
            foreach (var r in rows.Where(r => r.Entry.StartDate <= sunday && r.Entry.EndDate >= monday))
            {
                var first = r.Entry.StartDate < monday ? 0 : r.Entry.StartDate.DayNumber - monday.DayNumber;
                var last = r.Entry.EndDate > sunday ? 6 : r.Entry.EndDate.DayNumber - monday.DayNumber;
                var lane = laneEnds.FindIndex(end => end < first);
                if (lane < 0) { lane = laneEnds.Count; laneEnds.Add(last); } else laneEnds[lane] = last;
                bars.Add(new CalendarBar(r, first + 1, last - first + 1, lane, r.Entry.StartDate < monday, r.Entry.EndDate > sunday));
            }
            weeks.Add(new CalendarWeek(monday, bars));
        }

        var selected = id is { } sid ? all.FirstOrDefault(r => r.Entry.Id == sid) : null;
        if (id is not null && selected is null) TempData["Error"] = $"Campaign #{id} isn't on the calendar.";
        var topic = selected is null ? ChatTopics.NewCampaign : ChatTopics.Campaign(selected.Entry.Id);
        var conversation = await _ai.ChatMessages.Where(m => m.Topic == topic).MaxAsync(m => (long?)m.ConversationId);
        var planningThread = new List<CampaignMessage>();
        if (selected is not null)
        {
            var selectedBriefs = _ai.CampaignBriefs.Where(b => b.CalendarId == selected.Entry.Id && !b.IsTest).Select(b => b.Id);
            planningThread = (await _ai.CampaignMessages.AsNoTracking().Where(m => selectedBriefs.Contains(m.BriefId)).OrderByDescending(m => m.Id).Take(12).ToListAsync())
                .OrderBy(m => m.Id).ToList();
        }
        return View(new CampaignsViewModel
        {
            Today = today,
            From = start,
            Weeks = weeks,
            Live = rows.Where(r => r.Entry.StartDate <= today && r.Entry.EndDate >= today).ToList(),
            ComingUp = rows.Where(r => r.Entry.StartDate > today).ToList(),
            Done = rows.Where(r => r.Entry.EndDate < today && r.Entry.EndDate >= today.AddDays(-DoneDays)).OrderByDescending(r => r.Entry.StartDate).ToList(),
            Cancelled = all.Where(r => r.Entry.Status == CampaignStatus.Cancelled && r.Entry.EndDate >= today.AddDays(-DoneDays)).OrderByDescending(r => r.Entry.StartDate).ToList(),
            LayerNames = await _db.Layers.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.DisplayName),
            Selected = selected,
            PlanningThread = planningThread,
            LeadDays = _options.Campaigns.LeadDays,
            MinDays = _options.Campaigns.MinDays,
            MaxMiniDays = _options.Campaigns.MaxMiniDays,
            CanReplan = selected is not null && selected.Entry.StartDate >= today.AddDays(_options.Campaigns.LeadDays),
            Conversation = conversation is null ? Array.Empty<ChatMessage>()
                : await _ai.ChatMessages.AsNoTracking().Where(m => m.Topic == topic && m.ConversationId == conversation).OrderBy(m => m.Id).ToListAsync(),
            TurnRunning = _launcher.IsRunning && _launcher.Topic == topic,
            OtherTurnRunning = _launcher.IsRunning && _launcher.Topic != topic,
            Answering = _launcher.Answering,
        });
    }

    /// <summary>A message to Nova on this tab: about the campaign picked (a brief re-plans it), or about a new campaign. The turn
    /// runs in the background, continuing that campaign's conversation; the page refreshes until the answer lands.</summary>
    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Talk(long? id, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return RedirectToAction(nameof(Index), new { id });
        var topic = id is { } cid ? ChatTopics.Campaign(cid) : ChatTopics.NewCampaign;
        var conversation = await _ai.ChatMessages.Where(m => m.Topic == topic).MaxAsync(m => (long?)m.ConversationId);
        if (!_launcher.TryStart(conversation, text, Who, topic))
            TempData["Error"] = "The agents are still answering another message (here, on another campaign or on the Chat tab): send this one when that answer lands.";
        return RedirectToAction(nameof(Index), null, new { id }, "nova");
    }

    public async Task<IActionResult> Detail(long id)
    {
        var entry = await _ai.CampaignCalendar.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id);
        if (entry is null) return NotFound();
        var briefs = await _ai.CampaignBriefs.AsNoTracking().Where(b => b.CalendarId == id && !b.IsTest).OrderByDescending(b => b.Id).ToListAsync();
        var briefIds = briefs.Select(b => b.Id).ToList();
        var plans = await _ai.CampaignPlans.AsNoTracking().Where(p => briefIds.Contains(p.BriefId) && !p.IsTest).OrderByDescending(p => p.Id).ToListAsync();
        var batchIds = plans.Where(p => p.PublishBatchId != null).Select(p => p.PublishBatchId!.Value).ToList();
        var latest = PlanStatus.Standing(plans);
        var type = CampaignMath.TypeOf(entry.Name);
        var past = (await _ai.CampaignCalendar.AsNoTracking().Where(c => c.LayerId == entry.LayerId && c.Id != id && c.EndDate < entry.StartDate).ToListAsync())
            .Where(c => CampaignMath.TypeOf(c.Name) == type).OrderByDescending(c => c.StartDate)
            .Select(c => new CampaignPastWindow(c.Name, c.StartDate, c.EndDate, c.Context, c.Representative)).ToList();
        return View(new CampaignDetailViewModel
        {
            Entry = entry,
            LayerName = (await _db.Layers.AsNoTracking().Where(l => l.Id == entry.LayerId).Select(l => l.DisplayName).SingleOrDefaultAsync()) ?? $"Layer {entry.LayerId}",
            Today = DateOnly.FromDateTime(DateTime.Now),
            Briefs = briefs,
            Plans = plans,
            Batches = await _ai.PublishBatches.AsNoTracking().Where(b => batchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id),
            Thread = await _ai.CampaignMessages.AsNoTracking().Where(m => briefIds.Contains(m.BriefId)).OrderBy(m => m.Id).ToListAsync(),
            TopLines = latest is null ? Array.Empty<CampaignPlanLine>()
                : await _ai.CampaignPlanLines.AsNoTracking().Where(l => l.PlanId == latest.Id).OrderByDescending(l => l.ExpectedGrossProfit).Take(25).ToListAsync(),
            Result = await _ai.CampaignResults.AsNoTracking().Where(r => r.CalendarId == id).OrderByDescending(r => r.Id).FirstOrDefaultAsync(),
            PastWindows = past,
        });
    }

    /// <summary>A person takes a campaign off the calendar before it starts: kept as cancelled, its briefs closed, its plans
    /// stopped, and a publish still on the shelf rolled back. Audited.</summary>
    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(long id, [FromServices] CampaignPlanning planning)
    {
        var (done, message) = await planning.CancelAsync(id, Who);
        if (!done)
        {
            TempData["Error"] = message;
            return RedirectToAction(nameof(Index), new { id });
        }
        var entry = await _ai.CampaignCalendar.AsNoTracking().SingleAsync(c => c.Id == id);
        _db.AuditLog.Add(new PricingTool.Data.Entities.AuditLogEntry
        {
            LayerId = entry.LayerId, TimestampUtc = DateTime.UtcNow, UserName = Who, Category = AgentModeStore.AuditCategory, Action = "Campaign cancelled",
            EntityType = nameof(CampaignCalendarEntry), EntityId = id.ToString(), OldValue = entry.Name, NewValue = CampaignStatus.Cancelled,
        });
        await _db.SaveChangesAsync();
        TempData["Message"] = message;
        return RedirectToAction(nameof(Index));
    }

    /// <summary>The veto: a campaign published ahead of its start is stopped before it goes live (its publish rolled back).</summary>
    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Stop(long planId, [FromServices] NopDiscountWriter writer)
    {
        var plan = await _ai.CampaignPlans.SingleOrDefaultAsync(p => p.Id == planId);
        var brief = plan is null ? null : await _ai.CampaignBriefs.SingleOrDefaultAsync(b => b.Id == plan.BriefId);
        var batch = plan?.PublishBatchId is { } bid ? await _ai.PublishBatches.AsNoTracking().SingleOrDefaultAsync(b => b.Id == bid) : null;
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (plan is null || brief is null || batch is null || batch.RolledBackUtc is not null || plan.Status != PlanStatus.Published || brief.StartDate <= today)
        {
            TempData["Error"] = $"Plan #{planId} isn't a published campaign that hasn't started: nothing to stop.";
            return RedirectToAction(nameof(Index));
        }
        var restored = await writer.RollbackAsync(batch.Id);
        plan.Status = PlanStatus.Stopped;
        (plan.ReviewedBy, plan.ReviewedUtc, plan.WaitsForPerson) = (Who, DateTime.UtcNow, false);
        _ai.CampaignMessages.Add(new CampaignMessage
        {
            BriefId = brief.Id, PlanId = plan.Id, Round = plan.Round, From = Who, CreatedUtc = DateTime.UtcNow,
            Text = $"Stopped before going live: publish batch #{batch.Id} rolled back ({restored} rows restored).",
        });
        await _ai.SaveChangesAsync();
        _db.AuditLog.Add(new PricingTool.Data.Entities.AuditLogEntry
        {
            LayerId = brief.LayerId, TimestampUtc = DateTime.UtcNow, UserName = Who, Category = AgentModeStore.AuditCategory, Action = "Campaign stopped before going live",
            EntityType = nameof(CampaignPlan), EntityId = plan.Id.ToString(), OldValue = PlanStatus.Published, NewValue = PlanStatus.Stopped,
        });
        await _db.SaveChangesAsync();
        TempData["Message"] = $"{brief.Name}: stopped before going live; publish batch #{batch.Id} rolled back ({restored} rows restored).";
        return RedirectToAction(nameof(Index), new { id = brief.CalendarId });
    }

    private string Who => User.Identity?.Name ?? "unknown";
}
