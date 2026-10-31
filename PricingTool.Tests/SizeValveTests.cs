using PricingTool.Agent;
using PricingTool.Agent.Operations;

namespace PricingTool.Tests;

/// <summary>The cycle's size check acts at 8.5 GB (a trim with 3-day windows) and stops the sandbox steps at 9.5 GB.</summary>
public class SizeValveTests
{
    private static readonly CycleOptions C = new();

    /// <summary>A database of <paramref name="before"/> GB that the trim brings to <paramref name="after"/>.</summary>
    private sealed class Db(decimal before, decimal after)
    {
        public int Trims;
        public int? Days;
        public SizeValve.Probe Probe => new(_ => Task.FromResult(Trims == 0 ? before : after), (days, _) =>
        {
            Trims++;
            Days = days;
            return Task.FromResult("12 proposals, 3 publish exceptions, 5 mirrored discounts");
        });
    }

    [Theory]
    [InlineData(7.10, "Succeeded")]
    [InlineData(8.20, "Warning")]
    public async Task Under_8_5_GB_it_only_measures_and_warns(double gb, string status)
    {
        var db = new Db((decimal)gb, 0m);
        var (s, detail) = await SizeValve.CheckAsync(C, db.Probe);
        Assert.Equal((status, 0), (s, db.Trims));
        Assert.DoesNotContain("ran at once", detail);
    }

    [Fact]
    public async Task At_8_5_GB_the_retention_runs_at_once_with_3_day_windows_and_says_what_it_freed()
    {
        var db = new Db(8.62m, 8.05m);
        var (s, detail) = await SizeValve.CheckAsync(C, db.Probe);
        Assert.Equal((StepStatus.Warning, 1, 3), (s, db.Trims, db.Days!.Value));
        Assert.Contains("ran at once with 3-day windows", detail);
        Assert.Contains("freed 0.57 GB, now 8.05 GB", detail);
        Assert.DoesNotContain("wait for the day", detail);
        Assert.Null(SizeValve.SandboxPause(C, 8.05m));
    }

    [Fact]
    public async Task Still_at_9_5_GB_after_the_trim_the_sandbox_steps_wait_for_the_day()
    {
        var db = new Db(9.80m, 9.60m);
        var (_, detail) = await SizeValve.CheckAsync(C, db.Probe);
        Assert.Contains("still at or over 9.5 GB: the sandbox mirror and publish wait for the day", detail);
        Assert.Contains("push export", SizeValve.SandboxPause(C, 9.60m));
        Assert.Null(SizeValve.SandboxPause(C, 9.49m));
    }
}
