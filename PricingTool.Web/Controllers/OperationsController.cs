using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PricingTool.Agent;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Checker;
using PricingTool.Agent.Operations;
using PricingTool.Agent.Storage;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Web.Models;

namespace PricingTool.Web.Controllers;

/// <summary>The laptop operation (prompt 05): the money ledger with the exports waiting for the push, the hours-saved
/// time sheet, and the daily cycle's steps. Every figure here comes from the app's own tables.</summary>
[Authorize(Roles = "Analyst,Manager")]
public class OperationsController : Controller
{
    private readonly AgentDbContext _ai;
    private readonly PricingToolDbContext _db;
    private readonly AgentOptions _options;

    public OperationsController(AgentDbContext ai, PricingToolDbContext db, IOptions<AgentOptions> options)
    {
        _ai = ai;
        _db = db;
        _options = options.Value;
    }

    public async Task<IActionResult> Ledger([FromServices] MoneyLedger ledger) => View(new LedgerViewModel
    {
        Summary = await ledger.SummaryAsync(),
        Entries = await _ai.MoneyLedger.AsNoTracking().OrderByDescending(e => e.Id).ToListAsync(),
        Exports = await _ai.ExportBatches.AsNoTracking().OrderByDescending(b => b.Id).Take(30).ToListAsync(),
        Holdouts = _options.Limits.Holdouts,
    });

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkPushed(long exportBatchId, [FromServices] CheckedExport export)
    {
        var by = $"{_options.Cycle.PushedBy}, marked by {User.Identity?.Name ?? "unknown"}";
        try
        {
            var approvalId = await export.MarkPushedAsync(exportBatchId, by);
            TempData["Message"] = $"Export #{exportBatchId} recorded as pushed (approval #{approvalId}). The ledger measures it at 7 and 30 days.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Ledger));
    }

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveRaise(string monthlyRaiseEur)
    {
        if (!decimal.TryParse(monthlyRaiseEur, NumberStyles.Number, CultureInfo.InvariantCulture, out var raise) || raise <= 0)
        {
            TempData["Error"] = "The monthly raise must be a positive amount in euros (e.g. 400 or 400.50).";
            return RedirectToAction(nameof(Ledger));
        }
        var value = raise.ToString("0.##", CultureInfo.InvariantCulture);
        await SaveSettingAsync(MoneyLedger.RaiseSettingKey, value, "Monthly raise for the bar set");
        TempData["Message"] = $"Monthly raise set to €{value}: the bar is €{raise * 3:0.##} measured in 30 days.";
        return RedirectToAction(nameof(Ledger));
    }

    public async Task<IActionResult> TimeSheet() => View(new TimeSheetViewModel
    {
        TaskTypes = await _ai.TimeSheetTaskTypes.AsNoTracking().OrderBy(t => t.Id).ToListAsync(),
        Entries = await _ai.TimeSheetEntries.AsNoTracking().OrderByDescending(e => e.Id).Take(200).ToListAsync(),
        Files = LatestFiles(_options.Cycle.TimeSheetDir(), "time-sheet-*.xlsx", 5),
        OwnerEmailSet = !string.IsNullOrWhiteSpace(_options.TimeSheetOwnerEmail),
    });

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveMinutesBefore(int taskTypeId, int minutesBefore)
    {
        if (minutesBefore is < 1 or > 24 * 60)
        {
            TempData["Error"] = "Minutes before must be between 1 and 1440 (the measured time of the task done by hand).";
            return RedirectToAction(nameof(TimeSheet));
        }
        var type = await _ai.TimeSheetTaskTypes.SingleAsync(t => t.Id == taskTypeId);
        var old = type.MinutesBefore;
        type.MinutesBefore = minutesBefore;
        await _ai.SaveChangesAsync();
        _db.AuditLog.Add(new AuditLogEntry
        {
            TimestampUtc = DateTime.UtcNow, UserName = User.Identity?.Name ?? "unknown", Category = AgentModeStore.AuditCategory,
            Action = "Time sheet: minutes before set", EntityType = nameof(TimeSheetTaskType), EntityId = $"{type.Id} {type.Name}",
            OldValue = old?.ToString(CultureInfo.InvariantCulture) ?? "(not set)", NewValue = minutesBefore.ToString(CultureInfo.InvariantCulture),
        });
        await _db.SaveChangesAsync();
        TempData["Message"] = $"\"{type.Name}\": {minutesBefore} minutes by hand. Its rows are exported from now on.";
        return RedirectToAction(nameof(TimeSheet));
    }

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ExportTimeSheet([FromServices] TimeSheetExporter exporter)
    {
        var r = await exporter.ExportAsync();
        TempData["Message"] = $"{r.Exported} rows exported to {r.XlsxPath} (+ .csv, .json); {r.WithoutMinutesBefore} rows wait for their task type's minutes before; " +
                              $"{r.BeforePeriod} rows from before {TimeSheetExporter.PeriodStart:d MMM} kept, not exported.";
        return RedirectToAction(nameof(TimeSheet));
    }

    public async Task<IActionResult> Cycle([FromServices] IKillSwitch killSwitch)
    {
        var since = DateOnly.FromDateTime(DateTime.Now).AddDays(-6);
        return View(new CycleViewModel
        {
            Steps = await _ai.DailyCycleSteps.AsNoTracking().Where(s => s.CycleDate >= since)
                .OrderByDescending(s => s.CycleDate).ThenBy(s => s.Id).ToListAsync(),
            Cycle = _options.Cycle,
            AgentEnabled = !(await killSwitch.GetAsync()).Stopped,
        });
    }

    private async Task SaveSettingAsync(string key, string value, string action)
    {
        var user = User.Identity?.Name ?? "unknown";
        var setting = await _db.ToolSettings.FirstOrDefaultAsync(s => s.Key == key);
        var old = setting?.Value;
        if (setting is null)
        {
            setting = new ToolSetting { Key = key };
            _db.ToolSettings.Add(setting);
        }
        setting.Value = value;
        setting.UpdatedUtc = DateTime.UtcNow;
        setting.UpdatedBy = user;
        _db.AuditLog.Add(new AuditLogEntry
        {
            TimestampUtc = DateTime.UtcNow, UserName = user, Category = AgentModeStore.AuditCategory, Action = action,
            EntityType = nameof(ToolSetting), EntityId = key, OldValue = old ?? "(not set)", NewValue = value,
        });
        await _db.SaveChangesAsync();
    }

    private static List<FileInfo> LatestFiles(string dir, string pattern, int take) =>
        Directory.Exists(dir) ? new DirectoryInfo(dir).GetFiles(pattern).OrderByDescending(f => f.LastWriteTimeUtc).Take(take).ToList() : new();
}
