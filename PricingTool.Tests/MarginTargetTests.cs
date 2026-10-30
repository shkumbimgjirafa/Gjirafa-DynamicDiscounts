using PricingTool.Core.Domain;
using PricingTool.Core.Options;
using PricingTool.Core.Services;
using Xunit;

namespace PricingTool.Tests;

/// <summary>
/// Unit tests for the per-band margin-target guardrail allocation (<see cref="MarginTargetService"/>).
/// Pure arithmetic over candidate records — no DB, no pipeline.
/// </summary>
public class MarginTargetTests
{
    private static readonly PricingEngineOptions Opts = new();   // MarginTargetTolerancePct = 0.5
    private static readonly MarginTargetService Svc = new();

    private static PriceBandConfig Band(int id, decimal? target) =>
        new() { BandId = id, MinPrice = 0m, MaxPrice = 1000m, TargetMarginPct = target };

    private static MarginCandidate Cand(
        string sku, bool locked, decimal price, decimal cost, decimal floor, decimal anchor, int bandId = 1) =>
        new(sku, bandId, locked, price, cost, floor, anchor);

    [Fact]
    public void OverTarget_TrimsFreeTowardGoal_LeavesLockedUntouched()
    {
        var bands = new[] { Band(1, 20m) };
        var cands = new[]
        {
            Cand("L1", locked: true,  price: 100m, cost: 80m, floor: 85m, anchor: 200m), // 20% — fixed
            Cand("F1", locked: false, price: 100m, cost: 50m, floor: 60m, anchor: 120m), // 50%, trim headroom 40
            Cand("F2", locked: false, price: 100m, cost: 50m, floor: 60m, anchor: 120m),
        };

        var result = Svc.BuildOverrides(bands, cands, Opts);

        Assert.False(result.Overrides.ContainsKey("L1"));            // locked conclusion never moved
        Assert.True(result.Overrides["F1"].IsTrim);
        Assert.Equal(62.5m, result.Overrides["F1"].AdjustedPrice);   // 100 − 75·(40/80)
        Assert.Equal(62.5m, result.Overrides["F2"].AdjustedPrice);

        var band = Assert.Single(result.Bands);
        Assert.False(band.Unreachable);
        Assert.Equal(2, band.MovedCount);

        // The band's blended margin lands exactly on target once the free SKUs are trimmed.
        Assert.Equal(20m, BlendedAfter(cands, result));
    }

    [Fact]
    public void UnderTarget_RaisesFreeToTarget_ReachesGoal_WithoutOvershoot()
    {
        var bands = new[] { Band(1, 20m) };
        var cands = new[]
        {
            Cand("L1", locked: true,  price: 100m, cost: 80m, floor: 85m, anchor: 200m), // 20% — at target, fixed
            Cand("F1", locked: false, price: 100m, cost: 90m, floor: 95m, anchor: 130m), // 10%, target price 112.5
            Cand("F2", locked: false, price: 100m, cost: 90m, floor: 95m, anchor: 130m),
        };

        var result = Svc.BuildOverrides(bands, cands, Opts);

        Assert.False(result.Overrides.ContainsKey("L1"));
        Assert.False(result.Overrides["F1"].IsTrim);                 // lifted toward, not past, the goal
        Assert.Equal(112.5m, result.Overrides["F1"].AdjustedPrice);  // its exact 20%-margin price, not the 130 anchor
        Assert.Equal(112.5m, result.Overrides["F2"].AdjustedPrice);
        Assert.False(Assert.Single(result.Bands).Unreachable);
        Assert.Equal(20m, BlendedAfter(cands, result));              // band lands on target
    }

