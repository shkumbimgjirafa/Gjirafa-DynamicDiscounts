using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PricingTool.Data;
using PricingTool.Web.Models;
using PricingTool.Web.Services;

namespace PricingTool.Web.Controllers;

[Authorize(Roles = "Analyst,Manager")]
public class AuditController : Controller
{
    private readonly PricingToolDbContext _db;
    private readonly CurrentLayerService _layers;

    public AuditController(PricingToolDbContext db, CurrentLayerService layers)
    {
        _db = db;
        _layers = layers;
    }

    public async Task<IActionResult> Index(string? search, string? category, DateTime? from, DateTime? to)
    {
        var layerId = await _layers.RequireCurrentIdAsync();
        // Show this layer's entries plus legacy/global (null-layer) entries.
        var query = _db.AuditLog.AsNoTracking()
            .Where(e => e.LayerId == layerId || e.LayerId == null);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(e =>
                e.Action.Contains(search) || e.UserName.Contains(search) ||
                (e.EntityId != null && e.EntityId.Contains(search)) ||
                (e.OldValue != null && e.OldValue.Contains(search)) ||
                (e.NewValue != null && e.NewValue.Contains(search)));

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(e => e.Category == category);

        if (from.HasValue) query = query.Where(e => e.TimestampUtc >= from.Value);
        if (to.HasValue) query = query.Where(e => e.TimestampUtc < to.Value.AddDays(1));

        var model = new AuditViewModel
        {
            Search = search,
            Category = category,
            From = from,
            To = to,
            Entries = await query.OrderByDescending(e => e.Id).Take(200).ToListAsync(),
        };
        return View(model);
    }
}
