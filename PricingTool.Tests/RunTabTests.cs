using Microsoft.EntityFrameworkCore;
using PricingTool.Agent.Storage;
using PricingTool.Web.Models;
using PricingTool.Web.Services;

namespace PricingTool.Tests;

/// <summary>The Run tab's current run, and the Chat tab's folding of the agents' repeated lines.</summary>
public class RunTabTests : IDisposable
{
    private readonly AgentDbContext _ai = new(new DbContextOptionsBuilder<AgentDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose() => _ai.Dispose();

    private static PublishBatch Batch(long id, string kind, string status, long? run, int layer = 4, bool rolledBack = false) => new()
    {
        Id = id, LayerId = layer, Db = "GjirafaEcommerce", StoreId = 2, Target = "Sandbox", ShopSchema = "shop_sandbox_GjirafaEcommerce",
        Kind = kind, Status = status, EngineRunId = run, StartedUtc = DateTime.UtcNow, RolledBackUtc = rolledBack ? DateTime.UtcNow : null,
    };

    [Fact]
    public async Task The_current_run_is_the_newest_regular_publish_of_a_run_that_went_through()
    {
        _ai.PublishBatches.AddRange(
            Batch(1, "Regular", "Published", 1105),
            Batch(2, "Regular", "Published", 1106),                         // the current run
            Batch(3, "Regular", "Failed", null),                            // the cost watcher's one-SKU publish
            Batch(4, "Expire", "Published", null),
            Batch(5, "Regular", "Published", 1107, rolledBack: true),       // rolled back: not on the shelf
            Batch(6, "Campaign", "Published", null),
            Batch(7, "Regular", "Published", 1200, layer: 1));              // another layer
        await _ai.SaveChangesAsync();

        var published = await PricingTool.Agent.Operations.RunReportWriter.Published(_ai.PublishBatches, 4).Select(b => b.Id).ToListAsync();

        Assert.Equal(new long[] { 2, 1 }, published);
    }

    [Fact]
    public void A_sweep_of_holds_folds_into_one_line_even_with_other_lines_between()
    {
        var t = new DateTime(2026, 10, 31, 7, 18, 0, DateTimeKind.Utc);
        FeedItem Hold(int minute, string sku) => new(t.AddMinutes(-minute), "CostWatcher", 1, "held", $"{sku}: reason", null, "cw|hold|Held|team-discount");
        var newestFirst = new[]
        {
            Hold(0, "A"),
            new FeedItem(t.AddMinutes(-1), "Publisher", 1, "Expire batch #73", null, null),
            Hold(2, "B"),
            Hold(3, "C"),
            Hold(45, "D"),                                                  // more than 30 minutes after the sweep's newest: a new line
        };

        var lines = AgentActivity.Group(newestFirst).ToList();

        Assert.Equal(3, lines.Count);
        Assert.Equal(3, lines[0].Count);
        Assert.Equal("Held 3 SKUs for a person after a cost rise: a cheaper team deal sells under the new floor", lines[0].What);
        Assert.Contains("C: reason", lines[0].Why);
        Assert.Equal("Expire batch #73", lines[1].What);
        Assert.Equal((1, "D: reason"), (lines[2].Count, lines[2].Why));
    }
}
