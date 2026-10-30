using PricingTool.Core.Domain;
using PricingTool.Core.Services;

namespace PricingTool.Tests;

public class RoundingTests
{
    private readonly RoundingService _rounding = new();
    private static readonly PriceBounds Wide = new(0.01m, 100000m);
    // Whole-currency markets (MKD/ALL) run at ~×100 the EUR nominal, so their tests need a higher cap.
    private static readonly PriceBounds WideHundreds = new(0.01m, 100_000_000m);

    [Theory]
    [InlineData(10.50, 10.99)] // .99 candidates 9.99 / 10.99; 10.99 is closer (0.49 vs 0.51)
    [InlineData(10.40, 9.99)]
    [InlineData(10.99, 10.99)]
    public void EndsIn99_PicksClosestCandidate(decimal input, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.EndsIn99, Wide);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Theory]
    [InlineData(1.21, 1.19)]  // under €5: 10-cent .x9 grid → 1.19 / 1.29, closer 1.19
    [InlineData(2.60, 2.59)]
    [InlineData(4.30, 4.29)]
    [InlineData(0.74, 0.69)]  // tie 0.69 / 0.79 → lower wins
    public void EndsIn99_LowPrice_UsesDimeCharmGrid(decimal input, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.EndsIn99, Wide);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Fact]
    public void EndsIn99_AtOrAboveThreshold_KeepsEuroGrid()
    {
        // 7.30 ≥ €5 → euro .99 grid: 6.99 / 7.99, closer 6.99 (not the dime-grid 7.29).
        var result = _rounding.Apply(7.30m, RoundingConvention.EndsIn99, Wide);
        Assert.Equal(6.99m, result.Price);
    }

    [Fact]
    public void EndsIn99_LowPriceThreshold_IsConfigurable()
    {
        // Threshold 0 disables the dime grid → 1.21 falls back to the coarse euro grid (0.99).
        var result = _rounding.Apply(1.21m, RoundingConvention.EndsIn99, Wide, lowPriceThreshold: 0m);
        Assert.Equal(0.99m, result.Price);
    }

    [Fact]
    public void EndsIn99_RoundsUp_WhenDownwardCandidateBreachesLowerBound()
    {
        // 9.99 would violate the guardrail lower bound 10.20 → must go up to 10.99.
        var result = _rounding.Apply(10.40m, RoundingConvention.EndsIn99, new PriceBounds(10.20m, 1000m));
        Assert.Equal(10.99m, result.Price);
    }

    [Fact]
    public void EndsIn99_RoundsDown_WhenUpwardCandidateBreachesUpperBound()
    {
        // 10.99 exceeds the OldPrice cap of 10.50 → must go down to 9.99 (allowed by lower bound).
        var result = _rounding.Apply(10.50m, RoundingConvention.EndsIn99, new PriceBounds(5m, 10.50m));
        Assert.Equal(9.99m, result.Price);
    }

    [Fact]
    public void Rounding_SkippedEntirely_WhenNoCandidateFitsBounds()
    {
        // Bounds too narrow for any .99 value → keep the clamped price, flag the skip.
        var result = _rounding.Apply(10.40m, RoundingConvention.EndsIn99, new PriceBounds(10.20m, 10.60m));
        Assert.False(result.RoundingApplied);
        Assert.True(result.SkippedOutOfBounds);
        Assert.Equal(10.40m, result.Price);
    }

    [Theory]
    [InlineData(1000.00, 995)] // round hundred steps down to 995-style
    [InlineData(1002.00, 995)] // floor-to-5 lands on 1000 → adjusted to 995
    [InlineData(1097.00, 1095)]
    public void Charm995_RoundDown_AvoidsBareRoundHundreds(decimal input, decimal expected)
    {
        Assert.Equal(expected, RoundingService.RoundDown(input, RoundingConvention.Charm995));
    }

    [Theory]
    [InlineData(997.00, 1000)]  // 995 is below the price, so up must be 1000
    [InlineData(1001.00, 1005)]
    public void Charm995_RoundUp_NeverGoesBelowInput(decimal input, decimal expected)
    {
        Assert.Equal(expected, RoundingService.RoundUp(input, RoundingConvention.Charm995));
    }

    [Fact]
    public void Charm995_1049_RoundsTo1045Or1050_WithinBounds()
    {
        var result = _rounding.Apply(1049.37m, RoundingConvention.Charm995, Wide);
        Assert.Equal(1050m, result.Price); // 1050 (dist .63) beats 1045 (dist 4.37); not a round hundred
    }

    [Fact]
    public void WholeEuro_RoundsToNearestEuroWithinBounds()
    {
        var result = _rounding.Apply(120.49m, RoundingConvention.WholeEuro, Wide);
        Assert.Equal(120m, result.Price);
    }

    [Theory]
    [InlineData(6149, 6099)]  // largest …99 at or below
    [InlineData(6199, 6199)]
    [InlineData(99, 99)]
    public void EndsIn99Hundreds_RoundDown(decimal input, decimal expected)
        => Assert.Equal(expected, RoundingService.RoundDown(input, RoundingConvention.EndsIn99Hundreds));

    [Theory]
    [InlineData(9990, 9999)]  // smallest …99 at or above
    [InlineData(9899, 9899)]
    [InlineData(6100, 6199)]
    public void EndsIn99Hundreds_RoundUp(decimal input, decimal expected)
        => Assert.Equal(expected, RoundingService.RoundUp(input, RoundingConvention.EndsIn99Hundreds));

    [Theory]
    [InlineData(9990, 9999)]  // up (dist 9) beats down 9899 (dist 91) — MKD/ALL charm pricing
    [InlineData(6149, 6099)]  // 6099 vs 6199 tie → lower wins
    public void EndsIn99Hundreds_Apply_PicksClosest(decimal input, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.EndsIn99Hundreds, Wide);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Theory]
    [InlineData(47.31, 46.50)] // largest x.50 at or below
    [InlineData(47.50, 47.50)]
    [InlineData(149.99, 149.50)]
    public void EndsIn50_RoundDown(decimal input, decimal expected)
        => Assert.Equal(expected, RoundingService.RoundDown(input, RoundingConvention.EndsIn50));

    [Theory]
    [InlineData(47.31, 47.50)] // smallest x.50 at or above
    [InlineData(47.50, 47.50)]
    [InlineData(149.01, 149.50)]
    public void EndsIn50_RoundUp(decimal input, decimal expected)
        => Assert.Equal(expected, RoundingService.RoundUp(input, RoundingConvention.EndsIn50));

    [Theory]
    [InlineData(47.31, 47.50)] // down 46.50 (dist .81) vs up 47.50 (dist .19) → 47.50
    [InlineData(47.80, 47.50)] // down 47.50 (dist .30) vs up 48.50 (dist .70) → 47.50
    [InlineData(100.00, 99.50)] // tie 99.50 / 100.50 → lower wins; no "snap to 99" margin give-away
    public void EndsIn50_Apply_PicksClosest_Gj50Signature(decimal input, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.EndsIn50, Wide);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    // ---- Gj50Charm: Weber-scaled grid + round-up-biased selection ----

    [Theory]
    [InlineData(27.31, 26.50)]    // under €30: budget .55 → step 1  (…50 grid)
    [InlineData(47.31, 45.50)]    // €30–99: fixed x5.50 / x9.50 grid
    [InlineData(250.00, 249.50)]  // €100–999: fixed xx9.50 grid
    [InlineData(1233.23, 1229.50)]// €1000+: budget 24.7 → step 10 (…9.50)
    [InlineData(1640.00, 1624.50)]// budget 32.8 → step 25 (…24.50/…49.50/…74.50/…99.50)
    [InlineData(3450.00, 3449.50)]// budget 69   → step 50
    [InlineData(8000.00, 7999.50)]// budget 160  → step 100
    public void Gj50Charm_RoundDown_GridScalesWithMagnitude(decimal input, decimal expected)
        => Assert.Equal(expected, RoundingService.RoundDown(input, RoundingConvention.Gj50Charm));

    [Theory]
    [InlineData(27.31, 27.50)]
    [InlineData(47.31, 49.50)]
    [InlineData(99.60, 109.50)]   // past 99.50 the next point is 109.50 (xx9.50), never a 105.50
    [InlineData(250.00, 259.50)]
    [InlineData(1233.23, 1239.50)]
    [InlineData(1640.00, 1649.50)]
    [InlineData(3450.00, 3499.50)]
    [InlineData(8000.00, 8099.50)]
    public void Gj50Charm_RoundUp_GridScalesWithMagnitude(decimal input, decimal expected)
        => Assert.Equal(expected, RoundingService.RoundUp(input, RoundingConvention.Gj50Charm));

    [Theory]
    [InlineData(1233.23, 1239.50)] // worked example: both flank equally-unround 1230/1240 → up for margin
    [InlineData(414.00, 419.50)]   // €100–999: 409.50 / 419.50 both within 2% → up (nearest would be 409.50)
    [InlineData(1640.00, 1649.50)] // up side 1650 is rounder than 1625 → up
    [InlineData(3450.00, 3499.50)] // up side 3500 is rounder than 3450; +1.4% within the 2% budget → up
    public void Gj50Charm_Apply_PrefersUp_ToClawBackMargin(decimal input, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.Gj50Charm, Wide);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Theory]
    [InlineData(50.00, 49.50)]     // just under 50, not 50.50 just over it
    [InlineData(100.00, 99.50)]
    [InlineData(200.00, 199.50)]
    [InlineData(250.00, 249.50)]   // 250 (one zero) is rounder than 255 → down
    [InlineData(500.00, 499.50)]
    [InlineData(1000.00, 999.50)]
    [InlineData(5000.00, 4999.50)] // big-ticket round number → prestige-adjacent 4999.50, not 5099.50
    [InlineData(199.80, 199.50)]   // generalizes the old "no 1xx → 2xx jump" guard
    [InlineData(999.80, 999.50)]   // …and the 9xx → 10xx (€1000) jump
    [InlineData(249.80, 249.50)]   // down sits just under 250 (rounder than 251) → down, not 250.50
    public void Gj50Charm_Apply_StaysJustBelowTheRounderNumber(decimal input, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.Gj50Charm, Wide);
        Assert.Equal(expected, result.Price); // down charm point wins — never lands just above a round number
        Assert.True(result.RoundingApplied);
    }

    [Fact]
    public void Gj50Charm_Apply_FallsToDown_WhenUpExceedsAnchorCap()
    {
        // Up candidate 1239.50 exceeds the OldPrice cap 1235 → take the down charm point 1229.50.
        var result = _rounding.Apply(1233.23m, RoundingConvention.Gj50Charm, new PriceBounds(0.01m, 1235m));
        Assert.Equal(1229.50m, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Theory]
    [InlineData(70.50, 69.50)]   // sits exactly 0.50 over 70 → pulled to just below 70
    [InlineData(100.50, 99.50)]  // just over 100 → 99.50
    [InlineData(200.50, 199.50)] // just over 200 → 199.50
    [InlineData(22.50, 22.50)]   // control: not just-above a salient round → kept
    public void Gj50Charm_Apply_PullsJustAboveRoundDownToJustBelow(decimal input, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.Gj50Charm, Wide);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Theory]
    [InlineData(4.99, 4.50)]   // below budget → nearest .50 (down)
    [InlineData(7.20, 7.50)]   // below budget → nearest .50 (up)
    [InlineData(20.00, 19.50)] // both .50 points 2.5% away (> budget) → nearest, tie → down
    public void Gj50Charm_Apply_SnapsToNearestHalf_WhenBelowBudget(decimal input, decimal expected)
    {
        // Cheap items: a .50 step is a >2% move, so the round-up bias stands down — but the price still
        // always ends in .50 (snap to the nearest), never the raw optimum.
        var result = _rounding.Apply(input, RoundingConvention.Gj50Charm, Wide);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Fact]
    public void Gj50Charm_RelativePrecision_TunesGridGranularity()
    {
        // €1000+: default 2% budget at €1200 → step-10 grid (…9.50).
        Assert.Equal(1209.50m, RoundingService.RoundUp(1200m, RoundingConvention.Gj50Charm));
        // A tight 0.2% budget pins a finer step-1 grid (…50).
        Assert.Equal(1200.50m, RoundingService.RoundUp(1200m, RoundingConvention.Gj50Charm,
            lowPriceThreshold: 5m, relativePrecision: 0.002m));
        // €100–999 is a fixed xx9.50 grid — the budget doesn't change it.
        Assert.Equal(609.50m, RoundingService.RoundUp(600m, RoundingConvention.Gj50Charm,
            lowPriceThreshold: 5m, relativePrecision: 0.002m));
    }

    [Theory]
    [InlineData(30.50, 29.50)]  // small round-ten euro: the €1 drop is out of the Weber budget, but x0.50
    [InlineData(30.80, 29.50)]  // is banned, so it still crosses down to the …9.50 below the ten.
    [InlineData(40.50, 39.50)]
    [InlineData(100.50, 99.50)]
    public void Gj50Charm_Apply_RoundTenEuro_NeverLandsOnX0_50(decimal input, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.Gj50Charm, Wide);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Theory]
    [InlineData(30.50, 30.00, 45.00, 35.50)]  // 29.50 breaches the floor → the next €30–99 point, 35.50
    [InlineData(29.90, 29.60, 45.00, 35.50)]  // under-€30 price whose floor sits inside the ten → same 35.50
    [InlineData(30.50, 30.00, 33.00, 31.50)]  // cap blocks 35.50 too → next euro's .50, still not x0.50
    public void Gj50Charm_Apply_RoundTenEuro_FloorBlocksDrop_RisesPastTheTen(
        decimal input, decimal floor, decimal cap, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.Gj50Charm, new PriceBounds(floor, cap));
        Assert.Equal(expected, result.Price);
    }

    [Theory]
    [InlineData(30.20, 29.50)]   // 29.50 (0.70) vs 35.50 (5.30)
    [InlineData(32.60, 35.50)]   // 29.50 (3.10) vs 35.50 (2.90)
    [InlineData(32.50, 29.50)]   // tie → lower
    [InlineData(37.40, 35.50)]
    [InlineData(37.60, 39.50)]
    [InlineData(42.40, 39.50)]
    [InlineData(42.60, 45.50)]
    [InlineData(47.31, 45.50)]   // nearest, not up-biased: 45.50 (1.81) vs 49.50 (2.19)
    [InlineData(63.00, 65.50)]
    [InlineData(88.00, 89.50)]
    [InlineData(97.40, 95.50)]
    [InlineData(97.60, 99.50)]
    [InlineData(99.90, 99.50)]
    public void Gj50Charm_Apply_ThirtyToNinetyNine_NearestX5OrX9(decimal input, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.Gj50Charm, Wide);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Theory]
    [InlineData(100.00, 99.50)]
    [InlineData(104.40, 99.50)]   // neither within 2% → nearest
    [InlineData(104.60, 109.50)]
    [InlineData(123.76, 119.50)]
    [InlineData(155.00, 159.50)]
    [InlineData(404.00, 399.50)]  // both within 2%, but 399.50 sits just below 400 → down
    [InlineData(680.00, 689.50)]  // both within 2% → up (claw margin)
    [InlineData(995.00, 999.50)]
    public void Gj50Charm_Apply_HundredsRange_EndsInXX9_50(decimal input, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.Gj50Charm, Wide);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Theory]
    [InlineData(33.00, 32.00, 50.00, 35.50)]     // floor blocks 29.50 → 35.50
    [InlineData(38.00, 30.00, 38.50, 35.50)]     // cap blocks 39.50 → 35.50
    [InlineData(112.00, 111.00, 130.00, 119.50)] // floor blocks 109.50 → 119.50
    public void Gj50Charm_Apply_FixedEnding_RespectsBounds(decimal input, decimal floor, decimal cap, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.Gj50Charm, new PriceBounds(floor, cap));
        Assert.Equal(expected, result.Price);
    }

    [Theory]
    [InlineData(61.00, 60.20, 62.00, 61.50)]     // no x5.50/x9.50 in [60.20, 62] → Weber .50
    [InlineData(152.00, 151.00, 158.00, 152.50)] // no xx9.50 in [151, 158] → Weber .50
    public void Gj50Charm_Apply_FixedEnding_NarrowBand_FallsBackToHalf(decimal input, decimal floor, decimal cap, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.Gj50Charm, new PriceBounds(floor, cap));
        Assert.Equal(expected, result.Price);
        Assert.False(result.SkippedOutOfBounds);
    }

    [Fact]
    public void Gj50Charm_PinnedBounds_HeldWithoutSkipFlag()
    {
        // Dead-stock tunnel held at exactly CurrentPrice → bounds pinned to (30,30). The price is held;
        // it must NOT be tagged as a rounding anomaly (the hold is deliberate, not a skipped round).
        var result = _rounding.Apply(30.00m, RoundingConvention.Gj50Charm, new PriceBounds(30.00m, 30.00m));
        Assert.Equal(30.00m, result.Price);
        Assert.False(result.RoundingApplied);
        Assert.False(result.SkippedOutOfBounds);
    }

    [Fact]
    public void Gj50Charm_Sweep_IsMonotonic_AndAlwaysEndsInHalf()
    {
        // Run a fine sweep of real prices through the live service and assert the emergent properties:
        //  - monotonic: a dearer item never ends up priced below a cheaper one;
        //  - the result always ends in .50 (with wide bounds, no candidate is ever guardrail-blocked);
        //  - the result always stays within bounds.
        // Spans every grid step-transition (250, 500, 1250, 2500, 5000, 12500) and salient round numbers.
        const decimal precision = 0.02m;
        decimal previous = 0m;
        for (decimal price = 1.00m; price <= 13000m; price += 0.05m)
        {
            var result = _rounding.Apply(price, RoundingConvention.Gj50Charm, Wide, relativePrecision: precision).Price;

            Assert.True(result >= previous - 0.0001m, $"non-monotonic at {price}: {result} < prev {previous}");
            Assert.True(result >= Wide.Lower && result <= Wide.Upper, $"out of bounds at {price}: {result}");
            Assert.True(result - Math.Floor(result) == 0.50m, $"not a .50 ending at {price}: {result}");
            Assert.True(Math.Floor(result) < 10m || Math.Floor(result) % 10m != 0m,
                $"round-ten euro (x0.xx banned) at {price}: {result}");
            if (result >= 30m && result < 100m)
                Assert.True(result % 10m == 5.50m || result % 10m == 9.50m, $"€30–99 not x5.50/x9.50 at {price}: {result}");
            if (result >= 100m && result < 1000m)
                Assert.True(result % 10m == 9.50m, $"€100–999 not xx9.50 at {price}: {result}");

            previous = result;
        }
    }

    // ---- GjmCharm: round up to …99 (Weber-bounded); round-ten euros pull down to …9.99; 10c under €5 ----

    [Theory]
    [InlineData(45.40, 45.99)]     // round up to this euro's …99 (Weber lets it jump 0.59)
    [InlineData(45.95, 45.99)]
    [InlineData(47.31, 47.99)]
    [InlineData(51.30, 51.99)]
    [InlineData(123.76, 123.99)]   // not a round-ten euro → …99
    public void GjmCharm_Apply_RoundsUpToNinetyNine(decimal input, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.GjmCharm, Wide);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Theory]
    [InlineData(50.30, 49.99)]     // round-ten euro → pull DOWN across the ten, not 50.99
    [InlineData(50.80, 49.99)]     // whole 50.xx euro maps to 49.99
    [InlineData(60.20, 59.99)]
    [InlineData(120.88, 119.99)]   // the worked example
    [InlineData(100.00, 99.99)]
    [InlineData(250.00, 249.99)]
    [InlineData(1000.00, 999.99)]
    [InlineData(1010.30, 1009.99)]
    public void GjmCharm_Apply_RoundTenEuro_PullsDownBelowTheTen(decimal input, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.GjmCharm, Wide);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Theory]
    [InlineData(10.40, 9.99)]   // €10.xx is a round-ten euro → always cross the ten down to …9.99, even
    [InlineData(10.99, 9.99)]   // though the drop is a >2% move: "never end in x0.xx" overrides the budget.
    [InlineData(20.60, 19.99)]
    public void GjmCharm_Apply_RoundTenEuro_AlwaysCrossesDownAcrossTheTen(decimal input, decimal expected)
    {
        // The Weber budget can block the pull across the ten (it used to fall back to a x0.49/x0.99), but
        // the round-ten euro is banned outright, so the price must still drop to the …9.99 below the ten.
        var result = _rounding.Apply(input, RoundingConvention.GjmCharm, Wide);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Theory]
    [InlineData(30.20, 30.10, 39.50, 31.49)]  // 887025lt: floor 30.10 blocks 29.99 → rise past the ten to 31.49
    [InlineData(30.80, 30.10, 39.50, 31.49)]
    [InlineData(100.40, 100.00, 130.00, 101.49)]
    public void GjmCharm_Apply_RoundTenEuro_FloorBlocksDrop_RisesPastTheTen(
        decimal input, decimal floor, decimal cap, decimal expected)
    {
        // …9.99 would breach the margin floor, so instead of sitting on the banned x0.49/x0.99 the price
        // rises past the whole ten to the next euro's …49 — never a round-ten euro, never below the floor.
        var result = _rounding.Apply(input, RoundingConvention.GjmCharm, new PriceBounds(floor, cap));
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
        Assert.True(result.Price >= floor && result.Price <= cap);
    }

    [Fact]
    public void GjmCharm_Apply_RoundTenEuro_DegenerateBand_KeepsPrice_WhenNothingCleanFits()
    {
        // Floor AND cap both sit inside the €30 ten (30.10 … 30.80): 29.99 is below the floor and 31.49 is
        // above the cap, so no non-round-ten charm point fits — the price is held rather than forced.
        var result = _rounding.Apply(30.40m, RoundingConvention.GjmCharm, new PriceBounds(30.10m, 30.80m));
        Assert.True(result.Price >= 30.10m && result.Price <= 30.80m);
    }

    [Fact]
    public void GjmCharm_Apply_FallsBack_WhenNinetyNineExceedsAnchorCap()
    {
        // up …99 = 45.99 exceeds the OldPrice cap 45.40 → nearest in-bounds …49/…99 = 44.99.
        var result = _rounding.Apply(45.40m, RoundingConvention.GjmCharm, new PriceBounds(0.01m, 45.40m));
        Assert.Equal(44.99m, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Theory]
    [InlineData(4.37, 4.39)]  // up to the next .x9 — jump 0.02 ≤ 2% budget (0.087)
    [InlineData(2.55, 2.59)]  // jump 0.04 ≤ 0.051
    [InlineData(3.82, 3.89)]  // jump 0.07 ≤ 0.076
    [InlineData(4.95, 4.99)]  // jump 0.04 ≤ 0.099
    public void GjmCharm_Apply_LowPrice_RoundsUpToNextX9_WithinBudget(decimal input, decimal expected)
    {
        // Below €5 the charm grid tightens to 10-cent .x9 steps; the up-bias still claws margin when the
        // jump fits the ~2% Weber budget. Every result ends in …9, like the >€5 …49/…99 charm.
        var result = _rounding.Apply(input, RoundingConvention.GjmCharm, Wide);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Theory]
    [InlineData(1.21, 1.19)]  // up jump 0.08 > 0.024 budget → nearest .x9 (down)
    [InlineData(2.10, 2.09)]  // up jump 0.09 > 0.042 → nearest (down)
    [InlineData(0.74, 0.69)]  // tie 0.69 / 0.79 → lower wins
    public void GjmCharm_Apply_LowPrice_SnapsToNearestX9_WhenUpExceedsBudget(decimal input, decimal expected)
    {
        // Cheap items: a 10-cent up-jump is more than 2% of the price, so the up-bias stands down and the
        // price snaps to the nearest .x9 — still a …9 ending, never the .x0 of the old nearest-10c rule.
        var result = _rounding.Apply(input, RoundingConvention.GjmCharm, Wide);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Fact]
    public void GjmCharm_Apply_LowPriceUpJump_HeldInsideLowRegime_AtBoundary()
    {
        // A price just under €5 must not let its up-jump leapfrog the €5 boundary into the half-euro
        // regime (that would break monotonicity). 4.97's up-candidate 4.99 (≤ €5) is fine; €5.00 itself
        // belongs to the >€5 charm regime and lands on the …99 just below it.
        Assert.Equal(4.99m, _rounding.Apply(4.97m, RoundingConvention.GjmCharm, Wide).Price);
        Assert.Equal(4.99m, _rounding.Apply(5.00m, RoundingConvention.GjmCharm, Wide).Price);
    }

    [Fact]
    public void GjmCharm_Apply_LowPrice_FallsToNearest_WhenUpX9ExceedsAnchorCap()
    {
        // up .x9 = 4.39 exceeds the OldPrice cap 4.35 → nearest in-bounds .x9 = 4.29.
        var result = _rounding.Apply(4.32m, RoundingConvention.GjmCharm, new PriceBounds(0.01m, 4.35m));
        Assert.Equal(4.29m, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Fact]
    public void GjmCharm_PinnedBounds_HeldWithoutSkipFlag()
    {
        var result = _rounding.Apply(30.00m, RoundingConvention.GjmCharm, new PriceBounds(30.00m, 30.00m));
        Assert.Equal(30.00m, result.Price);
        Assert.False(result.RoundingApplied);
        Assert.False(result.SkippedOutOfBounds);
    }

    [Fact]
    public void GjmCharm_Sweep_IsMonotonic_AndAlwaysEndsInNine()
    {
        // Full charm range across the €5 grid-transition: monotonic, in-bounds, and every result ends in
        // …9 — the 10-cent .x9 grid below €5 (0.49, 0.59, …, 4.99) and the half-euro …49/…99 above. A
        // dearer item never prices below a cheaper one (so the boundary at €5 introduces no inversion).
        decimal previous = 0m;
        for (decimal price = 0.50m; price <= 13000m; price += 0.05m)
        {
            var result = _rounding.Apply(price, RoundingConvention.GjmCharm, Wide).Price;
            var lastCentDigit = (int)(Math.Round(result * 100m) % 10m);

            Assert.True(result >= previous - 0.0001m, $"non-monotonic at {price}: {result} < prev {previous}");
            Assert.True(result >= Wide.Lower && result <= Wide.Upper, $"out of bounds at {price}: {result}");
            Assert.True(lastCentDigit == 9, $"does not end in …9 at {price}: {result}");
            Assert.True(Math.Floor(result) < 10m || Math.Floor(result) % 10m != 0m,
                $"round-ten euro (x0.xx banned) at {price}: {result}");

            previous = result;
        }
    }

    // ---- Gj50CharmHundreds: whole-currency (MKD/ALL) Gj50 charm — Weber-scaled, always ends …50 ----

    [Theory]
    [InlineData(4731, 4650)]      // budget 94.6 → step 100 (…50 grid)
    [InlineData(25000, 24950)]    // budget 500 → step 500 (…450/…950)
    [InlineData(123323, 122950)]  // budget 2466 → step 1000 (…950)
    [InlineData(800000, 799950)]  // budget 16000 → step 10000 (…9 950)
    public void Gj50CharmHundreds_RoundDown_GridScalesWithMagnitude(decimal input, decimal expected)
        => Assert.Equal(expected, RoundingService.RoundDown(input, RoundingConvention.Gj50CharmHundreds));

    [Theory]
    [InlineData(4731, 4750)]
    [InlineData(25000, 25450)]
    [InlineData(123323, 123950)]
    public void Gj50CharmHundreds_RoundUp_GridScalesWithMagnitude(decimal input, decimal expected)
        => Assert.Equal(expected, RoundingService.RoundUp(input, RoundingConvention.Gj50CharmHundreds));

    [Theory]
    [InlineData(4731, 4750)]       // flanks 4650/4750 → up for margin
    [InlineData(123323, 123950)]   // ×100 of the €1,233.23 → 1,239.50 worked example
    public void Gj50CharmHundreds_Apply_PrefersUp_ToClawBackMargin(decimal input, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.Gj50CharmHundreds, WideHundreds);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Theory]
    [InlineData(10000, 9950)]      // just under 10 000, not 10 050 just over it
    [InlineData(20000, 19950)]
    [InlineData(50000, 49950)]
    [InlineData(500000, 499950)]   // big-ticket round number → prestige-adjacent …9 950
    public void Gj50CharmHundreds_Apply_StaysJustBelowTheRounderNumber(decimal input, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.Gj50CharmHundreds, WideHundreds);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Theory]
    [InlineData(3050, 2950)]       // round-thousand block: …050 is banned → cross down to the …950 below
    [InlineData(10050, 9950)]
    public void Gj50CharmHundreds_Apply_RoundThousand_NeverLandsOnX050(decimal input, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.Gj50CharmHundreds, WideHundreds);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Fact]
    public void Gj50CharmHundreds_Apply_RoundThousand_FloorBlocksDrop_RisesPastTheThousand()
    {
        // 2 950 would breach the floor 3 000 → rise past the thousand to 3 150 rather than sit on 3 050.
        var result = _rounding.Apply(3050m, RoundingConvention.Gj50CharmHundreds, new PriceBounds(3000m, 4500m));
        Assert.Equal(3150m, result.Price);
        Assert.True(result.Price >= 3000m);
    }

    [Fact]
    public void Gj50CharmHundreds_Sweep_IsMonotonic_AndAlwaysEndsIn50()
    {
        // Whole-currency (MKD/ALL) sweep across every step-transition: monotonic, in-bounds, always ends …50,
        // and never a round-thousand …050 ending.
        decimal previous = 0m;
        for (decimal price = 100m; price <= 900000m; price += 37m)
        {
            var result = _rounding.Apply(price, RoundingConvention.Gj50CharmHundreds, WideHundreds).Price;
            var lastTwo = result % 100m;

            Assert.True(result >= previous - 0.0001m, $"non-monotonic at {price}: {result} < prev {previous}");
            Assert.True(result >= WideHundreds.Lower && result <= WideHundreds.Upper, $"out of bounds at {price}: {result}");
            Assert.True(lastTwo == 50m, $"does not end in …50 at {price}: {result}");
            Assert.True(Math.Floor(result / 100m) % 10m != 0m || Math.Floor(result / 100m) < 10m,
                $"round-thousand …050 (banned) at {price}: {result}");

            previous = result;
        }
    }

    // ---- GjmCharmHundreds: whole-currency (MKD/ALL) Mall charm — Weber-scaled, ends …90 (claw) or …50 ----

    [Theory]
    [InlineData(4731, 4690)]      // budget 94.6 → step 100 (…90 grid)
    [InlineData(30000, 29990)]    // budget 600 → step 500 (…490/…990)
    [InlineData(300000, 299990)]  // budget 6000 → step 5000 (…4 990/…9 990)
    public void GjmCharmHundreds_RoundDown_GridScalesWithMagnitude(decimal input, decimal expected)
        => Assert.Equal(expected, RoundingService.RoundDown(input, RoundingConvention.GjmCharmHundreds));

    [Theory]
    [InlineData(4731, 4790)]
    [InlineData(30000, 30490)]
    public void GjmCharmHundreds_RoundUp_GridScalesWithMagnitude(decimal input, decimal expected)
        => Assert.Equal(expected, RoundingService.RoundUp(input, RoundingConvention.GjmCharmHundreds));

    [Theory]
    [InlineData(5000, 4990)]      // just below the round 5 000
    [InlineData(30000, 29990)]
    [InlineData(50000, 49990)]
    public void GjmCharmHundreds_Apply_StaysJustBelowRoundNumber_EndingIn90(decimal input, decimal expected)
    {
        var result = _rounding.Apply(input, RoundingConvention.GjmCharmHundreds, WideHundreds);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Fact]
    public void GjmCharmHundreds_Apply_ClawsUpToNearRound90_ForMargin()
    {
        // In-budget: 30 600 claws UP to the near-round 30 990 (just below 31 000) to reclaim margin.
        var result = _rounding.Apply(30600m, RoundingConvention.GjmCharmHundreds, WideHundreds);
        Assert.Equal(30990m, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Theory]
    [InlineData(1340, 1350)]   // sub-budget: the …50 fallback lands nearer than either …90 (1290/1390)
    [InlineData(940, 950)]     // sub-budget: nearest fine …50 (950) beats …90 (890/990)
    [InlineData(1250, 1250)]   // already on a …50 charm point
    public void GjmCharmHundreds_Apply_SubBudget_UsesFineFiftyOrNinetyFallback(decimal input, decimal expected)
    {
        // Cheap items where the 100-wide …90 grid is coarser than the Weber budget: snap to the nearest
        // point of the fine …50 / …90 grid — the "or 50" fallback halves the snap distance.
        var result = _rounding.Apply(input, RoundingConvention.GjmCharmHundreds, WideHundreds);
        Assert.Equal(expected, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Fact]
    public void GjmCharmHundreds_Apply_FallsToDown_WhenUpExceedsAnchorCap()
    {
        // Up candidate 30 990 exceeds the OldPrice cap 30 900 → take the in-budget down …90 point 30 490.
        var result = _rounding.Apply(30600m, RoundingConvention.GjmCharmHundreds, new PriceBounds(0.01m, 30900m));
        Assert.Equal(30490m, result.Price);
        Assert.True(result.RoundingApplied);
    }

    [Fact]
    public void GjmCharmHundreds_Sweep_IsMonotonic_AndAlwaysEndsIn90Or50()
    {
        // Whole-currency (MKD/ALL) Mall sweep across every step-transition and the sub-budget → in-budget
        // boundary: monotonic, in-bounds, and every result ends in …90 or …50.
        decimal previous = 0m;
        for (decimal price = 100m; price <= 900000m; price += 37m)
        {
            var result = _rounding.Apply(price, RoundingConvention.GjmCharmHundreds, WideHundreds).Price;
            var lastTwo = result % 100m;

            Assert.True(result >= previous - 0.0001m, $"non-monotonic at {price}: {result} < prev {previous}");
            Assert.True(result >= WideHundreds.Lower && result <= WideHundreds.Upper, $"out of bounds at {price}: {result}");
            Assert.True(lastTwo == 90m || lastTwo == 50m, $"does not end in …90/…50 at {price}: {result}");

            previous = result;
        }
    }

    [Theory]
    [InlineData(RoundingConvention.GjmCharm)]
    [InlineData(RoundingConvention.Gj50Charm)]
    public void Charm_NeverLandsOnRoundTenEuro_EvenWhenTheFloorSitsInsideTheTen(RoundingConvention convention)
    {
        // Adversarial sweep of the floor-blocked path: for every floor between €10 and €300 (stepped so
        // many floors land *inside* a ten, like the €30.20 that produced 887025lt's 30.49), price a SKU
        // just above its floor with a generous cap. The result must never be a round-ten euro — UNLESS
        // the band is degenerate (floor AND the next clean charm point above the ten can't both fit).
        for (decimal floor = 10.00m; floor <= 300m; floor += 0.13m)
        {
            var cap = floor * 1.5m;                               // room to rise past the ten if needed
            for (decimal price = floor; price <= floor + 1.2m; price += 0.07m)
            {
                var result = _rounding.Apply(price, convention, new PriceBounds(floor, cap)).Price;
                Assert.True(result >= floor - 0.0001m && result <= cap + 0.0001m,
                    $"{convention} out of bounds: floor {floor}, price {price} -> {result}");

                var euro = Math.Floor(result);
                if (euro < 10m || euro % 10m != 0m) continue;     // fine

                // Round-ten euro is only tolerated when NO clean charm point fits [floor, cap].
                var below = convention == RoundingConvention.GjmCharm ? euro - 0.01m : euro - 0.50m;
                var above = convention == RoundingConvention.GjmCharm ? euro + 1.49m : euro + 1.50m;
                var belowFits = below >= floor && below <= cap;
                var aboveFits = above >= floor && above <= cap;
                Assert.False(belowFits || aboveFits,
                    $"{convention} landed on round-ten {result} (floor {floor}, price {price}) though a clean point fit");
            }
        }
    }

    [Fact]
    public void None_NormalizesToTwoDecimals()
    {
        var result = _rounding.Apply(85.4567m, RoundingConvention.None, Wide);
        Assert.Equal(85.46m, result.Price);
        Assert.False(result.RoundingApplied);
    }

    [Fact]
    public void Rounding_NeverViolatesGuardrails_AcrossGrid()
    {
        // Property check: whatever the convention/bounds, the result stays inside the bounds.
        var conventions = new[]
        {
            RoundingConvention.EndsIn99, RoundingConvention.EndsIn95,
            RoundingConvention.WholeEuro, RoundingConvention.Charm995,
            RoundingConvention.EndsIn99Hundreds, RoundingConvention.EndsIn50,
            RoundingConvention.Gj50Charm, RoundingConvention.GjmCharm,
            RoundingConvention.Gj50CharmHundreds, RoundingConvention.GjmCharmHundreds,
            RoundingConvention.None,
        };
        var prices = new[] { 3.10m, 9.99m, 47.31m, 99.50m, 250.77m, 999.99m, 1003.21m, 4321.99m };

        foreach (var convention in conventions)
        foreach (var price in prices)
        {
            var lower = price * 0.93m;
            var upper = price * 1.07m;
            var result = _rounding.Apply(price, convention, new PriceBounds(lower, upper));

            Assert.True(result.Price >= lower - 0.005m,
                $"{convention} {price}: {result.Price} below lower bound {lower}");
            Assert.True(result.Price <= upper + 0.005m,
                $"{convention} {price}: {result.Price} above upper bound {upper}");
        }
    }

    [Fact]
    public void Normalize_TwoDecimalRounding_CannotEscapeBounds()
    {
        // Lower bound 73.752: naive Round(73.7501) = 73.75 < bound — Normalize must go up.
        var price = RoundingService.Normalize(73.7501m, new PriceBounds(73.752m, 100m));
        Assert.True(price >= 73.752m);
    }
}
