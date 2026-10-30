using Microsoft.EntityFrameworkCore;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Checker;
using PricingTool.Agent.Operations;
using PricingTool.Agent.Storage;
using PricingTool.Data;
using PricingTool.Data.Entities;

namespace PricingTool.Tests;

/// <summary>What the exceptions inbox counts as waiting for a person, and what it only lists.</summary>
public class ExceptionsInboxTests : IDisposable
{
    private readonly AgentDbContext _ai = new(new DbContextOptionsBuilder<AgentDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly PricingToolDbContext _tool = new(new DbContextOptionsBuilder<PricingToolDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly DateTime _recent = DateTime.UtcNow.AddHours(-1);

    public void Dispose()
    {
        _ai.Dispose();
        _tool.Dispose();
    }

    private AgentDecision Decision(long passId, string status, string category) => new()
    {
        PassId = passId, LayerId = 4, BandId = 26, Category = category, Source = "Alpha", Action = "raise", Status = status,
        CurrentFloorPct = 13m, ProposedFloorPct = 18m, Reason = "test", EvidenceRefs = "[]", CreatedUtc = _recent,
    };

    private AgentRunCheck Check(long runId, string verdict, bool isTest = false) => new()
    {
        RunId = runId, LayerId = 4, StartedUtc = _recent, Status = "Succeeded", Verdict = verdict, IsTest = isTest,
    };

    private PublishBatch Batch(string status) => new()
    {
        LayerId = 4, Db = "GjirafaEcommerce", StoreId = 2, Target = "Sandbox", ShopSchema = "shop_sandbox_GjirafaEcommerce",
        Kind = "Regular", Status = status, StartedUtc = _recent, Requested = 10,
    };

    [Fact]
    public async Task Counts_only_what_needs_a_person_and_lists_the_rest()
    {
        _tool.Layers.Add(new Layer { Id = 4, Brand = "Gjirafa50", CountryCode = "KS", DisplayName = "Gjirafa50 KS", OperationalDatabase = "GjirafaEcommerce", StoreId = 2, Currency = "EUR", IsActive = true });
        _tool.PriceBands.Add(new PriceBand { Id = 26, LayerId = 4, Name = "10-50", MinPrice = 10m, MaxPrice = 50m, MarginFloorPct = 13m });
        _tool.SaveChanges();

        _ai.AgentPasses.AddRange(
            new AgentPass { Id = 1, LayerId = 4, Source = "Alpha", Mode = "Autonomous", StartedUtc = _recent, Status = "Succeeded" },
            new AgentPass { Id = 2, LayerId = 4, Source = "Alpha", Mode = "Autonomous", StartedUtc = _recent, Status = "Succeeded", IsTest = true });
        _ai.AgentDecisions.AddRange(
            Decision(1, DecisionStatus.Held, "Held"), Decision(1, DecisionStatus.Pending, "Pending"),
            Decision(1, DecisionStatus.Applied, "Applied"), Decision(2, DecisionStatus.Held, "Held in a test pass"));

        _ai.AgentRunChecks.AddRange(
            Check(1100, Severity.Block), Check(1100, Severity.Pass),        // re-checked and passed: no longer blocked
            Check(1101, Severity.Block),                                    // blocked
            Check(1102, Severity.Warn),                                     // held prices: listed only
            Check(1103, Severity.Block, isTest: true));                     // test check: never shown

        var stopped = Batch("Stopped");
        var rolledBack = Batch("RolledBack");
        rolledBack.RoundTripChecked = 10;
        rolledBack.RoundTripMismatches = 3;                                 // rolled back by the round-trip check
        var canary = Batch("RolledBack");
        canary.RoundTripChecked = 9;                                        // a clean canary a person rolled back: listed only
        var withExceptions = Batch("Published");
        _ai.PublishBatches.AddRange(stopped, rolledBack, canary, withExceptions, Batch("Published"));
        _ai.SaveChanges();
        _ai.PublishExceptions.AddRange(
            new PublishException { BatchId = withExceptions.Id, Sku = "A", Kind = "cheaper-deal", Detail = "", CreatedUtc = _recent },
            new PublishException { BatchId = withExceptions.Id, Sku = "B", Kind = "cheaper-deal", Detail = "", CreatedUtc = _recent },
            new PublishException { BatchId = canary.Id, Sku = "C", Kind = "cheaper-deal", Detail = "", CreatedUtc = _recent });

        var today = DateOnly.FromDateTime(DateTime.Now);
        _ai.DailyCycleSteps.AddRange(
            new DailyCycleStep { CycleDate = today, Step = "engine-L4", StartedUtc = _recent, Status = "Failed" },
            new DailyCycleStep { CycleDate = today, Step = "engine-L4", StartedUtc = _recent, Status = "Succeeded" },   // retried fine
            new DailyCycleStep { CycleDate = today, Step = "backup", StartedUtc = _recent, Status = "Failed" });
        _ai.SaveChanges();

        var inbox = await new ExceptionsInbox(_ai, _tool).LoadAsync();

        Assert.Equal(new[] { "Held", "Pending" }, inbox.Decisions.Select(d => d.Decision.Category).OrderBy(c => c));
        Assert.Equal("10-50", inbox.Decisions[0].BandName);
        Assert.Equal(1101, Assert.Single(inbox.BlockedRuns).RunId);
        Assert.Equal(1102, Assert.Single(inbox.RunsWithHeldPrices).RunId);
        Assert.Equal(4, inbox.Publishes.Count);
        Assert.False(inbox.Publishes.Single(p => p.Batch.Id == canary.Id).NeedsAPerson);
        Assert.Equal(("cheaper-deal", 2), Assert.Single(inbox.Publishes.Single(p => p.Batch.Id == withExceptions.Id).Exceptions));
        Assert.Equal("backup", Assert.Single(inbox.FailedSteps).Step);
        Assert.Equal(2 + 1 + 2 + 1, inbox.WaitingCount);                    // decisions + blocked run + stopped/rolled back + failed step
        Assert.Equal("Gjirafa50 KS", inbox.LayerName(4));
    }
}
