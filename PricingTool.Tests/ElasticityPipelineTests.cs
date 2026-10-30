using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PricingTool.Core.Abstractions;
using PricingTool.Core.Domain;
using PricingTool.Core.Services;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;

namespace PricingTool.Tests;

public class OlsRegressionTests
{
    [Fact]
    public void Fit_RecoversKnownLine_SlopeInterceptR2()
    {
        // y = 3 - 2x, exact → slope -2, intercept 3, R² 1.
        var x = new[] { 1.0, 2.0, 3.0, 4.0 };
        var y = new[] { 1.0, -1.0, -3.0, -5.0 };

        var fit = OlsRegression.Fit(x, y);

        Assert.NotNull(fit);
        Assert.Equal(-2.0, fit!.Value.Slope, 6);
        Assert.Equal(3.0, fit.Value.Intercept, 6);
        Assert.Equal(1.0, fit.Value.R2, 6);
        Assert.Equal(0.0, fit.Value.StdErr, 6);   // perfect fit → zero standard error
    }

    [Fact]
    public void Fit_TwoPoints_StdErrIsInfinite()
    {
        // n = 2 → no residual degrees of freedom → slope uncertainty is unidentifiable.
        var fit = OlsRegression.Fit(new[] { 1.0, 2.0 }, new[] { 1.0, -1.0 });
        Assert.NotNull(fit);
        Assert.True(double.IsPositiveInfinity(fit!.Value.StdErr));
    }

    [Fact]
    public void Fit_NoVarianceInX_ReturnsNull()
    {
        var fit = OlsRegression.Fit(new[] { 2.0, 2.0, 2.0 }, new[] { 1.0, 2.0, 3.0 });
        Assert.Null(fit);
    }

    [Fact]
    public void Fit_TooFewPoints_ReturnsNull()
    {
        Assert.Null(OlsRegression.Fit(new[] { 1.0 }, new[] { 1.0 }));
    }

    [Fact]
    public void FromSums_MatchesFit()
    {
        var x = new[] { 1.0, 2.0, 3.0, 4.0, 5.0 };
        var y = new[] { 2.1, 1.0, 0.2, -0.9, -2.1 };
        var viaArrays = OlsRegression.Fit(x, y)!.Value;

        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
        for (var i = 0; i < x.Length; i++) { sx += x[i]; sy += y[i]; sxx += x[i]*x[i]; syy += y[i]*y[i]; sxy += x[i]*y[i]; }
        var viaSums = OlsRegression.FromSums(x.Length, sx, sy, sxx, syy, sxy)!.Value;

        Assert.Equal(viaArrays.Slope, viaSums.Slope, 9);
        Assert.Equal(viaArrays.R2, viaSums.R2, 9);
        Assert.Equal(viaArrays.StdErr, viaSums.StdErr, 9);
    }
}

public class ElasticityGateTests
{
    [Fact]
    public void CleanElasticFit_IsUsable()
    {
        Assert.True(ElasticityGate.IsUsable(slope: -1.8, r2: 0.6, observations: 10, distinctPricePoints: 6, priceRangeRatio: 1.2m));
    }

    [Theory]
    [InlineData(-0.5)] // inelastic but a legitimate, usable signal (Algo 5 just won't act on it)
    [InlineData(-1.0)]
    public void ValidNegativeElasticities_AreUsable_EvenIfInelastic(double slope)
    {
        Assert.True(ElasticityGate.IsUsable(slope, r2: 0.6, observations: 10, distinctPricePoints: 6, priceRangeRatio: 1.2m));
    }

    [Theory]
    [InlineData(0.3)]    // wrong sign
    [InlineData(-0.01)]  // near-zero / noise
    [InlineData(-12.0)]  // implausibly steep artifact
    public void ImplausibleSlopes_AreRejected(double slope)
    {
        Assert.False(ElasticityGate.IsUsable(slope, r2: 0.6, observations: 10, distinctPricePoints: 6, priceRangeRatio: 1.2m));
    }

    [Fact]
    public void PoorFitOrThinData_IsRejected()
    {
        Assert.False(ElasticityGate.IsUsable(-1.8, r2: 0.1, observations: 10, distinctPricePoints: 6, priceRangeRatio: 1.2m)); // R² too low
        Assert.False(ElasticityGate.IsUsable(-1.8, r2: 0.6, observations: 3, distinctPricePoints: 6, priceRangeRatio: 1.2m));  // too few buckets
        Assert.False(ElasticityGate.IsUsable(-1.8, r2: 0.6, observations: 10, distinctPricePoints: 2, priceRangeRatio: 1.2m)); // too few prices
        Assert.False(ElasticityGate.IsUsable(-1.8, r2: 0.6, observations: 10, distinctPricePoints: 6, priceRangeRatio: 1.01m)); // no price spread
    }

    [Theory]
    [InlineData(-1.5, true)]
    [InlineData(-1.0, false)]
    [InlineData(-0.5, false)]
    public void IsElastic_OnlyBelowMinusOne(double slope, bool expected)
        => Assert.Equal(expected, ElasticityGate.IsElastic(slope));

    [Theory]
    [InlineData(-2.0, 0.1, true)]    // clearly elastic, tight SE → confident
    [InlineData(-1.18, 0.05, true)]  // near unit but tight → still confident
    [InlineData(-1.18, 0.4, false)]  // near unit, noisy → not confident
    [InlineData(-1.5, 0.5, false)]   // elastic point estimate but SE too wide → not confident
    public void IsConfidentlyElastic_RequiresUpperCiBelowMinusOne(double slope, double se, bool expected)
        => Assert.Equal(expected, ElasticityGate.IsConfidentlyElastic(slope, se));

