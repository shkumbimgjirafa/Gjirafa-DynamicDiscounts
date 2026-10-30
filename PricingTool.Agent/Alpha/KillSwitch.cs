using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PricingTool.Data;
using PricingTool.Data.Entities;

namespace PricingTool.Agent.Alpha;

/// <summary>Whether the agents and the publisher may act right now, and if not, why.
/// <paramref name="ByConfiguration"/>: stopped by <c>Agent:Enabled = false</c>, which releasing at runtime can't override.</summary>
public sealed record KillSwitchState(bool Stopped, string Reason, bool ByConfiguration = false)
{
    public static readonly KillSwitchState Running = new(false, "");
}

/// <summary>
/// The kill switch: it stops every agent call, Alpha's pass, the run checker (so runs stay blocked) and the
/// publisher at once. A person can still review held items and roll back a publish while it is pulled.
/// </summary>
public interface IKillSwitch
{
    Task<KillSwitchState> GetAsync(CancellationToken ct = default);
}

/// <summary>Configuration only (<c>Agent:Enabled</c>): what a component falls back to when no runtime switch is
/// wired in (unit tests that construct it directly).</summary>
public sealed class ConfigKillSwitch : IKillSwitch
{
    private readonly AgentOptions _options;

    public ConfigKillSwitch(AgentOptions options) => _options = options;

    public Task<KillSwitchState> GetAsync(CancellationToken ct = default) =>
        Task.FromResult(_options.Enabled ? KillSwitchState.Running : KillSwitch.OffInConfig);
}

/// <summary>
/// The runtime kill switch: <c>Agent:Enabled</c> in configuration (the master: off there means off) and the
/// tool's ToolSetting <see cref="SettingKey"/>, which a Manager pulls or releases from the Agent page without a
/// restart. No setting means running. Every change is audited (category Agent).
/// </summary>
public sealed class KillSwitch : IKillSwitch
{
    public const string SettingKey = "Agent.Enabled";

    internal static readonly KillSwitchState OffInConfig = new(true, "Agent:Enabled is off in configuration", ByConfiguration: true);

    private readonly PricingToolDbContext _db;
    private readonly AgentOptions _options;
    private readonly TimeProvider _clock;

    public KillSwitch(PricingToolDbContext db, IOptions<AgentOptions> options, TimeProvider? clock = null)
    {
        _db = db;
        _options = options.Value;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<KillSwitchState> GetAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled) return OffInConfig;
        var setting = await _db.ToolSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SettingKey, ct);
        return setting is not null && string.Equals(setting.Value, "false", StringComparison.OrdinalIgnoreCase)
            ? new KillSwitchState(true, $"pulled by {setting.UpdatedBy} at {setting.UpdatedUtc:yyyy-MM-dd HH:mm} UTC")
            : KillSwitchState.Running;
    }

    /// <summary>Pulls (<paramref name="stop"/> = true) or releases the switch. Releasing can't override
    /// <c>Agent:Enabled = false</c> in configuration.</summary>
    public async Task SetAsync(bool stop, string by, CancellationToken ct = default)
    {
        var value = stop ? "false" : "true";
        var setting = await _db.ToolSettings.FirstOrDefaultAsync(s => s.Key == SettingKey, ct);
        var old = setting?.Value;
        if (string.Equals(old ?? "true", value, StringComparison.OrdinalIgnoreCase)) return;
        if (setting is null)
        {
            setting = new ToolSetting { Key = SettingKey };
            _db.ToolSettings.Add(setting);
        }
        var now = _clock.GetUtcNow().UtcDateTime;
        setting.Value = value;
        setting.UpdatedUtc = now;
        setting.UpdatedBy = by;
        _db.AuditLog.Add(new AuditLogEntry
        {
            TimestampUtc = now, UserName = by, Category = AgentModeStore.AuditCategory,
            Action = stop ? "Kill switch pulled: agents and publisher stopped" : "Kill switch released",
            EntityType = "ToolSetting", EntityId = SettingKey,
            OldValue = old ?? "(unset: running)", NewValue = value,
        });
        await _db.SaveChangesAsync(ct);
    }
}
