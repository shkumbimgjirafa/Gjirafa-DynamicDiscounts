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

    public async Task<IActionResult> Index()
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
        return View(new AgentStatusViewModel
        {
            KillSwitch = await _killSwitch.GetAsync(),
            Layers = autonomy,
            Publisher = _publisher,
            Waiting = inbox.WaitingCount,
            RecentPasses = passes,
            Changed = changed,
            LayerNames = inbox.LayerNames,
        });
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

    private string Who => User.Identity?.Name ?? "unknown";
}
