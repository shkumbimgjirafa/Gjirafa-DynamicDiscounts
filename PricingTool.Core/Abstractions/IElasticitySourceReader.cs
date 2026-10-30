using PricingTool.Core.Domain;

namespace PricingTool.Core.Abstractions;

/// <summary>One (PlatformId, CompanyId) sales scope in GjirafaTranslations.SR_ProductsData.</summary>
public readonly record struct ElasticityScope(int PlatformId, int CompanyId);

/// <summary>
/// Reads per-SKU elasticity regression inputs from the transaction history (GjirafaTranslations
/// .SR_ProductsData) over a trailing window, pooled across the given (PlatformId, CompanyId)
/// scopes. A layer's fit passes ALL platforms of its COUNTRY (both storefronts sell the same
/// warehouse stock and the sync keeps shared-SKU prices identical, so the country's pooled weekly
/// buckets measure demand at the one price actually shown — and the extra volume lets more SKUs
/// clear the min-observations gate). The real implementation aggregates set-based over the
/// read-only source connection; the demo implementation returns nothing (no live transaction
/// history in demo mode).
/// </summary>
public interface IElasticitySourceReader
{
    Task<IReadOnlyList<ElasticityFitInput>> GetElasticityInputsAsync(
        IReadOnlyList<ElasticityScope> scopes, int windowDays, CancellationToken ct = default);
}
