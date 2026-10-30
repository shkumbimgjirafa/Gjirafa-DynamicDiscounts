using Microsoft.EntityFrameworkCore;
using PricingTool.Data.Entities;

namespace PricingTool.Data.Services;

public record ScheduleInfo(TimeOnly RunTimeUtc, int CadenceHours, DateTime? LastScheduledRunUtc);

/// <summary>Per-layer schedule, stored on the Layer row; the worker reads each active layer every cycle.</summary>
public class ScheduleService
{
    private readonly PricingToolDbContext _db;

    public ScheduleService(PricingToolDbContext db) => _db = db;

    public async Task<ScheduleInfo> GetAsync(int layerId, CancellationToken ct = default)
    {
        var layer = await _db.Layers.AsNoTracking().FirstOrDefaultAsync(l => l.Id == layerId, ct)
            ?? throw new InvalidOperationException($"Layer {layerId} not found.");
        return ToInfo(layer);
    }

    /// <summary>Maps a layer's stored schedule fields to a <see cref="ScheduleInfo"/>.</summary>
    public static ScheduleInfo ToInfo(Layer layer)
    {
        var runTime = TimeOnly.TryParse(layer.RunTimeUtc, out var parsed) ? parsed : new TimeOnly(3, 0);
        var cadence = layer.CadenceHours > 0 ? layer.CadenceHours : 24;
        return new ScheduleInfo(runTime, cadence, layer.LastScheduledRunUtc);
    }

    /// <summary>Updates the run time / cadence, the new-product handling and the discount-optimization
    /// switch for one layer.</summary>
    public async Task SetScheduleAsync(int layerId, string runTimeUtc, int cadenceHours, bool ignoreNewProducts,
        bool discountOptimizationEnabled, CancellationToken ct = default)
    {
        var layer = await _db.Layers.FirstOrDefaultAsync(l => l.Id == layerId, ct)
            ?? throw new InvalidOperationException($"Layer {layerId} not found.");
        layer.RunTimeUtc = runTimeUtc;
        layer.CadenceHours = cadenceHours;
        layer.IgnoreNewProducts = ignoreNewProducts;
        layer.DiscountOptimizationEnabled = discountOptimizationEnabled;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Records the last scheduled-run timestamp for one layer (set by the worker).</summary>
    public async Task SetLastScheduledRunAsync(int layerId, DateTime utc, CancellationToken ct = default)
    {
        var layer = await _db.Layers.FirstOrDefaultAsync(l => l.Id == layerId, ct);
        if (layer is null) return;
        layer.LastScheduledRunUtc = utc;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The next scheduled run STRICTLY AFTER <paramref name="nowUtc"/> (for display). Slots sit at the
    /// configured <c>RunTimeUtc</c> time-of-day, spaced by the cadence, phased on the last served slot — so
    /// the spacing is a true <c>CadenceHours</c> AND the time-of-day follows RunTimeUtc (changing it shifts
    /// future slots). (The old now.Date-anchored grid re-based every tick, which collapsed any cadence
    /// &gt; 24h to roughly daily.) Due-ness is decided by <see cref="DueSlot"/>, NOT by comparing against
    /// this value. When the layer has never run, returns the next upcoming time-of-day slot at/after now.
    /// </summary>
    public static DateTime ComputeNextRun(ScheduleInfo schedule, DateTime nowUtc)
    {
        var runTime = schedule.RunTimeUtc.ToTimeSpan();
        var cadence = TimeSpan.FromHours(schedule.CadenceHours);

        if (schedule.LastScheduledRunUtc is DateTime last)
        {
            var slot = last.Date.Add(runTime);
            while (slot <= last) slot = slot.Add(cadence);    // first run-time slot strictly after the last run
            while (slot <= nowUtc) slot = slot.Add(cadence);  // …then the first strictly after now (display)
            return slot;
        }

        // Never run: the next upcoming time-of-day slot from now.
        var s = nowUtc.Date.Add(runTime);
        while (s < nowUtc) s = s.Add(cadence);
        return s;
    }

    /// <summary>
    /// The scheduled slot a run starting at <paramref name="nowUtc"/> should serve, or null when the layer
    /// is not yet due. Slots sit at the <c>RunTimeUtc</c> time-of-day, spaced by the cadence, phased on the
    /// last served slot; the worker stamps the returned value as <c>LastScheduledRunUtc</c> (NOT wall-clock
    /// now) so the phase stays stable. A layer is due once its next run-time slot has arrived; a catch-up
    /// after an outage serves the CURRENT period exactly once (the latest slot at/before now), so it never
    /// fires a second run later the same day and never fires one run per missed slot. A never-run layer is
    /// due immediately and anchors on the most recent time-of-day slot at/before now.
    /// </summary>
    public static DateTime? DueSlot(ScheduleInfo schedule, DateTime nowUtc)
    {
        var runTime = schedule.RunTimeUtc.ToTimeSpan();
        var cadence = TimeSpan.FromHours(schedule.CadenceHours);

        if (schedule.LastScheduledRunUtc is not DateTime last)
        {
            // Never run: anchor on the most recent time-of-day slot at/before now, and run immediately.
            var s = nowUtc.Date.Add(runTime);
            while (s > nowUtc) s = s.Subtract(cadence);
            return s;
        }

        var slot = last.Date.Add(runTime);
        while (slot <= last) slot = slot.Add(cadence);              // first run-time slot strictly after last
        if (nowUtc < slot) return null;                            // not due yet
        while (slot.Add(cadence) <= nowUtc) slot = slot.Add(cadence); // catch up to the current period, once
        return slot;
    }
}
