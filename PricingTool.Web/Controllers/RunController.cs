using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PricingTool.Agent;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Operations;
using PricingTool.NopIntegration;
using PricingTool.Web.Models;
using PricingTool.Web.Services;

namespace PricingTool.Web.Controllers;

/// <summary>
/// The Run tab: the current run of the layer picked, treated as live. Its floors and targets by category × band, what it
/// changed, whether it's working, and Alpha's account of it; Run week and the switches a person holds over it.
/// </summary>
[Authorize(Roles = "Analyst,Manager")]
public class RunController : Controller
{
    private readonly RunReportService _reports;
    private readonly AgentActivity _activity;
    private readonly KillSwitch _killSwitch;
    private readonly AgentModeStore _modes;
    private readonly CurrentLayerService _layers;
    private readonly RunWeekLauncher _launcher;
    private readonly PublisherOptions _publisher;
    private readonly AgentOptions _agent;

    public RunController(RunReportService reports, AgentActivity activity, KillSwitch killSwitch, AgentModeStore modes, CurrentLayerService layers,
        RunWeekLauncher launcher, IOptions<PublisherOptions> publisher, IOptions<AgentOptions> agent)
    {
        _reports = reports;
        _activity = activity;
        _killSwitch = killSwitch;
        _modes = modes;
        _layers = layers;
        _launcher = launcher;
        _publisher = publisher.Value;
        _agent = agent.Value;
    }

    public async Task<IActionResult> Index()
    {
        var layerId = await _layers.RequireCurrentIdAsync();
        var holdouts = _agent.Limits.Holdouts.TryGetValue(layerId.ToString(CultureInfo.InvariantCulture), out var h) ? h : new List<string>();
        var report = await _reports.BuildAsync(layerId, holdouts, HttpContext.RequestAborted);
        return View(new RunPageModel
        {
            Report = report,
            PriceListRunWeekId = report.Week is { } w && RunWeekPriceList.FindFile(_agent.Cycle.ExportDir(), w.Id) is not null ? w.Id : null,
            KillSwitch = await _killSwitch.GetAsync(),
            Mode = await _modes.GetAsync(layerId),
            Publisher = _publisher,
            CanRunWeek = _agent.Cycle.Layers.Contains(layerId),
            RunWeekRunning = _launcher.IsRunning,
            Week = await _activity.WeekAsync(DateTime.UtcNow.AddDays(-7)),
        });
    }
    /// <summary>A Run week's price list (<see cref="RunWeekPriceList"/>): the CSV its last step wrote to the exports folder.</summary>
    [HttpGet("/Run/PriceList/{id:long}")]
    public IActionResult PriceList(long id)
    {
        var path = RunWeekPriceList.FindFile(_agent.Cycle.ExportDir(), id);
        return path is null ? NotFound($"Run week #{id} has no price list (it writes one only after a publish that went through).")
            : PhysicalFile(path, "text/csv", Path.GetFileName(path));
    }
}
