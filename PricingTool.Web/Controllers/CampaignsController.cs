using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Campaigns;
using PricingTool.Agent.Storage;
using PricingTool.Data;
using PricingTool.NopIntegration;
using PricingTool.Web.Models;

namespace PricingTool.Web.Controllers;

/// <summary>
/// The Campaigns tab (plan section 5, Web): the calendar (live, coming up, done), one card per campaign with its brief, plan,
/// the agent-to-agent thread and the result, and the veto window: a campaign published ahead of its start can be stopped
/// (its publish rolled back) until it goes live. Anyone signed in can look; stopping is Manager-only and audited.
/// </summary>
[Authorize(Roles = "Analyst,Manager")]
public class CampaignsController : Controller
{
    private const int DoneDays = 120;
    private readonly PricingToolDbContext _db;
    private readonly AgentDbContext _ai;

    public CampaignsController(PricingToolDbContext db, AgentDbContext ai)
    {
        _db = db;
        _ai = ai;
    }

    public async Task<IActionResult> Index()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var entries = await _ai.CampaignCalendar.AsNoTracking().Where(c => c.EndDate >= today.AddDays(-DoneDays))
            .OrderBy(c => c.StartDate).ToListAsync();
        var ids = entries.Select(e => e.Id).ToList();
        var briefs = (await _ai.CampaignBriefs.AsNoTracking().Where(b => b.CalendarId != null && ids.Contains(b.CalendarId.Value) && !b.IsTest
                          && b.Status != BriefStatus.Superseded && b.Status != BriefStatus.Rejected).ToListAsync())
            .GroupBy(b => b.CalendarId!.Value).ToDictionary(g => g.Key, g => g.MaxBy(b => b.Id)!);
        var briefIds = briefs.Values.Select(b => b.Id).ToList();
        var plans = (await _ai.CampaignPlans.AsNoTracking().Where(p => briefIds.Contains(p.BriefId) && !p.IsTest).ToListAsync())
            .GroupBy(p => p.BriefId).ToDictionary(g => g.Key, g => g.MaxBy(p => p.Id)!);
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
        return View(new CampaignsViewModel
        {
            Today = today,
            Live = entries.Where(e => e.StartDate <= today && e.EndDate >= today).Select(Row).ToList(),
            ComingUp = entries.Where(e => e.StartDate > today).Select(Row).ToList(),
            Done = entries.Where(e => e.EndDate < today).OrderByDescending(e => e.StartDate).Select(Row).ToList(),
            LayerNames = await _db.Layers.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.DisplayName),
        });
    }

    public async Task<IActionResult> Detail(long id)
    {
        var entry = await _ai.CampaignCalendar.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id);
        if (entry is null) return NotFound();
        var briefs = await _ai.CampaignBriefs.AsNoTracking().Where(b => b.CalendarId == id && !b.IsTest).OrderByDescending(b => b.Id).ToListAsync();
        var briefIds = briefs.Select(b => b.Id).ToList();
        var plans = await _ai.CampaignPlans.AsNoTracking().Where(p => briefIds.Contains(p.BriefId) && !p.IsTest).OrderByDescending(p => p.Id).ToListAsync();
        var batchIds = plans.Where(p => p.PublishBatchId != null).Select(p => p.PublishBatchId!.Value).ToList();
        var latest = plans.FirstOrDefault();
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
        return RedirectToAction(nameof(Detail), new { id = brief.CalendarId ?? 0 });
    }

    private string Who => User.Identity?.Name ?? "unknown";
}
