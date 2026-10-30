using Microsoft.EntityFrameworkCore;
using PricingTool.Data;
using PricingTool.Data.Entities;

namespace PricingTool.Web.Services;

/// <summary>
/// Resolves the "current layer" for the request from session (falling back to the first active
/// layer), and exposes the active-layer list for the nav. Scoped — values are cached per request.
/// </summary>
public class CurrentLayerService
{
    public const string SessionKey = "SelectedLayerId";

    private readonly PricingToolDbContext _db;
    private readonly IHttpContextAccessor _http;
    private List<Layer>? _active;
    private Layer? _current;

    public CurrentLayerService(PricingToolDbContext db, IHttpContextAccessor http)
    {
        _db = db;
        _http = http;
    }

    /// <summary>
    /// Active layers in seed order (SortOrder): GjirafaMall KS/MK/AL then Gjirafa50 KS/MK/AL. This
    /// makes GjirafaMall KS the default landing layer and keeps brands contiguous for the nav grouping.
    /// </summary>
    public async Task<IReadOnlyList<Layer>> GetActiveLayersAsync(CancellationToken ct = default)
        => _active ??= await _db.Layers.AsNoTracking()
            .Where(l => l.IsActive)
            .OrderBy(l => l.SortOrder).ThenBy(l => l.Brand)
            .ToListAsync(ct);

    /// <summary>The selected layer (from session) or the first active layer; null only if none are active.</summary>
    public async Task<Layer?> GetCurrentAsync(CancellationToken ct = default)
    {
        if (_current is not null) return _current;
        var active = await GetActiveLayersAsync(ct);
        if (active.Count == 0) return null;
        var sessionId = _http.HttpContext?.Session.GetInt32(SessionKey);
        _current = active.FirstOrDefault(l => l.Id == sessionId) ?? active[0];
        return _current;
    }

    /// <summary>Current layer id; throws if no layer is active (a misconfiguration, not a normal state).</summary>
    public async Task<int> RequireCurrentIdAsync(CancellationToken ct = default)
        => (await GetCurrentAsync(ct))?.Id
           ?? throw new InvalidOperationException("No active layer is configured.");
}
