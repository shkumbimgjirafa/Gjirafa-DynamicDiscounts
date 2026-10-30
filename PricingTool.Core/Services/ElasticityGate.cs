namespace PricingTool.Core.Services;

/// <summary>
/// Decides whether a fitted elasticity is trustworthy enough to store as usable. Gates on fit
/// quality (enough observations, enough distinct price points, real price variation, R²) and on
/// economic plausibility (the coefficient must be negative and of sane magnitude). Validated against
/// the live catalog: for one layer over 365d these thresholds yield a focused, high-confidence set.
///
/// NOTE: a usable coefficient may still be inelastic — Algorithm 5 separately acts ONLY on the
/// elastic ones (|E| &gt; 1). Inelastic-but-usable values are stored for analysis, not acted on.
/// Thresholds are constants here; promote to options if they need per-layer tuning.
/// </summary>
public static class ElasticityGate
{
    public const int MinObservations = 4;          // weekly buckets
    public const int MinDistinctPricePoints = 3;
    public const decimal MinPriceRangeRatio = 1.05m; // max/min price ≥ 1.05 (≥5% spread)
    public const double MinR2 = 0.20;
    public const double MinMagnitude = 0.05;        // |E| must exceed this (reject near-zero/noise)
    public const double MaxMagnitude = 8.0;         // |E| must be below this (reject collinear artifacts)

    /// <summary>z for the one-sided confidence that the true elasticity is below −1 (1.2816 ≈ 90%).</summary>
    public const double ElasticConfidenceZ = 1.2816;

    public static bool IsUsable(double slope, double r2, int observations, int distinctPricePoints, decimal priceRangeRatio) =>
        observations >= MinObservations
        && distinctPricePoints >= MinDistinctPricePoints
        && priceRangeRatio >= MinPriceRangeRatio
        && r2 >= MinR2
        && slope < -MinMagnitude   // must be negative (demand slopes down) and not near-zero
        && slope > -MaxMagnitude;  // and not an implausibly steep artifact

    /// <summary>True when a usable coefficient is in the ELASTIC region Algorithm 5 acts on.</summary>
    public static bool IsElastic(double slope) => slope < -1.0;

    /// <summary>
    /// True only when we are statistically confident the elasticity is below −1: even the optimistic
    /// (closest-to-zero) end of the one-sided confidence interval, slope + z·SE, is ≤ −1. This rejects
    /// noisy near-unit-elastic fits (e.g. −1.18 ± 0.4) whose profit-max markup E/(E+1) would explode.
    /// A non-finite SE (too few points) is never confident.
    /// </summary>
    public static bool IsConfidentlyElastic(double slope, double stdErr, double z = ElasticConfidenceZ) =>
        double.IsFinite(stdErr) && slope + z * stdErr <= -1.0;
}
