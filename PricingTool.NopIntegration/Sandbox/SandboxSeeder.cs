using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace PricingTool.NopIntegration.Sandbox;

/// <summary>
/// Creates the shop sandbox from the live schema snapshot and fills it with the KS state the data session
/// exported read-only: the discounts live now or starting within 60 days (hk.ActiveDiscountsKs), their
/// product mappings (hk.ActiveDiscountProductsKs), the KS-scope products' discount flags
/// (hk.ProductDiscountFlagsKs, non-deleted products only) and the newest KS tier price per product (hk.TierPricesKs,
/// the store's regular price). Live ids are kept (IDENTITY_INSERT), so overlap, supersede, flag merges, raises and
/// round-trips run against the real state. Left out: expired discounts, products outside the KS export (their
/// mappings and tier rows stay, without a Product row), older tier rows and other stores' tier rows;
/// Product.HasTierPrices is set where a KS tier row exists.
/// </summary>
public static class SandboxSeeder
{
    public sealed record SeedResult(string Db, int Discounts, int Mappings, int Products, long Milliseconds)
    {
        public int TierPrices { get; init; }
    }

    public static async Task CreateAsync(SqlConnection conn, IReadOnlyList<LiveSchemaRow> snapshot, CancellationToken ct = default)
    {
        foreach (var statement in SandboxSchema.CreateStatements(snapshot))
        {
            await using var cmd = new SqlCommand(statement, conn) { CommandTimeout = 300 };
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public static async Task<IReadOnlyList<SeedResult>> SeedAsync(SqlConnection conn, IReadOnlyList<LiveSchemaRow> snapshot, CancellationToken ct = default)
    {
        var results = new List<SeedResult>();
        foreach (var db in snapshot.Select(r => r.Db).Distinct().OrderBy(d => d, StringComparer.Ordinal))
        {
            var sw = Stopwatch.StartNew();
            var s = SandboxSchema.SchemaFor(db);
            var discounts = await ExecAsync(conn, CopySql(snapshot, db, "Discount", "hk.ActiveDiscountsKs", identityInsert: true), db, ct);
            var mappings = await ExecAsync(conn, $"""
                INSERT INTO [{s}].[Discount_AppliedToProducts] (Discount_Id, Product_Id)
                SELECT DISTINCT Discount_Id, Product_Id FROM hk.ActiveDiscountProductsKs WHERE Db = @Db;
                """, db, ct);
            var products = await ExecAsync(conn, $"""
                SET IDENTITY_INSERT [{s}].[Product] ON;
                INSERT INTO [{s}].[Product] (Id, Sku, HasTierPrices, Deleted, HasDiscountsApplied, HasDiscountsAppliedStoreIds)
                SELECT ProductId, Sku, 0, 0, ISNULL(HasDiscountsApplied, 0), HasDiscountsAppliedStoreIds
                FROM hk.ProductDiscountFlagsKs WHERE Db = @Db;
                SET IDENTITY_INSERT [{s}].[Product] OFF;
                """, db, ct);
            var tiers = await SeedTierPricesAsync(conn, db, ct);
            results.Add(new SeedResult(db, discounts, mappings, products, sw.ElapsedMilliseconds) { TierPrices = tiers });
        }
        return results;
    }

    /// <summary>
    /// Adds the regular-price tables to a sandbox built before them, without touching its discounts: Product is rebuilt
    /// with the live HasTierPrices column in its live place (its rows and ids kept, in one transaction), TierPrice is
    /// (re)created and seeded from hk.TierPricesKs. Run again, it reseeds TierPrice only.
    /// </summary>
    public static async Task<IReadOnlyList<SeedResult>> AddPricesAsync(SqlConnection conn, IReadOnlyList<LiveSchemaRow> snapshot, CancellationToken ct = default)
    {
        var results = new List<SeedResult>();
        foreach (var db in snapshot.Select(r => r.Db).Distinct().OrderBy(d => d, StringComparer.Ordinal))
        {
            var sw = Stopwatch.StartNew();
            var s = SandboxSchema.SchemaFor(db);
            var products = 0;
            await using (var has = new SqlCommand($"SELECT COL_LENGTH('[{s}].[Product]', 'HasTierPrices')", conn))
                if (await has.ExecuteScalarAsync(ct) is DBNull or null)
                {
                    await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
                    var rebuild = new List<string>
                    {
                        $"SELECT Id, Sku, Deleted, HasDiscountsApplied, HasDiscountsAppliedStoreIds INTO #product FROM [{s}].[Product];",
                        $"DROP TABLE [{s}].[Product];",
                    };
                    rebuild.AddRange(SandboxSchema.TableStatements(snapshot, db, "Product"));
                    var insert = $"""
                        SET IDENTITY_INSERT [{s}].[Product] ON;
                        INSERT INTO [{s}].[Product] (Id, Sku, HasTierPrices, Deleted, HasDiscountsApplied, HasDiscountsAppliedStoreIds)
                        SELECT Id, Sku, 0, Deleted, HasDiscountsApplied, HasDiscountsAppliedStoreIds FROM #product;
                        SET IDENTITY_INSERT [{s}].[Product] OFF;
                        """;
                    rebuild.Add(insert);
                    rebuild.Add("DROP TABLE #product;");
                    foreach (var statement in rebuild)
                    {
                        await using var cmd = new SqlCommand(statement, conn, tx) { CommandTimeout = 600 };
                        var n = await cmd.ExecuteNonQueryAsync(ct);
                        if (ReferenceEquals(statement, insert)) products = n;
                    }
                    await tx.CommitAsync(ct);
                }
            await using (var drop = new SqlCommand($"IF OBJECT_ID('[{s}].[TierPrice]') IS NOT NULL DROP TABLE [{s}].[TierPrice];", conn))
                await drop.ExecuteNonQueryAsync(ct);
            foreach (var statement in SandboxSchema.TableStatements(snapshot, db, "TierPrice"))
            {
                await using var cmd = new SqlCommand(statement, conn) { CommandTimeout = 300 };
                await cmd.ExecuteNonQueryAsync(ct);
            }
            var tiers = await SeedTierPricesAsync(conn, db, ct);
            results.Add(new SeedResult(db, 0, 0, products, sw.ElapsedMilliseconds) { TierPrices = tiers });
        }
        return results;
    }

    /// <summary>The newest KS tier row per product with its live id (dates NULL, as on every newest live KS row), then the
    /// products' HasTierPrices flag where a row exists.</summary>
    private static async Task<int> SeedTierPricesAsync(SqlConnection conn, string db, CancellationToken ct)
    {
        var s = SandboxSchema.SchemaFor(db);
        var rows = await ExecAsync(conn, $"""
            SET IDENTITY_INSERT [{s}].[TierPrice] ON;
            INSERT INTO [{s}].[TierPrice] (Id, CustomerRoleId, ProductId, StoreId, Quantity, Price, StartDateTimeUtc, EndDateTimeUtc, OldPrice, ProfitMargin)
            SELECT TierPriceId, CustomerRoleId, ProductId, StoreId, Quantity, Price, NULL, NULL, OldPrice, ProfitMargin
            FROM hk.TierPricesKs WHERE Db = @Db;
            SET IDENTITY_INSERT [{s}].[TierPrice] OFF;
            """, db, ct);
        await ExecAsync(conn, $"UPDATE p SET HasTierPrices = 1 FROM [{s}].[Product] p WHERE EXISTS (SELECT 1 FROM [{s}].[TierPrice] t WHERE t.ProductId = p.Id);", db, ct);
        return rows;
    }

    /// <summary>INSERT ... SELECT over the snapshot's columns, NOT NULL columns guarded with a neutral default
    /// (the export keeps some of them nullable).</summary>
    private static string CopySql(IReadOnlyList<LiveSchemaRow> snapshot, string db, string table, string source, bool identityInsert)
    {
        var s = SandboxSchema.SchemaFor(db);
        var cols = snapshot.Where(r => r.Db == db && r.Kind == "column" && r.Table == table).OrderBy(r => r.Ordinal).ToList();
        var names = string.Join(", ", cols.Select(c => $"[{c.Name}]"));
        var values = string.Join(", ", cols.Select(c => c.IsNullable || c.IsIdentity ? $"[{c.Name}]" : $"ISNULL([{c.Name}], {Default(c)})"));
        var insert = $"INSERT INTO [{s}].[{table}] ({names}) SELECT {values} FROM {source} WHERE Db = @Db;";
        return identityInsert ? $"SET IDENTITY_INSERT [{s}].[{table}] ON; {insert} SET IDENTITY_INSERT [{s}].[{table}] OFF;" : insert;
    }

    private static string Default(LiveSchemaRow c) =>
        c.Type.ToLowerInvariant() is "nvarchar" or "varchar" or "nchar" or "char" ? "N''" : "0";

    private static async Task<int> ExecAsync(SqlConnection conn, string sql, string db, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 600 };
        cmd.Parameters.AddWithValue("@Db", db);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
