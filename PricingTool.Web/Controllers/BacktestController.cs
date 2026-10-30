using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PricingTool.Agent.Backtest;
using PricingTool.Agent.Storage;

namespace PricingTool.Web.Controllers;

/// <summary>
/// Backtest results (plan section 5, Web): the latest stored run of every lever, monthly per layer and elasticity case, the campaign
/// replays and the planner's calibration, lever 4 on MK/AL, with what each rests on, and the whole run as CSV. The run itself is
/// stored by <c>PricingTool.AgentTools backtest-snapshot</c> (about a minute), so the page opens at once.
/// </summary>
[Authorize(Roles = "Analyst,Manager")]
public class BacktestController : Controller
{
    private readonly AgentDbContext _ai;
    public BacktestController(AgentDbContext ai) => _ai = ai;

    public async Task<IActionResult> Index()
    {
        var record = await _ai.BacktestSnapshots.AsNoTracking().OrderByDescending(s => s.Id).FirstOrDefaultAsync();
        ViewData["Record"] = record;
        return View(BacktestSnapshotBuilder.Read(record));
    }

    public async Task<IActionResult> Csv()
    {
        var record = await _ai.BacktestSnapshots.AsNoTracking().OrderByDescending(s => s.Id).FirstOrDefaultAsync();
        if (BacktestSnapshotBuilder.Read(record) is not { } data) return NotFound();
        return File(Encoding.UTF8.GetBytes(BacktestSnapshotBuilder.Csv(data)), "text/csv", $"backtest-{record!.CreatedUtc:yyyyMMdd-HHmm}.csv");
    }
}
