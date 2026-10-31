using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PricingTool.Data;

namespace PricingTool.Web.ViewComponents;

/// <summary>One <c>hk.*</c> table's latest load: when, from where, how many rows (a row of <c>hk.DataSources</c>).</summary>
public sealed record DataLoad(string TableName, string Source, DateTime ExtractedAtUtc, long RowCount);

/// <summary>Where the data on every page comes from (plan section 5, "Footer"): the latest load of each hk table, read from
/// <c>hk.DataSources</c> and cached for five minutes. A failure to read never breaks the page; the line is left out.</summary>
public class DataStatusViewComponent : ViewComponent
{
    private readonly PricingToolDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DataStatusViewComponent> _logger;

    public DataStatusViewComponent(PricingToolDbContext db, IMemoryCache cache, ILogger<DataStatusViewComponent> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    /// <param name="view">"Strip" for the line under the navigation, "Footer" for the per-table list.</param>
    public async Task<IViewComponentResult> InvokeAsync(string view)
    {
        try
        {
            var loads = await _cache.GetOrCreateAsync("hk.DataSources.latest", async e =>
            {
                e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                return await _db.Database.SqlQueryRaw<DataLoad>("""
                    SELECT d.TableName, d.Source, d.ExtractedAtUtc, CAST(d.[RowCount] AS bigint) AS [RowCount]
                    FROM hk.DataSources d
                    WHERE d.Id = (SELECT MAX(x.Id) FROM hk.DataSources x WHERE x.TableName = d.TableName)
                    """).ToListAsync();
            }) ?? new List<DataLoad>();
            return View(view, loads.OrderBy(l => l.TableName, StringComparer.OrdinalIgnoreCase).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read hk.DataSources for the page footer.");
            return Content(string.Empty);
        }
    }
}
