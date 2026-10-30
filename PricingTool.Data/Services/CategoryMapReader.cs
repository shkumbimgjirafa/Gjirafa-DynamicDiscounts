using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using PricingTool.Core.Domain;

namespace PricingTool.Data.Services;

/// <summary>Where the engine finds a SKU's decision category for the per-category rules (see <see cref="CategoryBands"/>).</summary>
public interface ICategoryMapReader
{
    /// <summary>
    /// Every listing (product id, category) of every SKU on the layer that has at least one listing in
    /// <paramref name="categories"/>, or null when there is no product map to read (the rules then don't apply).
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<SkuListing>>?> ReadAsync(int layerId, bool isMall, IReadOnlyCollection<string> categories,
        CancellationToken ct = default);
}

/// <summary>
/// The hackathon's product map, <c>hk.Products</c> in the tool's database: GjirafaMall by main group (missing =
/// "Others"), Gjirafa50 by L1 category (missing = "Unknown"), the same categories as the holdout, the run checker
/// and the agents' scorecard. Looked up by SKU, never by product id (a GjirafaMall row synced from Gjirafa50 carries
/// Gjirafa50's id). Returns null where <c>hk.Products</c> doesn't exist (the imported tool's own databases).
/// </summary>
public sealed class HkCategoryMapReader : ICategoryMapReader
{
    private readonly string _connectionString;

    public HkCategoryMapReader(IConfiguration config) =>
        _connectionString = config.GetConnectionString("PricingToolDb") ?? throw new InvalidOperationException("Missing connection string 'PricingToolDb'.");

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<SkuListing>>?> ReadAsync(int layerId, bool isMall,
        IReadOnlyCollection<string> categories, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using (var exists = new SqlCommand("SELECT OBJECT_ID(N'hk.Products', N'U')", conn))
            if (await exists.ExecuteScalarAsync(ct) is null or DBNull) return null;

        await using var cmd = new SqlCommand("""
            WITH m AS (
                SELECT h.Sku, h.ProductId,
                       CASE WHEN @Mall = 1 THEN ISNULL(h.MainGroup, N'Others') ELSE ISNULL(h.CategoryL1, N'Unknown') END AS Category
                FROM hk.Products h
                WHERE h.LayerId = @L)
            SELECT m.Sku, m.ProductId, m.Category FROM m
            WHERE m.Sku IN (SELECT r.Sku FROM m r WHERE r.Category IN (SELECT value FROM STRING_SPLIT(@Cats, N'|')))
            """, conn) { CommandTimeout = 300 };
        cmd.Parameters.AddWithValue("@L", layerId);
        cmd.Parameters.AddWithValue("@Mall", isMall);
        cmd.Parameters.AddWithValue("@Cats", string.Join("|", categories));
        var map = new Dictionary<string, List<SkuListing>>(StringComparer.OrdinalIgnoreCase);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var sku = r.GetString(0);
            if (!map.TryGetValue(sku, out var list)) map[sku] = list = new List<SkuListing>(1);
            list.Add(new SkuListing(r.GetInt32(1), r.GetString(2)));
        }
        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<SkuListing>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }
}
