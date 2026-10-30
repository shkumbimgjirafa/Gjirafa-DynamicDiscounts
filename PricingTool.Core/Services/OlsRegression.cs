namespace PricingTool.Core.Services;

/// <summary>Result of a simple ordinary-least-squares fit of y on x. <see cref="StdErr"/> is the
/// standard error of the slope (∞ when there are too few points to estimate it).</summary>
public readonly record struct OlsFit(double Slope, double Intercept, double R2, double StdErr, int N);

/// <summary>
/// Closed-form simple linear regression (y = slope·x + intercept). For the elasticity fit we feed
/// x = ln(price), y = ln(units), so the slope IS the constant price elasticity of demand.
/// Returns null when there are too few points or x has no variation (slope unidentifiable).
/// </summary>
public static class OlsRegression
{
    public static OlsFit? Fit(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        if (x.Count != y.Count || x.Count < 2) return null;

        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
        for (var i = 0; i < x.Count; i++)
        {
            sx += x[i]; sy += y[i];
            sxx += x[i] * x[i]; syy += y[i] * y[i];
            sxy += x[i] * y[i];
        }
        return FromSums(x.Count, sx, sy, sxx, syy, sxy);
    }

    /// <summary>
    /// Fit from pre-aggregated sums — lets the heavy aggregation run set-based in SQL (Σx, Σy, Σx²,
    /// Σy², Σxy over the weekly buckets) and only the tiny final arithmetic happen in C#.
    /// </summary>
    public static OlsFit? FromSums(int n, double sumX, double sumY, double sumXX, double sumYY, double sumXY)
    {
        if (n < 2) return null;

        var sxxc = sumXX - sumX * sumX / n;       // centered Σx²
        if (sxxc <= 0) return null;               // x never varied → slope undefined

        var sxyc = sumXY - sumX * sumY / n;       // centered Σxy
        var syyc = sumYY - sumY * sumY / n;       // centered Σy²

        var slope = sxyc / sxxc;
        var intercept = (sumY - slope * sumX) / n;
        var r2 = syyc > 0 ? (sxyc * sxyc) / (sxxc * syyc) : 0d;

        // Standard error of the slope: SE = sqrt( MSE / Σ(x-x̄)² ), MSE = SSE / (n-2),
        // SSE = Σ(y-ŷ)² = Syy_centered − slope·Sxy_centered. With <3 points the slope's uncertainty
        // is unidentifiable → ∞ (the confidence gate then treats it as never-confident).
        var sse = Math.Max(0d, syyc - slope * sxyc);
        var stdErr = n > 2 ? Math.Sqrt(sse / (n - 2) / sxxc) : double.PositiveInfinity;

        return new OlsFit(slope, intercept, r2, stdErr, n);
    }
}
