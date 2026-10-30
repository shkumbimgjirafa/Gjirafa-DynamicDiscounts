namespace PricingTool.Core.Domain;

public interface IPricingAlgorithm
{
    /// <summary>Stable identifier, e.g. "VELOCITY_FORECAST". Stored in votes and band settings.</summary>
    string Code { get; }

    /// <summary>Human-readable name for the admin UI.</summary>
    string DisplayName { get; }

    /// <summary>Returns null when the algorithm has no opinion for this SKU.</summary>
    AlgorithmVote? Evaluate(SkuContext ctx);
}
