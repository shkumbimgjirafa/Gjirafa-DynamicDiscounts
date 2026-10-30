using Microsoft.Data.SqlClient;

namespace PricingTool.Agent.Storage;

/// <summary>
/// How an engine proposal finds its row in the product map (<c>hk.Products</c>): by SKU within the layer, the
/// proposal's own product id only choosing among that SKU's listings.
///
/// A <c>ProductId</c> join is wrong for GjirafaMall rows synced from Gjirafa50 (reason SYNCED_TO_GJ50): the
/// engine deliberately stamps those with the Gjirafa50 (GjirafaEcommerce) product id
/// (<c>PricingRunOrchestrator</c>), and the two shop databases number their products independently. On
/// GjirafaMall KS run #1101, 44,036 of 162,319 changed prices were synced: 32,726 carried an id the Mall
/// map doesn't have (so they fell to "Others"), and 11,310 an id that belongs to an unrelated Mall product
/// (so they took that product's category). Every non-synced row's id and SKU agree. Use
/// <c>hk.ProductForProposal</c> instead of joining on <c>ProductId</c> wherever an engine proposal needs its
/// category: the holdout, the agents' tools, the canary.
/// </summary>
public static class ProductMapSql
{
    public const string FunctionName = "hk.ProductForProposal";
    public const string SkuIndexName = "IX_hk_Products_LayerId_Sku";

    /// <summary>The inline table function (one statement, no string literals so it can sit inside EXEC).
    /// <c>SameProductId</c> = 1 when the row is the proposal's own product (always, for a non-synced row).</summary>
    public const string CreateFunction = """
        CREATE OR ALTER FUNCTION hk.ProductForProposal(@LayerId int, @ProductId int, @Sku nvarchar(400))
        RETURNS TABLE
        AS RETURN
            SELECT TOP 1 h.ProductId, h.Sku, h.Brand, h.CategoryL1, h.CategoryL2, h.MainGroup, h.IsGj50Listed,
                   CAST(CASE WHEN h.ProductId = @ProductId THEN 1 ELSE 0 END AS bit) AS SameProductId
            FROM hk.Products h
            WHERE h.LayerId = @LayerId AND h.Sku = @Sku
            ORDER BY CASE WHEN h.ProductId = @ProductId THEN 0 ELSE 1 END, h.ProductId
        """;

    /// <summary>
    /// Idempotent: the SKU index (so each lookup is one seek, not a scan of 2.6M rows) and the function.
    /// Does nothing while <c>hk.Products</c> doesn't exist yet; the refresher runs it again after each
    /// load, so a database whose product map arrives later still gets both.
    /// </summary>
    public const string Ensure = $"""
        IF OBJECT_ID(N'hk.Products', N'U') IS NOT NULL
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'hk.Products') AND name = N'{SkuIndexName}')
                CREATE NONCLUSTERED INDEX {SkuIndexName} ON hk.Products (LayerId, Sku) INCLUDE (CategoryL1, MainGroup);
            EXEC(N'{CreateFunction}');
        END
        """;

    /// <summary>Runs <see cref="Ensure"/> on the data connection (the tool's database, where <c>hk.Products</c>
    /// lives). The first run builds the index over the whole map, hence the long timeout.</summary>
    public static async Task EnsureAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(Ensure, conn) { CommandTimeout = 600 };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public const string Drop = $"""
        DROP FUNCTION IF EXISTS {FunctionName};
        IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'hk.Products') AND name = N'{SkuIndexName}')
            DROP INDEX {SkuIndexName} ON hk.Products;
        """;
}
