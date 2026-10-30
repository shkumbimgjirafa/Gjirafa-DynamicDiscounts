using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace PricingTool.NopIntegration.Sandbox;

/// <summary>
/// Creates the shop sandbox from the live schema snapshot and fills it with the KS state the data session
/// exported read-only: the discounts live now or starting within 60 days (hk.ActiveDiscountsKs), their
/// product mappings (hk.ActiveDiscountProductsKs) and the KS-scope products' discount flags
/// (hk.ProductDiscountFlagsKs, non-deleted products only). Live ids are kept (IDENTITY_INSERT), so overlap,
/// supersede, flag merges and round-trips run against the real state. Left out: expired discounts, and
/// products outside the KS export (their mappings stay, without a Product row).
/// </summary>
public static class SandboxSeeder
{
    public sealed record SeedResult(string Db, int Discounts, int Mappings, int Products, long Milliseconds);

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
                INSERT INTO [{s}].[Product] (Id, Sku, Deleted, HasDiscountsApplied, HasDiscountsAppliedStoreIds)
                SELECT ProductId, Sku, 0, ISNULL(HasDiscountsApplied, 0), HasDiscountsAppliedStoreIds
                FROM hk.ProductDiscountFlagsKs WHERE Db = @Db;
                SET IDENTITY_INSERT [{s}].[Product] OFF;
                """, db, ct);
            results.Add(new SeedResult(db, discounts, mappings, products, sw.ElapsedMilliseconds));
        }
        return results;
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
