using Microsoft.EntityFrameworkCore;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Storage;
using PricingTool.Data;
using PricingTool.Data.Entities;

namespace PricingTool.Agent.Campaigns;

/// <summary>
/// The engine settings a person asked to change for a campaign's days, applied by the daily cycle before the engine runs:
/// today the engine's discount optimization (the Schedule page's trim of deep displayed discounts). On a campaign's days
/// the layer's setting is switched off (and the campaign remembers that it did); on the first day with no such campaign
/// running, a setting the campaigns switched off is switched back on. Each change is audited. Idempotent.
/// </summary>
public sealed class CampaignSwitches
{
    public const string AuditUser = "campaign switches";

    private readonly AgentDbContext _db;
    private readonly PricingToolDbContext _tool;
    private readonly TimeProvider _clock;

    public CampaignSwitches(AgentDbContext db, PricingToolDbContext tool, TimeProvider? clock = null)
    {
        _db = db;
        _tool = tool;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>What changed, in words ("" when nothing did).</summary>
    public async Task<string> ApplyAsync(DateOnly today, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var entries = await _db.CampaignCalendar
            .Where(c => c.DiscountOptimizationOff && c.Status != CampaignStatus.Cancelled || c.DiscountOptimizationSwitchedOffUtc != null).ToListAsync(ct);
        var done = new List<string>();
        foreach (var layerId in entries.Select(e => e.LayerId).Distinct().OrderBy(l => l))
        {
            var layer = await _tool.Layers.SingleOrDefaultAsync(l => l.Id == layerId, ct);
            if (layer is null) continue;
            var running = entries.Where(e => e.LayerId == layerId && e.DiscountOptimizationOff && e.Status != CampaignStatus.Cancelled
                                             && e.StartDate <= today && e.EndDate >= today).ToList();
            if (running.Count > 0)
            {
                if (!layer.DiscountOptimizationEnabled) continue;
                layer.DiscountOptimizationEnabled = false;
                running[0].DiscountOptimizationSwitchedOffUtc = now;
                Audit(layerId, $"Discount optimization off for {running[0].Name} ({running[0].StartDate:dd MMM}–{running[0].EndDate:dd MMM})", "on", "off", running[0].Id);
                done.Add($"layer {layerId}: discount optimization off for {running[0].Name}");
                continue;
            }
            var switchedOff = entries.Where(e => e.LayerId == layerId && e.DiscountOptimizationSwitchedOffUtc != null).ToList();
            if (switchedOff.Count == 0) continue;
            if (!layer.DiscountOptimizationEnabled)
            {
                layer.DiscountOptimizationEnabled = true;
                Audit(layerId, $"Discount optimization back on after {switchedOff[0].Name}", "off", "on", switchedOff[0].Id);
                done.Add($"layer {layerId}: discount optimization back on after {switchedOff[0].Name}");
            }
            foreach (var e in switchedOff) e.DiscountOptimizationSwitchedOffUtc = null;
        }
        await _tool.SaveChangesAsync(ct);
        await _db.SaveChangesAsync(ct);
        return string.Join("; ", done);
    }

    private void Audit(int layerId, string action, string oldValue, string newValue, long calendarId) => _tool.AuditLog.Add(new AuditLogEntry
    {
        LayerId = layerId, TimestampUtc = _clock.GetUtcNow().UtcDateTime, UserName = AuditUser, Category = AgentModeStore.AuditCategory, Action = action,
        EntityType = nameof(CampaignCalendarEntry), EntityId = calendarId.ToString(System.Globalization.CultureInfo.InvariantCulture), OldValue = oldValue, NewValue = newValue,
    });
}
