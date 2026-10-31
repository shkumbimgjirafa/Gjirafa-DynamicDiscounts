using System.Reflection;
using System.Text;
using System.Text.Json;

namespace PricingTool.NopIntegration.Sandbox;

/// <summary>One column or index of the live shop schema, as the data session snapshotted it (hk.LiveDiscountSchema).</summary>
public sealed record LiveSchemaRow(
    string Db, string Kind, string Table, int Ordinal, string Name, string Type,
    int? MaxLength, int? Precision, int? Scale, bool IsNullable, bool IsIdentity, string? IndexColumns);

/// <summary>
/// The shop sandbox: per shop DB a schema <c>shop_sandbox_&lt;Db&gt;</c> in the hackathon database holding
/// <c>Discount</c>, <c>Discount_AppliedToProducts</c>, <c>TierPrice</c> (the store's regular price) and the
/// <c>Product</c> columns the publisher touches, with exactly the live columns (types, lengths, nullability,
/// identity) and the live indexes. One schema per shop because the shops differ (GjirafaMall's <c>Discount</c>
/// has a column GjirafaEcommerce's lacks; <c>TierPrice.Price</c> is decimal(18,4) on GjirafaMall, (18,2) on
/// GjirafaEcommerce). The writer addresses the shop through a configured schema (<c>dbo</c> live), so the SQL it
/// runs is the same in both places.
///
/// Two snapshots, embedded: <c>live-discount-schema.json</c> (the discount tables, the Product discount flags)
/// and <c>live-price-schema.json</c> (TierPrice, Product.HasTierPrices). They hold no <c>Product</c> index; the
/// sandbox gives <c>Product</c> its primary key on the identity <c>Id</c> plus a sandbox-only index on <c>Sku</c>
/// (named <c>IX_Sandbox_*</c>, outside the contract).
/// </summary>
public static class SandboxSchema
{
    public const string SchemaPrefix = "shop_sandbox_";
    public static readonly string[] Tables = { "Discount", "Discount_AppliedToProducts", "Product", "TierPrice" };

    public static string SchemaFor(string db) => SchemaPrefix + db;

    public static IReadOnlyList<LiveSchemaRow> LoadSnapshot() =>
        Load("live-discount-schema.json").Concat(Load("live-price-schema.json")).ToList();