    [Fact]
    public void OverTarget_LeavesBelowTargetFreeUntouched_TrimsOnlyAboveTarget()
    {
        // The reported bug: a free SKU already UNDER the target must never be trimmed further from it.
        var bands = new[] { Band(1, 20m) };
        var cands = new[]
        {
            Cand("HI",  locked: false, price: 100m, cost: 40m, floor: 45m, anchor: 120m), // 60% — above target
            Cand("LOW", locked: false, price: 100m, cost: 85m, floor: 90m, anchor: 100m), // 15% — already below 20%
        };

        var result = Svc.BuildOverrides(bands, cands, Opts);

        Assert.False(result.Overrides.ContainsKey("LOW"));           // below target -> never trimmed
        Assert.True(result.Overrides["HI"].IsTrim);
        Assert.Equal(56.25m, result.Overrides["HI"].AdjustedPrice);  // trims only far enough to hit the band goal
        Assert.Equal(1, Assert.Single(result.Bands).MovedCount);
        Assert.Equal(20m, BlendedAfter(cands, result));
    }

    [Fact]
    public void OverTarget_TrimStopsAtTarget_NotFloor_WhenLockedForcesAGap()
    {
        // A locked, very high-margin SKU keeps the band over target. The one free SKU is trimmed only to
        // its own target margin, NOT dragged down to the floor to chase an otherwise-unreachable band goal.
        var bands = new[] { Band(1, 20m) };
        var cands = new[]
        {
            Cand("L1", locked: true,  price: 100m, cost: 20m, floor: 30m, anchor: 200m), // 80% — fixed
            Cand("F1", locked: false, price: 100m, cost: 60m, floor: 65m, anchor: 120m), // 40%, target price 75, floor 65
        };

        var result = Svc.BuildOverrides(bands, cands, Opts);

        Assert.True(result.Overrides["F1"].IsTrim);
        Assert.Equal(75m, result.Overrides["F1"].AdjustedPrice);     // the 20% target price, not the 65 floor
        Assert.True(Assert.Single(result.Bands).Unreachable);        // locked SKU keeps the band over target
    }

    [Fact]
    public void UnderTarget_LiftStopsAtTarget_NotAnchor_WhenLockedHoldsBandBelow()
    {
        // Symmetric to the trim case: a locked below-target SKU keeps the band under goal, but free SKUs are
        // only lifted to their own target margin, never overshot past it toward the anchor to compensate.
        var bands = new[] { Band(1, 20m) };
        var cands = new[]
        {
            Cand("L1", locked: true,  price: 100m, cost: 90m, floor: 95m, anchor: 200m), // 10% — fixed, drags band down
            Cand("F1", locked: false, price: 100m, cost: 90m, floor: 95m, anchor: 130m), // 10%, target price 112.5
            Cand("F2", locked: false, price: 100m, cost: 90m, floor: 95m, anchor: 130m),
        };

        var result = Svc.BuildOverrides(bands, cands, Opts);

        Assert.False(result.Overrides["F1"].IsTrim);
        Assert.Equal(112.5m, result.Overrides["F1"].AdjustedPrice);  // target price, not the 130 anchor
        Assert.True(Assert.Single(result.Bands).Unreachable);        // locked SKU holds the band under target
    }

    [Fact]
    public void WithinTolerance_IsNoOp()
    {
        var bands = new[] { Band(1, 20m) };
        var cands = new[]
        {
            Cand("L1", locked: true,  price: 100m, cost: 80m, floor: 85m, anchor: 200m), // 20%
            Cand("F1", locked: false, price: 100m, cost: 80m, floor: 85m, anchor: 120m), // 20%
        };

        var result = Svc.BuildOverrides(bands, cands, Opts);

        Assert.Empty(result.Overrides);
        var band = Assert.Single(result.Bands);
        Assert.Equal(0, band.MovedCount);
        Assert.False(band.Unreachable);
        Assert.False(band.NoFreeProducts);
    }

    [Fact]
    public void NoFreeProducts_FlagsUnreachable_MovesNothing()
    {
        var bands = new[] { Band(1, 20m) };
        var cands = new[]
        {
            Cand("L1", locked: true, price: 100m, cost: 60m, floor: 70m, anchor: 200m), // 40%, but locked
        };

        var result = Svc.BuildOverrides(bands, cands, Opts);

        Assert.Empty(result.Overrides);
        var band = Assert.Single(result.Bands);
        Assert.True(band.NoFreeProducts);
        Assert.True(band.Unreachable);
    }

