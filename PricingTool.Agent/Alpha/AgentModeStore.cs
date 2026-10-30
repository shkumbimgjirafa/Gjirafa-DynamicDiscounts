using Microsoft.EntityFrameworkCore;
using PricingTool.Data;
using PricingTool.Data.Entities;

namespace PricingTool.Agent.Alpha;

/// <summary>How much the agent may do on a layer without a person.</summary>
public enum AutonomyMode
{
    /// <summary>The agent does not run on this layer.</summary>
    Off,
    /// <summary>Every decision within the limits waits for one click.</summary>
    ApproveFirst,
    /// <summary>Decisions within the limits apply at once; only what's outside them waits for a person.</summary>
    Autonomous,
}

/// <summary>The autonomy switch per layer, kept in the tool's ToolSettings ("Agent.Mode.L&lt;layer&gt;").
/// A layer without a setting is <see cref="AutonomyMode.ApproveFirst"/>: nothing applies by itself.</summary>
public sealed class AgentModeStore
{
    /// <summary>Audit category for everything the agents change.</summary>
    public const string AuditCategory = "Agent";

    private readonly PricingToolDbContext _db;

    public AgentModeStore(PricingToolDbContext db) => _db = db;

    public static string Key(int layerId) => $"Agent.Mode.L{layerId}";

    public async Task<AutonomyMode> GetAsync(int layerId, CancellationToken ct = default)
    {
        var value = await _db.ToolSettings.AsNoTracking().Where(s => s.Key == Key(layerId)).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return Enum.TryParse<AutonomyMode>(value, ignoreCase: true, out var mode) ? mode : AutonomyMode.ApproveFirst;
    }

    public async Task SetAsync(int layerId, AutonomyMode mode, string by, CancellationToken ct = default)
    {
        var setting = await _db.ToolSettings.FirstOrDefaultAsync(s => s.Key == Key(layerId), ct);
        var old = setting?.Value;
        if (old == mode.ToString()) return;
        if (setting is null)
        {
            setting = new ToolSetting { Key = Key(layerId) };
            _db.ToolSettings.Add(setting);
        }
        setting.Value = mode.ToString();
        setting.UpdatedUtc = DateTime.UtcNow;
        setting.UpdatedBy = by;
        _db.AuditLog.Add(new AuditLogEntry
        {
            LayerId = layerId, TimestampUtc = DateTime.UtcNow, UserName = by, Category = AuditCategory,
            Action = "Autonomy mode changed", EntityType = "ToolSetting", EntityId = Key(layerId),
            OldValue = old ?? "(unset: ApproveFirst)", NewValue = mode.ToString(),
        });
        await _db.SaveChangesAsync(ct);
    }
}
