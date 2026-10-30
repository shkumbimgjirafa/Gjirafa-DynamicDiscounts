using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PricingTool.Core.Abstractions;
using PricingTool.Data.Entities;

namespace PricingTool.Data.Services;

/// <summary>
/// Recomputes each recent approval's before/after realized profit (SR <c>SUM(Margin)</c>) for its SKU set,
/// over the 7/30/90-day windows around its <see cref="Approval.ApprovedUtc"/>, and stores them on the
/// Approval. Runs on the same hourly cadence as the approved-batch refresh so the dashboard's per-approval
/// "did the bet pay off?" figures stay current — the "after" windows grow as calendar time passes the
/// approval date. Approvals whose widest (90-day) after-window has fully matured are left static.
/// </summary>
public class ApprovalProfitRefreshService
{
    /// <summary>Approvals approved within this many days are still refreshed (their 90-day after-window can
    /// still grow); older ones are treated as final and left static. 90 (widest window) + 10 days grace.</summary>
    public const int RefreshWindowDays = 100;

    private readonly PricingToolDbContext _db;
    private readonly ISalesReportProfitReader _profit;
    private readonly ILogger<ApprovalProfitRefreshService> _logger;

    public ApprovalProfitRefreshService(
        PricingToolDbContext db, ISalesReportProfitReader profit, ILogger<ApprovalProfitRefreshService> logger)
    {
        _db = db;
        _profit = profit;
        _logger = logger;
    }

    /// <summary>Refreshes every approval approved within the tracking window; a failure on one is logged and
    /// never stops the rest. Returns the number refreshed.</summary>
    public async Task<int> RefreshTrackedApprovalsAsync(CancellationToken ct = default)
    {
        if (_db.Database.IsRelational()) _db.Database.SetCommandTimeout(600);

        var cutoff = DateTime.UtcNow.Date.AddDays(-RefreshWindowDays);
        var approvals = await _db.Approvals.Where(a => a.ApprovedUtc >= cutoff).OrderBy(a => a.Id).ToListAsync(ct);
        var refreshed = 0;
        foreach (var approval in approvals)
        {
            try
            {
                await RefreshApprovalAsync(approval, ct);
                refreshed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Approval profit refresh failed for approval {Id}.", approval.Id);
            }
        }
        return refreshed;
    }

    /// <summary>Recomputes one approval's six before/after profit windows and stamps ProfitRefreshedUtc.</summary>
    public async Task RefreshApprovalAsync(Approval approval, CancellationToken ct = default)
    {
        var layer = await _db.Layers.AsNoTracking().FirstOrDefaultAsync(l => l.Id == approval.LayerId, ct);
        if (layer is null)
        {
            _logger.LogWarning("Approval profit refresh: layer {LayerId} for approval {Id} not found; skipped.",
                approval.LayerId, approval.Id);
            return;
        }

        var skus = await _db.ApprovalItems.Where(i => i.ApprovalId == approval.Id)
            .Select(i => i.Sku).ToListAsync(ct);
        if (skus.Count == 0) return;

        var w = await _profit.GetApprovalProfitWindowsAsync(
            layer.SrPlatformId, layer.SrCompanyId, layer.OperationalDatabase, skus, approval.ApprovedUtc, ct);

        approval.Profit7Before = w.Profit7Before;
        approval.Profit7After = w.Profit7After;
        approval.Profit30Before = w.Profit30Before;
        approval.Profit30After = w.Profit30After;
        approval.Profit90Before = w.Profit90Before;
        approval.Profit90After = w.Profit90After;
        approval.ProfitRefreshedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Approval {Id} profit refreshed ({Skus} SKUs): 30d {Before}→{After}.",
            approval.Id, skus.Count, w.Profit30Before, w.Profit30After);
    }
}
