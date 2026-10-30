using Microsoft.Data.SqlClient;

namespace PricingTool.NopIntegration.Sandbox;

/// <summary>
/// The schema contract: the sandbox's actual DDL, read from the database's catalog, must equal the live
/// snapshot column for column and index for index. Any difference is listed; the contract test fails on it.
/// </summary>
public static class SandboxContract
{
    /// <summary>Reads the sandbox tables of one shop in the snapshot's own format.</summary>
    public static async Task<IReadOnlyList<LiveSchemaRow>> ReadActualAsync(SqlConnection conn, string db, CancellationToken ct = default)
    {
        var schema = SandboxSchema.SchemaFor(db);
        var rows = new List<LiveSchemaRow>();
        await using (var cmd = new SqlCommand("""
            SELECT t.name, c.column_id, c.name, ty.name,
                   CASE WHEN c.max_length = -1 THEN -1 WHEN ty.name IN ('nvarchar','nchar') THEN c.max_length / 2 ELSE c.max_length END,
                   c.precision, c.scale, c.is_nullable, c.is_identity
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE SCHEMA_NAME(t.schema_id) = @Schema
            ORDER BY t.name, c.column_id
            """, conn))
        {
            cmd.Parameters.AddWithValue("@Schema", schema);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add(new LiveSchemaRow(db, "column", r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetString(3),
                    Convert.ToInt32(r.GetValue(4)), Convert.ToInt32(r.GetValue(5)), Convert.ToInt32(r.GetValue(6)),
                    r.GetBoolean(7), r.GetBoolean(8), null));
        }
        await using (var cmd = new SqlCommand("""
            SELECT t.name, i.index_id, i.name, i.type_desc, CAST(CASE WHEN i.is_unique = 1 OR i.is_primary_key = 1 THEN 1 ELSE 0 END AS bit),
                   STRING_AGG(CAST(c.name + CASE WHEN ic.is_included_column = 1 THEN ' (incl)' ELSE '' END AS nvarchar(max)), ',')
                       WITHIN GROUP (ORDER BY ic.is_included_column, CASE WHEN ic.is_included_column = 0 THEN ic.key_ordinal ELSE ic.index_column_id END)
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE SCHEMA_NAME(t.schema_id) = @Schema AND i.type > 0
            GROUP BY t.name, i.index_id, i.name, i.type_desc, i.is_unique, i.is_primary_key
            """, conn))
        {
            cmd.Parameters.AddWithValue("@Schema", schema);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add(new LiveSchemaRow(db, "index", r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetString(3),
                    null, null, null, r.GetBoolean(4), false, r.GetString(5)));
        }
        return rows;
    }

    /// <summary>Every difference between the snapshot and the sandbox, as readable lines (empty = they match).
    /// Columns: same name, type, length, precision, scale, nullability, identity, and the same relative order.
    /// Indexes: the snapshot's indexes with the same type, uniqueness, keys and included columns; no other
    /// index on the discount tables. The sandbox-only Product helpers (PK_Product, IX_Sandbox_*) are not compared.</summary>
    public static IReadOnlyList<string> Differences(IReadOnlyList<LiveSchemaRow> snapshot, IReadOnlyList<LiveSchemaRow> actual, string db)
    {
        var diffs = new List<string>();
        foreach (var table in SandboxSchema.Tables)
        {
            var want = snapshot.Where(r => r.Db == db && r.Kind == "column" && r.Table == table).OrderBy(r => r.Ordinal).ToList();
            var have = actual.Where(r => r.Db == db && r.Kind == "column" && r.Table == table).OrderBy(r => r.Ordinal).ToList();
            if (want.Count == 0) continue;
            if (have.Count == 0) { diffs.Add($"{db}.{table}: table missing from the sandbox"); continue; }

            foreach (var w in want)
            {
                var h = have.FirstOrDefault(x => x.Name == w.Name);
                if (h is null) { diffs.Add($"{db}.{table}.{w.Name}: column missing"); continue; }
                if (!string.Equals(h.Type, w.Type, StringComparison.OrdinalIgnoreCase)) diffs.Add($"{db}.{table}.{w.Name}: type {h.Type}, live {w.Type}");
                if (h.MaxLength != w.MaxLength) diffs.Add($"{db}.{table}.{w.Name}: length {h.MaxLength}, live {w.MaxLength}");
                if (h.Precision != w.Precision || h.Scale != w.Scale) diffs.Add($"{db}.{table}.{w.Name}: precision/scale {h.Precision},{h.Scale}, live {w.Precision},{w.Scale}");
                if (h.IsNullable != w.IsNullable) diffs.Add($"{db}.{table}.{w.Name}: nullable {h.IsNullable}, live {w.IsNullable}");
                if (h.IsIdentity != w.IsIdentity) diffs.Add($"{db}.{table}.{w.Name}: identity {h.IsIdentity}, live {w.IsIdentity}");
            }
            foreach (var extra in have.Where(h => want.All(w => w.Name != h.Name)))
                diffs.Add($"{db}.{table}.{extra.Name}: column not in the live schema");
            var wantOrder = want.Select(w => w.Name).ToList();
            var haveOrder = have.Select(h => h.Name).Where(wantOrder.Contains).ToList();
            if (!wantOrder.SequenceEqual(haveOrder)) diffs.Add($"{db}.{table}: column order differs from live");

            var wantIx = snapshot.Where(r => r.Db == db && r.Kind == "index" && r.Table == table).ToList();
            var haveIx = actual.Where(r => r.Db == db && r.Kind == "index" && r.Table == table).ToList();
            foreach (var w in wantIx)
            {
                var h = haveIx.FirstOrDefault(x => x.Name == w.Name);
                if (h is null) { diffs.Add($"{db}.{table}: index {w.Name} missing"); continue; }
                if (h.Type != w.Type) diffs.Add($"{db}.{table}: index {w.Name} is {h.Type}, live {w.Type}");
                if (h.IsNullable != w.IsNullable) diffs.Add($"{db}.{table}: index {w.Name} uniqueness differs");
                if (h.IndexColumns != w.IndexColumns) diffs.Add($"{db}.{table}: index {w.Name} columns ({h.IndexColumns}), live ({w.IndexColumns})");
            }
            foreach (var extra in haveIx.Where(h => wantIx.All(w => w.Name != h.Name) && !IsSandboxHelper(table, h.Name)))
                diffs.Add($"{db}.{table}: index {extra.Name} not in the live schema");
        }
        return diffs;
    }

    private static bool IsSandboxHelper(string table, string index) =>
        table == "Product" && (index == "PK_Product" || index.StartsWith("IX_Sandbox_", StringComparison.Ordinal));
}
