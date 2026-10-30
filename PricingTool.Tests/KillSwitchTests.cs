using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PricingTool.Agent;
using PricingTool.Agent.Alpha;
using PricingTool.Data;

namespace PricingTool.Tests;

/// <summary>The runtime kill switch: configuration is the master, the ToolSetting is pulled and released at
/// runtime, and every change is audited.</summary>
public class KillSwitchTests : IDisposable
{
    private readonly PricingToolDbContext _db = new(new DbContextOptionsBuilder<PricingToolDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose() => _db.Dispose();

    private KillSwitch Switch(bool enabledInConfig = true) => new(_db, Options.Create(new AgentOptions { Enabled = enabledInConfig }));

    [Fact]
    public async Task Runs_when_nothing_was_ever_pulled()
    {
        Assert.False((await Switch().GetAsync()).Stopped);
    }

    [Fact]
    public async Task Pulling_stops_and_says_who_releasing_runs_again_and_both_are_audited()
    {
        var ks = Switch();

        await ks.SetAsync(stop: true, "manager@test");
        var pulled = await ks.GetAsync();
        await ks.SetAsync(stop: true, "manager@test");                 // already pulled: nothing new
        await ks.SetAsync(stop: false, "manager@test");

        Assert.True(pulled.Stopped);
        Assert.Contains("pulled by manager@test", pulled.Reason);
        Assert.False((await ks.GetAsync()).Stopped);
        var audit = await _db.AuditLog.OrderBy(a => a.Id).Select(a => new { a.Action, a.NewValue, a.Category }).ToListAsync();
        Assert.Equal(2, audit.Count);
        Assert.StartsWith("Kill switch pulled", audit[0].Action);
        Assert.Equal(("Kill switch released", "true", "Agent"), (audit[1].Action, audit[1].NewValue, audit[1].Category));
    }

    [Fact]
    public async Task Off_in_configuration_wins_over_a_released_switch()
    {
        var ks = Switch(enabledInConfig: false);
        await ks.SetAsync(stop: false, "manager@test");

        var state = await ks.GetAsync();

        Assert.True(state.Stopped);
        Assert.Contains("off in configuration", state.Reason);
    }
}
