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

    [Fact]
    public async Task The_same_left_out_SKUs_every_day_count_once_with_the_days_they_were_seen()
    {
        var now = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
        DateTime d2 = now.AddDays(-2), d1 = now.AddDays(-1).AddHours(-2), d0 = now.AddHours(-1);
        PublishBatch At(DateTime started) { var b = Batch("Published"); b.StartedUtc = started; return b; }
        PublishException Ex(PublishBatch b, string sku, DateTime at, string kind = "cheaper-deal") =>
            new() { BatchId = b.Id, Sku = sku, Kind = kind, Detail = $"{kind} on {sku} at {at:MM-dd}", CreatedUtc = at };
        var (b2, b1, b0, old) = (At(d2), At(d1), At(d0), At(now.AddDays(-9)));
        _ai.PublishBatches.AddRange(b2, b1, b0, old);
        _ai.SaveChanges();
        _ai.PublishExceptions.AddRange(
            Ex(b2, "SKU-A", d2), Ex(b1, "SKU-A", d1), Ex(b0, "SKU-A", d0),            // held by a team deal every day
            Ex(b1, "SKU-B", d1), Ex(b0, "SKU-B", d0),
            Ex(b0, "SKU-C", d0),                                                        // new in the last day
            Ex(b0, "SKU-D", d0, "raised-under-team-deal"),
            Ex(old, "SKU-OLD", now.AddDays(-9)));                                       // outside the window
        _ai.SaveChanges();

        var groups = await ExceptionsInbox.PublishSkusAsync(_ai, now.AddDays(-7), now, 2);

        var deals = groups.Single(g => g.Kind == "cheaper-deal");
        Assert.Equal((4, 3, 1, 3), (deals.LayerId, deals.Skus, deals.NewInLastDay, deals.MostDays));
        Assert.Equal(new[] { ("SKU-A", 3), ("SKU-B", 2) }, deals.Latest.Select(l => (l.Sku, l.Days)));
        Assert.Equal($"cheaper-deal on SKU-A at {d0:MM-dd}", deals.Latest[0].Detail);              // the latest reason
        Assert.Equal(1, groups.Single(g => g.Kind == "raised-under-team-deal").Skus);
        Assert.DoesNotContain(groups.SelectMany(g => g.Latest), l => l.Sku == "SKU-OLD");
    }
}
