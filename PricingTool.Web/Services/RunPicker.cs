using Microsoft.EntityFrameworkCore;
using PricingTool.Data;
using PricingTool.Data.Entities;

namespace PricingTool.Web.Services;

/// <summary>Which run a page opens on when none is picked.</summary>
public static class RunPicker
{
    /// <summary>The newest finished run of a layer that still has its proposals stored, and the newer run passed over when
    /// there is one. Retention deletes the proposals of older local runs to keep LocalDB under its cap, so the newest run's
    /// header can have no rows behind it; a page opening on it would show nothing.</summary>
    public static async Task<(PricingRun? Run, PricingRun? NewerWithoutRows)> LatestWithRowsAsync(PricingToolDbContext db, int layerId, CancellationToken ct = default)
    {
        var finished = db.PricingRuns.Where(r => r.LayerId == layerId && r.Status != RunStatus.Running);
        var newest = await finished.OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);
        if (newest is null) return (null, null);
        var withRows = await finished.Where(r => db.ProposedPrices.Any(p => p.PricingRunId == r.Id)).OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);
        return withRows is null || withRows.Id == newest.Id ? (newest, null) : (withRows, newest);
    }

    /// <summary>The line a page shows when it passed over a newer run without rows.</summary>
    public static string? Note(PricingRun? passedOver, PricingRun? shown) => passedOver is null || shown is null ? null
        : $"Showing run #{shown.Id}: the newer run #{passedOver.Id} has no proposals stored (cleaned up to keep the database under its size cap), so there is nothing to list from it.";
}
