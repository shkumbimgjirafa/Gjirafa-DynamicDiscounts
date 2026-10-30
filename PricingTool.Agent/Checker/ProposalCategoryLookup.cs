using Microsoft.Data.SqlClient;
using PricingTool.Agent.Tools;

namespace PricingTool.Agent.Checker;

/// <summary>The decision category of a run's changed proposals (GjirafaMall main group, Gjirafa50 L1 category).</summary>
public interface IProposalCategoryLookup
{
    /// <summary>Proposal id → category for the run's changed prices in <paramref name="bandIds"/>.</summary>
    Task<IReadOnlyDictionary<long, string>> CategoriesAsync(long runId, int layerId, IReadOnlyCollection<int> bandIds, CancellationToken ct = default);
}

/// <summary>Through the product map by SKU (<c>hk.ProductForProposal</c>), the same way the engine finds a SKU's
/// per-category rule.</summary>
public sealed class ProposalCategoryLookup : IProposalCategoryLookup
{
    private readonly AgentDataSource _source;
    public ProposalCategoryLookup(AgentDataSource source) => _source = source;

    public async Task<IReadOnlyDictionary<long, string>> CategoriesAsync(long runId, int layerId, IReadOnlyCollection<int> bandIds, CancellationToken ct = default)
    {
        var map = new Dictionary<long, string>();
        if (bandIds.Count == 0) return map;
        await using var conn = new SqlConnection(_source.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            DECLARE @Mall bit = (SELECT CASE WHEN Brand = 'GjirafaMall' THEN 1 ELSE 0 END FROM PricingTool.Layers WHERE Id = @L);
            SELECT pp.Id, CASE WHEN @Mall = 1 THEN ISNULL(p.MainGroup, N'Others') ELSE ISNULL(p.CategoryL1, N'Unknown') END
            FROM PricingTool.ProposedPrices pp
            CROSS APPLY hk.ProductForProposal(pp.LayerId, pp.ProductId, pp.Sku) p
            WHERE pp.PricingRunId = @Run AND pp.HasChange = 1
              AND pp.PriceBandId IN (SELECT CAST(value AS int) FROM STRING_SPLIT(@Bands, ','))
            """, conn) { CommandTimeout = 300 };
        cmd.Parameters.AddWithValue("@L", layerId);
        cmd.Parameters.AddWithValue("@Run", runId);
        cmd.Parameters.AddWithValue("@Bands", string.Join(",", bandIds));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) map[r.GetInt64(0)] = r.GetString(1);
        return map;
    }
}