    [Fact]
    public void InsufficientHeadroom_MovesToFloor_FlagsUnreachable_NeverBreachesFloor()
    {
        var bands = new[] { Band(1, 20m) };
        var cands = new[]
        {
            Cand("L1", locked: true,  price: 100m, cost: 60m, floor: 70m, anchor: 200m), // 40% fixed
            Cand("F1", locked: false, price: 100m, cost: 60m, floor: 95m, anchor: 120m), // only 5 of trim headroom
        };

        var result = Svc.BuildOverrides(bands, cands, Opts);

        Assert.True(result.Overrides["F1"].IsTrim);
        Assert.Equal(95m, result.Overrides["F1"].AdjustedPrice);     // pinned at the floor, not below
        Assert.True(Assert.Single(result.Bands).Unreachable);
    }

    [Fact]
    public void BandWithoutTarget_IsIgnored()
    {
        var bands = new[] { Band(1, 20m), Band(2, null) };
        var cands = new[]
        {
            Cand("B1-F", locked: false, price: 100m, cost: 50m, floor: 60m, anchor: 120m, bandId: 1),
            Cand("B2-F", locked: false, price: 100m, cost: 50m, floor: 60m, anchor: 120m, bandId: 2), // no goal
        };

        var result = Svc.BuildOverrides(bands, cands, Opts);

        Assert.True(result.Overrides.ContainsKey("B1-F"));
        Assert.False(result.Overrides.ContainsKey("B2-F"));          // its band has no target → untouched
        Assert.DoesNotContain(result.Bands, b => b.BandId == 2);
    }

    [Fact]
    public void IsDeterministic()
    {
        var bands = new[] { Band(1, 22m) };
        var cands = new[]
        {
            Cand("L1", locked: true,  price: 120m, cost: 60m, floor: 70m, anchor: 200m),
            Cand("F1", locked: false, price: 100m, cost: 55m, floor: 62m, anchor: 140m),
            Cand("F2", locked: false, price: 90m,  cost: 40m, floor: 50m, anchor: 130m),
        };

        var a = Svc.BuildOverrides(bands, cands, Opts).Overrides;
        var b = Svc.BuildOverrides(bands, cands, Opts).Overrides;

        Assert.Equal(a.Count, b.Count);
        foreach (var (sku, ov) in a)
            Assert.Equal(ov.AdjustedPrice, b[sku].AdjustedPrice);
    }

    [Fact]
    public void WithinOnePointOfGoal_IsNoOp()
    {
        var bands = new[] { Band(1, 20m) };
        // Single free SKU at 20.8% margin — 0.8pp off, inside the default ±1pp band → left alone.
        var cands = new[] { Cand("F1", locked: false, price: 100m, cost: 79.2m, floor: 50m, anchor: 200m) };

        var result = Svc.BuildOverrides(bands, cands, Opts);

        Assert.Empty(result.Overrides);
        Assert.Equal(0, Assert.Single(result.Bands).MovedCount);
    }

    [Fact]
    public void MoreThanOnePointFromGoal_Acts()
    {
        var bands = new[] { Band(1, 20m) };
        // 21.5% margin — 1.5pp off, outside the ±1pp band → trimmed toward goal.
        var cands = new[] { Cand("F1", locked: false, price: 100m, cost: 78.5m, floor: 50m, anchor: 200m) };

        var result = Svc.BuildOverrides(bands, cands, Opts);

        Assert.True(result.Overrides["F1"].IsTrim);
    }

    /// <summary>Blended margin % of the band after applying the overrides to their candidates.</summary>
    private static decimal BlendedAfter(IReadOnlyList<MarginCandidate> cands, MarginTargetResult result)
    {
        decimal sumPrice = 0m, sumCost = 0m;
        foreach (var c in cands)
        {
            var price = result.Overrides.TryGetValue(c.Sku, out var ov) ? ov.AdjustedPrice : c.Price;
            sumPrice += price;
            sumCost += c.Cost;
        }
        return (sumPrice - sumCost) / sumPrice * 100m;
    }
}
