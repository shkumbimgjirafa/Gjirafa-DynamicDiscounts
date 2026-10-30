using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PricingTool.Agent;
using PricingTool.Agent.Storage;
using PricingTool.Agent.Tools;

namespace PricingTool.Tests;

/// <summary>get_past_decisions: the agent's own earlier changes and holds, from real passes only.</summary>
public class AgentDataToolsTests
{
    [Fact]
    public async Task Past_decisions_list_changes_and_holds_of_real_passes_newest_first_and_leave_out_keeps_and_tests()
    {
        await using var db = new AgentDbContext(new DbContextOptionsBuilder<AgentDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var real = new AgentPass { LayerId = 4, Source = "Alpha", Mode = "Autonomous", Status = "Succeeded", StartedUtc = new DateTime(2026, 10, 30) };
        var test = new AgentPass { LayerId = 4, Source = "Alpha", Mode = "Autonomous", Status = "Succeeded", StartedUtc = new DateTime(2026, 10, 30), IsTest = true };
        db.AgentPasses.AddRange(real, test);
        await db.SaveChangesAsync();
        AgentDecision D(AgentPass p, string cat, string action, string status, int minute) => new()
        {
            PassId = p.Id, LayerId = 4, BandId = 26, Category = cat, Source = "Alpha", Action = action, Status = status,
            CurrentFloorPct = 13, ProposedFloorPct = action == "raise" ? 14 : 13, CurrentTargetPct = 23, ProposedTargetPct = 23,
            Reason = cat, EvidenceRefs = "{}", CreatedUtc = new DateTime(2026, 10, 30, 18, minute, 0),
        };
        db.AgentDecisions.AddRange(
            D(real, "Aksesorë", "raise", "Applied", 1),
            D(real, "SMART", "raise", "Held", 2),
            D(real, "Gaming", "keep", "Applied", 3),
            D(test, "Celular", "raise", "Pending", 4));
        await db.SaveChangesAsync();
        var tools = new AgentDataTools(new PricingDataReader(new AgentDataSource("Server=unused")), Options.Create(new AgentOptions()),
            new AgentToolLog(db), db);

        var result = JsonSerializer.SerializeToElement(await tools.GetPastDecisionsAsync(new AgentToolContext(AgentRole.Alpha, "t"), 4, null, null),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var cats = result.GetProperty("decisions").EnumerateArray().Select(d => d.GetProperty("category").GetString()).ToList();
        Assert.Equal(new[] { "SMART", "Aksesorë" }, cats);                    // newest first; no keep, no test pass
        Assert.Equal("13 -> 14", result.GetProperty("decisions")[1].GetProperty("floor").GetString());
        Assert.Equal(2, (await db.AgentQueryLog.SingleAsync()).RowCount);
    }
}
