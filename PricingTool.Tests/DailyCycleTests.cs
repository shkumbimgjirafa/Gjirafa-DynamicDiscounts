using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PricingTool.Agent;
using PricingTool.Agent.Operations;
using PricingTool.Agent.Storage;

namespace PricingTool.Tests;

/// <summary>The daily cycle's bookkeeping: one row per step, a failure doesn't stop the rest, a second start the
/// same day resumes only what isn't done, and a failing step is retried at most three times a day.</summary>
public class DailyCycleTests
{
    private static readonly DateOnly Day = new(2026, 11, 2);

    [Fact]
    public async Task Steps_are_logged_a_failure_does_not_stop_the_rest_and_a_second_start_resumes()
    {
        await using var db = new AgentDbContext(new DbContextOptionsBuilder<AgentDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var calls = new Dictionary<string, int>();
        var failRefresh = true;
        DailyCycle.Step S(string name, Func<(string, string)> body) => new(name, _ =>
        {
            calls[name] = calls.GetValueOrDefault(name) + 1;
            return Task.FromResult(body());
        });
        var cycle = new DailyCycle(new ServiceCollection().BuildServiceProvider(), db, Options.Create(new AgentOptions()), NullLogger<DailyCycle>.Instance)
        {
            StepSource = _ => new[]
            {
                S("size-check", () => (StepStatus.Succeeded, "2.9 GB")),
                S("refresh-hk", () => failRefresh ? throw new InvalidOperationException("replica unreachable (test)") : (StepStatus.Succeeded, "ok")),
                S("engine-L4", () => (StepStatus.Succeeded, "run #1")),
                S("cost-watcher", () => (StepStatus.NotBuilt, "not built yet")),
            },
        };

        var first = await cycle.RunAsync(Day);
        Assert.Equal(new[] { "Succeeded", "Failed", "Succeeded", "NotBuilt" }, first.Select(s => s.Status));
        Assert.Equal("replica unreachable (test)", first.Single(s => s.Step == "refresh-hk").Error);

        failRefresh = false;
        await cycle.RunAsync(Day);                                                        // resumes: only the failed step runs
        Assert.Equal((1, 2, 1), (calls["size-check"], calls["refresh-hk"], calls["engine-L4"]));

        await cycle.RunAsync(Day);                                                        // all done: nothing runs
        Assert.Equal(2, calls["refresh-hk"]);
    }

    [Fact]
    public async Task A_step_that_keeps_failing_is_tried_three_times_a_day_at_most()
    {
        await using var db = new AgentDbContext(new DbContextOptionsBuilder<AgentDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var calls = 0;
        var cycle = new DailyCycle(new ServiceCollection().BuildServiceProvider(), db, Options.Create(new AgentOptions()), NullLogger<DailyCycle>.Instance)
        {
            StepSource = _ => new[] { new DailyCycle.Step("alpha-L4", _ => { calls++; throw new InvalidOperationException("usage limit (test)"); }) },
        };

        for (var i = 0; i < 5; i++) await cycle.RunAsync(Day);

        Assert.Equal(DailyCycle.MaxAttemptsPerDay, calls);
        await cycle.RunAsync(Day.AddDays(1));                                             // a new day tries again
        Assert.Equal(DailyCycle.MaxAttemptsPerDay + 1, calls);
    }
}
