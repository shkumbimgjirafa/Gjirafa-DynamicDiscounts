using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using PricingTool.Agent;
using PricingTool.Agent.Operations;
using PricingTool.Agent.Storage;
using PricingTool.Engine;

namespace PricingTool.Tests;

/// <summary>The engine host's schedule (prompt 05) and the one Agent configuration shared by the three apps.</summary>
public class HackathonHostTests
{
    private static readonly CycleOptions Options = new() { DailyStartLocal = "07:00", RetryFailedAfterMinutes = 60 };
    private static readonly DateOnly Mon = new(2026, 11, 2);
    private static DateTimeOffset At(DateOnly day, int h, int m = 0) => new(day.ToDateTime(new TimeOnly(h, m)), TimeSpan.FromHours(1));

    [Fact]
    public void The_host_runs_at_start_then_on_a_new_day_from_the_start_time_and_retries_failures_after_the_interval()
    {
        Assert.True(DailyCycleWorker.IsDue(null, false, DateTimeOffset.MinValue, At(Mon, 2), Options));            // started at 02:00: runs now
        Assert.False(DailyCycleWorker.IsDue(Mon, false, At(Mon, 2), At(Mon, 23, 45), Options));                    // same day, nothing failed
        Assert.False(DailyCycleWorker.IsDue(Mon, false, At(Mon, 2), At(Mon.AddDays(1), 6, 45), Options));         // next day, before 07:00
        Assert.True(DailyCycleWorker.IsDue(Mon, false, At(Mon, 2), At(Mon.AddDays(1), 7), Options));              // next day, 07:00
        Assert.False(DailyCycleWorker.IsDue(Mon, true, At(Mon, 7), At(Mon, 7, 45), Options));                      // failure, 45 min later
        Assert.True(DailyCycleWorker.IsDue(Mon, true, At(Mon, 7), At(Mon, 8), Options));                          // failure, 60 min later
    }

    [Fact]
    public void A_failure_is_retryable_only_while_it_is_the_latest_attempt_and_attempts_are_left()
    {
        DailyCycleStep S(long id, string step, string status) => new() { Id = id, Step = step, Status = status };
        Assert.True(DailyCycle.HasRetryableFailure(new[] { S(1, "refresh-hk", StepStatus.Failed), S(2, "engine-L4", StepStatus.Succeeded) }));
        Assert.False(DailyCycle.HasRetryableFailure(new[] { S(1, "refresh-hk", StepStatus.Failed), S(2, "refresh-hk", StepStatus.Succeeded) }));
        Assert.False(DailyCycle.HasRetryableFailure(Enumerable.Range(1, DailyCycle.MaxAttemptsPerDay).Select(i => S(i, "alpha-L4", StepStatus.Failed))));
    }

    [Fact]
    public void Test_runs_keep_their_records_in_the_test_database()
    {
        IConfiguration Config(string? testDatabase) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PricingToolDb"] = @"Server=(localdb)\mssqllocaldb;Database=PricingToolHackathon;Trusted_Connection=True",
            ["Agent:TestDatabase"] = testDatabase,
        }).Build();
        string Catalog(string cs) => new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cs).InitialCatalog;

        Assert.Equal("PricingToolHackathonTest", Catalog(AgentServiceCollectionExtensions.TestConnectionString(Config(null))));
        Assert.Equal("DemoDb", Catalog(AgentServiceCollectionExtensions.TestConnectionString(Config("DemoDb"))));
    }

    [Fact]
    public void The_engine_web_and_tools_use_the_same_agent_limits_and_models()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "PricingTool.sln"))) root = Path.GetDirectoryName(root)!;
        JsonObject Agent(string project, string file)
        {
            var section = JsonNode.Parse(File.ReadAllText(Path.Combine(root, project, file)))!["Agent"]!.AsObject().DeepClone().AsObject();
            section.Remove("_comment");
            section.Remove("ToolsExecutable");     // the tools app is its own MCP server
            return section;
        }
        var web = Agent("PricingTool.Web", "appsettings.Hackathon.json");
        Assert.True(JsonNode.DeepEquals(web, Agent("PricingTool.Engine", "appsettings.Hackathon.json")), "Engine's Agent section differs from Web's");
        Assert.True(JsonNode.DeepEquals(web, Agent("PricingTool.AgentTools", "appsettings.json")), "AgentTools' Agent section differs from Web's");
    }
}