    private static IEnumerable<LiveSchemaRow> Load(string file)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"PricingTool.NopIntegration.Sandbox.{file}")
            ?? throw new InvalidOperationException($"The live schema snapshot {file} is not embedded.");
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.GetProperty("rows").EnumerateArray().Select(r => new LiveSchemaRow(
            r.GetProperty("db").GetString()!, r.GetProperty("kind").GetString()!, r.GetProperty("table").GetString()!,
            r.GetProperty("ordinal").GetInt32(), r.GetProperty("name").GetString()!, r.GetProperty("type").GetString()!,
            Int(r, "maxLength"), Int(r, "precision"), Int(r, "scale"),
            r.GetProperty("isNullable").GetBoolean(), r.GetProperty("isIdentity").GetBoolean(),
            r.TryGetProperty("indexColumns", out var ic) && ic.ValueKind == JsonValueKind.String ? ic.GetString() : null)).ToList();

        static int? Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    }

    /// <summary>Drops (if present) and creates every shop's sandbox schema from the snapshot. Each statement is
    /// its own batch (CREATE SCHEMA must be alone).</summary>
    public static IReadOnlyList<string> CreateStatements(IReadOnlyList<LiveSchemaRow> snapshot)
    {
        var sql = new List<string>();
        foreach (var db in snapshot.Select(r => r.Db).Distinct().OrderBy(d => d, StringComparer.Ordinal))
        {
            var schema = SchemaFor(db);
            // The mirror's map of live ids goes with the rows it maps.
            sql.Add($"IF OBJECT_ID('[{schema}].[{SandboxMirror.MapTable}]') IS NOT NULL DROP TABLE [{schema}].[{SandboxMirror.MapTable}];");
            foreach (var table in Tables.Reverse())
                sql.Add($"IF OBJECT_ID('[{schema}].[{table}]') IS NOT NULL DROP TABLE [{schema}].[{table}];");
            sql.Add($"IF SCHEMA_ID('{schema}') IS NULL EXEC('CREATE SCHEMA [{schema}]');");
            foreach (var table in Tables)
                sql.AddRange(TableStatements(snapshot, db, table));
        }
        return sql;
    }

    /// <summary>CREATE TABLE and its indexes for one shop's table (nothing when the snapshot has no such table), in the sandbox
    /// schema or, for tests standing in for the live shop, in <paramref name="schema"/>.</summary>
    public static IReadOnlyList<string> TableStatements(IReadOnlyList<LiveSchemaRow> snapshot, string db, string table, string? schema = null)
    {
        var sql = new List<string>();
        schema ??= SchemaFor(db);
        var columns = snapshot.Where(r => r.Db == db && r.Kind == "column" && r.Table == table).OrderBy(r => r.Ordinal).ToList();
        if (columns.Count == 0) return sql;
        var indexes = snapshot.Where(r => r.Db == db && r.Kind == "index" && r.Table == table).ToList();
        var pk = indexes.FirstOrDefault(i => i.Name.StartsWith("PK_", StringComparison.Ordinal));

        var body = new List<string>(columns.Select(ColumnDefinition));
        if (pk is not null)
            body.Add($"CONSTRAINT [{pk.Name}] PRIMARY KEY {pk.Type} ({KeyList(pk.IndexColumns!)})");
        else if (table == "Product")
            body.Add("CONSTRAINT [PK_Product] PRIMARY KEY CLUSTERED ([Id])");
        sql.Add($"CREATE TABLE [{schema}].[{table}] (\n    {string.Join(",\n    ", body)}\n);");

        foreach (var ix in indexes.Where(i => i != pk))
        {
            var include = IncludedList(ix.IndexColumns!);
            sql.Add($"CREATE {ix.Type} INDEX [{ix.Name}] ON [{schema}].[{table}] ({KeyList(ix.IndexColumns!)})" +
                    (include.Length > 0 ? $" INCLUDE ({include})" : "") + ";");
        }
        if (table == "Product")
            sql.Add($"CREATE NONCLUSTERED INDEX [IX_Sandbox_Product_Sku] ON [{schema}].[Product] ([Sku]);");
        return sql;
    }

    internal static string ColumnDefinition(LiveSchemaRow c)
    {
        var type = c.Type.ToLowerInvariant() switch
        {
            "nvarchar" or "varchar" or "nchar" or "char" or "varbinary" or "binary" =>
                $"{c.Type}({(c.MaxLength is null or -1 ? "max" : c.MaxLength.Value.ToString())})",
            "decimal" or "numeric" => $"{c.Type}({c.Precision},{c.Scale})",
            "datetime2" or "time" or "datetimeoffset" => $"{c.Type}({c.Scale})",
            _ => c.Type,
        };
        return $"[{c.Name}] {type}{(c.IsIdentity ? " IDENTITY(1,1)" : "")} {(c.IsNullable ? "NULL" : "NOT NULL")}";
    }

    /// <summary>The key columns of a snapshot index ("A,B,C (incl)" → "[A], [B]").</summary>
    internal static string KeyList(string indexColumns) =>
        string.Join(", ", Split(indexColumns).Where(c => !c.Included).Select(c => $"[{c.Name}]"));

    internal static string IncludedList(string indexColumns) =>
        string.Join(", ", Split(indexColumns).Where(c => c.Included).Select(c => $"[{c.Name}]"));

    private static IEnumerable<(string Name, bool Included)> Split(string indexColumns) =>
        indexColumns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(c =>
            c.EndsWith("(incl)", StringComparison.Ordinal) ? (c[..^"(incl)".Length].Trim(), true) : (c, false));

    /// <summary>A human-readable script of the same statements (for review; the code runs the list).</summary>
    public static string Script(IReadOnlyList<LiveSchemaRow> snapshot)
    {
        var sb = new StringBuilder("-- Generated from live-discount-schema.json and live-price-schema.json by SandboxSchema.CreateStatements. Do not edit.\n");
        foreach (var s in CreateStatements(snapshot)) sb.Append(s).Append("\nGO\n");
        return sb.ToString();
    }
}