    [Fact]
    public void IsConfidentlyElastic_InfiniteSe_IsNeverConfident()
        => Assert.False(ElasticityGate.IsConfidentlyElastic(-3.0, double.PositiveInfinity));
}

public class ElasticityFitServiceTests
{
    private sealed class StubReader : IElasticitySourceReader
    {
        private readonly IReadOnlyList<ElasticityFitInput> _rows;
        public StubReader(IReadOnlyList<ElasticityFitInput> rows) => _rows = rows;
        public IReadOnlyList<ElasticityScope>? ScopesSeen { get; private set; }
        public Task<IReadOnlyList<ElasticityFitInput>> GetElasticityInputsAsync(
            IReadOnlyList<ElasticityScope> scopes, int windowDays, CancellationToken ct = default)
        {
            ScopesSeen = scopes;
            return Task.FromResult(_rows);
        }
    }

    private static PricingToolDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PricingToolDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    // A clean synthetic series where ln(units) = a + slope·ln(price) exactly (→ R²=1), with enough
    // distinct prices and spread to pass the gate.
    private static ElasticityFitInput MakeInput(string sku, double slope)
    {
        var prices = new[] { 10.0, 12.0, 14.0, 16.0, 18.0, 20.0, 22.0, 24.0, 26.0, 28.0 };
        int n = prices.Length;
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
        foreach (var p in prices)
        {
            var lx = Math.Log(p);
            var ly = 6.0 + slope * lx;
            sx += lx; sy += ly; sxx += lx * lx; syy += ly * ly; sxy += lx * ly;
        }
        var avg = prices.Average();
        var std = Math.Sqrt(prices.Sum(p => (p - avg) * (p - avg)) / (n - 1));
        return new ElasticityFitInput(sku, n, n, (decimal)prices.Min(), (decimal)prices.Max(),
            avg, std, sx, sy, sxx, syy, sxy);
    }

    [Fact]
    public async Task FitLayer_FitsGatesAndReplaces_Idempotently()
    {
        using var db = NewDb();
        db.Layers.Add(new Layer
        {
            Id = 1, Brand = "GjirafaMall", CountryCode = "KS", DisplayName = "KS",
            OperationalDatabase = "GjirafaMall", Currency = "EUR", SrPlatformId = 2, SrCompanyId = 0,
        });
        await db.SaveChangesAsync();

        var reader = new StubReader(new[] { MakeInput("SKU-EL", -2.0), MakeInput("SKU-IN", -0.5) });
        var svc = new ElasticityFitService(db, reader, new EfBulkWriter(db), NullLogger<ElasticityFitService>.Instance);

        var fitted = await svc.FitLayerAsync(1);
        Assert.Equal(2, fitted);

        var el = db.SkuElasticities.Single(e => e.Sku == "SKU-EL");
        Assert.True(el.IsUsable);
        Assert.True(el.Coefficient < -1m);          // elastic
        Assert.True(el.R2 > 0.99m);
        Assert.True(el.StandardError >= 0m && el.StandardError < 0.01m);  // perfect synthetic fit → ~0 SE
        Assert.True(ElasticityGate.IsConfidentlyElastic((double)el.Coefficient, (double)el.StandardError));

        var inel = db.SkuElasticities.Single(e => e.Sku == "SKU-IN");
        Assert.True(inel.IsUsable);                 // well-fit but inelastic — stored, just not acted on
        Assert.InRange(inel.Coefficient, -0.6m, -0.4m);

        // Re-fit replaces rather than duplicating.
        await svc.FitLayerAsync(1);
        Assert.Equal(2, db.SkuElasticities.Count(e => e.LayerId == 1));
        Assert.NotNull(db.Layers.Single(l => l.Id == 1).LastElasticityFitUtc);
    }

    [Fact]
    public async Task FitLayer_PoolsSalesAcrossAllPlatformsOfTheCountry()
    {
        // A KS layer's fit must read BOTH KS storefronts' sales (shared warehouse + synced prices →
        // one country-level demand signal), and must NOT pick up another country's platforms.
        using var db = NewDb();
        db.Layers.Add(new Layer
        {
            Id = 1, Brand = "GjirafaMall", CountryCode = "KS", DisplayName = "GjMall KS",
            OperationalDatabase = "GjirafaMall", Currency = "EUR", SrPlatformId = 2, SrCompanyId = 0,
        });
        db.Layers.Add(new Layer
        {
            Id = 4, Brand = "Gjirafa50", CountryCode = "KS", DisplayName = "Gj50 KS",
            OperationalDatabase = "GjirafaEcommerce", Currency = "EUR", SrPlatformId = 1, SrCompanyId = 0,
        });
        db.Layers.Add(new Layer
        {
            Id = 2, Brand = "GjirafaMall", CountryCode = "MK", DisplayName = "GjMall MK",
            OperationalDatabase = "GjirafaMall", Currency = "MKD", SrPlatformId = 2, SrCompanyId = 5,
        });
        await db.SaveChangesAsync();

        var reader = new StubReader(Array.Empty<ElasticityFitInput>());
        var svc = new ElasticityFitService(db, reader, new EfBulkWriter(db), NullLogger<ElasticityFitService>.Instance);
        await svc.FitLayerAsync(4);

        Assert.NotNull(reader.ScopesSeen);
        Assert.Equal(2, reader.ScopesSeen!.Count);
        Assert.Contains(new ElasticityScope(1, 0), reader.ScopesSeen!);   // its own platform
        Assert.Contains(new ElasticityScope(2, 0), reader.ScopesSeen!);   // the sibling KS storefront
        Assert.DoesNotContain(new ElasticityScope(2, 5), reader.ScopesSeen!);  // MK stays out
    }
}
