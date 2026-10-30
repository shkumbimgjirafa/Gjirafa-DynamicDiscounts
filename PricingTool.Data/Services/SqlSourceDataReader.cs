using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PricingTool.Core.Abstractions;
using PricingTool.Core.Domain;

namespace PricingTool.Data.Services;

/// <summary>
/// Pulls the daily dataset over the READ-ONLY source connection ("SourceReadOnly" connection
/// string). Prefers the deployed stored procedure; set SourceDataset:Mode=InlineQuery to run
/// the (corrected) query verbatim when a stored proc can't be created.
/// </summary>
public class SqlSourceDataReader : ISourceDataReader
{
    private readonly string _connectionString;
    private readonly bool _useStoredProcedure;
    private readonly ILogger<SqlSourceDataReader> _logger;

    public SqlSourceDataReader(IConfiguration config, ILogger<SqlSourceDataReader> logger)
    {
        _connectionString = config.GetConnectionString("SourceReadOnly")
            ?? throw new InvalidOperationException("Missing connection string 'SourceReadOnly'.");
        _useStoredProcedure = !string.Equals(
            config["SourceDataset:Mode"], "InlineQuery", StringComparison.OrdinalIgnoreCase);
        _logger = logger;
    }

    public async Task<IReadOnlyList<SnapshotRow>> GetDailyDatasetAsync(LayerSourceContext layer, CancellationToken ct = default)
    {
        var rows = new List<SnapshotRow>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 600;
        if (_useStoredProcedure)
        {
            command.CommandType = System.Data.CommandType.StoredProcedure;
            command.CommandText = SourceQueries.StoredProcedureName;
        }
        else
        {
            command.CommandType = System.Data.CommandType.Text;
            // The operational DB is baked into the query as a token because the source tables are
            // referenced by three-part name; overriding InitialCatalog alone would not switch DBs.
            // {opDb2} is the sibling platform (same country, shared warehouse); it falls back to this
            // layer's own DB when there is no sibling — @SiblingStoreId = -1 then makes that branch empty.
            command.CommandText = SourceQueries.DailyDatasetInline
                .Replace(SourceQueries.SiblingOperationalDbToken,
                    SafeDbName(layer.SiblingOperationalDatabase ?? layer.OperationalDatabase))
                .Replace(SourceQueries.OperationalDbToken, SafeDbName(layer.OperationalDatabase));
        }

        command.Parameters.AddWithValue("@StoreId", layer.StoreId);
        command.Parameters.AddWithValue("@SiblingStoreId", layer.SiblingStoreId);
        command.Parameters.AddWithValue("@TranslationCountryId", layer.TranslationCountryId);
        command.Parameters.AddWithValue("@WarehouseStoreId", layer.WarehouseStoreId);
        command.Parameters.AddWithValue("@WmsWarehouseId", layer.WmsWarehouseId);
        command.Parameters.AddWithValue("@FilterVendors", layer.FilterVendors);
        command.Parameters.AddWithValue("@ExcludeUnpublished", layer.ExcludeUnpublished);

        await using var reader = await command.ExecuteReaderAsync(ct);

        static decimal? Dec(SqlDataReader r, string col)
        {
            var i = r.GetOrdinal(col);
            return r.IsDBNull(i) ? null : Convert.ToDecimal(r.GetValue(i));
        }
        static decimal Dec0(SqlDataReader r, string col) => Dec(r, col) ?? 0m;
        static int Int0(SqlDataReader r, string col)
        {
            var i = r.GetOrdinal(col);
            return r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i));
        }
        static int? IntN(SqlDataReader r, string col)
        {
            var i = r.GetOrdinal(col);
            return r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i));
        }
        static string? StrN(SqlDataReader r, string col)
        {
            var i = r.GetOrdinal(col);
            return r.IsDBNull(i) ? null : r.GetString(i).Trim();
        }
        static string? Trunc(string? s, int max) => s is not null && s.Length > max ? s[..max] : s;

        while (await reader.ReadAsync(ct))
        {
            var skuOrdinal = reader.GetOrdinal("Sku");
            if (reader.IsDBNull(skuOrdinal)) continue;

            rows.Add(new SnapshotRow
            {
                Sku = reader.GetString(skuOrdinal).Trim(),
                ProductId = IntN(reader, "ProductId"),
                ProductName = StrN(reader, "ProductName"),
                // Capped to the column width like ActiveDiscountIds — a real barcode is 8-14 chars, but
                // this column is free text in the catalog and must never overflow the snapshot/proposal.
                Gtin = Trunc(StrN(reader, "Gtin"), 64),
                OldPrice = Dec(reader, "OldPrice"),
                AnchorPrice = Dec(reader, "AnchorPrice"),
                AnchorIsFallback = Int0(reader, "AnchorFromShelf") == 1,
                CurrentPrice = Dec(reader, "CurrentPrice"),
                CurrentDiscountPct = Dec(reader, "CurrentDiscountPct"),
                // Defensive cap: the id list is tiny in practice, but truncate to the column width so a
                // pathological product with hundreds of live discounts can never overflow it mid-run.
                ActiveDiscountIds = Trunc(StrN(reader, "ActiveDiscountIds"), 256),
                Pptcv = Dec(reader, "PPTCV"),
                GrossMargin = Dec(reader, "GrossMargin"),
                LocalWarehouseStock = Int0(reader, "LocalWarehouseStock"),
                SupplierWarehouseStock = Int0(reader, "Supplier_WarehouseStock"),
                SupplierId = IntN(reader, "SupplierId"),
                SupplierName = StrN(reader, "SupplierName"),
                BrandId = IntN(reader, "BrandId"),
                Brand = StrN(reader, "Brand"),
                IsNewProduct = Int0(reader, "IsNewProduct") == 1,
                OldestUnitAgeDays = IntN(reader, "OldestUnitAgeDays"),
                Qty7 = Int0(reader, "7d_qty"),
                Net7 = Dec0(reader, "7d_net"),
                Qty14 = Int0(reader, "14d_qty"),
                Qty30 = Int0(reader, "30d_qty"),
                Net30 = Dec0(reader, "30d_net"),
                Qty60 = Int0(reader, "60d_qty"),
                Qty90 = Int0(reader, "90d_qty"),
                Net90 = Dec0(reader, "90d_net"),
                LaunchDateUtc = null, // no reliable launch-date signal yet
            });
        }

        _logger.LogInformation("Pulled {Count} SKUs from {Db} ({Mode}).",
            rows.Count, layer.OperationalDatabase, _useStoredProcedure ? "stored procedure" : "inline query");
        return rows;
    }

    public async Task<IReadOnlyList<BatchSaleRow>> GetApprovedBatchSalesAsync(
        LayerSourceContext layer, DateTime sinceUtc, IReadOnlyDictionary<string, decimal> skuPushedPrices, CancellationToken ct = default)
    {
        var rows = new List<BatchSaleRow>();
        if (skuPushedPrices.Count == 0) return rows;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 600;
        command.CommandType = System.Data.CommandType.Text;
        // Same token-substitution order as the dataset query: sibling first, then this layer's own DB.
        command.CommandText = SourceQueries.ApprovedBatchSalesInline
            .Replace(SourceQueries.SiblingOperationalDbToken,
                SafeDbName(layer.SiblingOperationalDatabase ?? layer.OperationalDatabase))
            .Replace(SourceQueries.OperationalDbToken, SafeDbName(layer.OperationalDatabase));

        // Pipe-joined 'sku=price' pairs: the query splits these into #batch (Sku, PushedPrice) and keeps only
        // discounts priced at each SKU's pushed price. SKUs are alphanumeric (e.g. "1602568mo"), so '=' and
        // '|' never collide; the price is invariant-formatted so the '.' decimal parses under any SQL LANGUAGE.
        var skuPrices = string.Join('|', skuPushedPrices.Select(kv =>
            $"{kv.Key}={kv.Value.ToString("0.00", CultureInfo.InvariantCulture)}"));
        command.Parameters.AddWithValue("@skus", skuPrices);
        command.Parameters.AddWithValue("@pushed", sinceUtc);
        command.Parameters.AddWithValue("@StoreId", layer.StoreId);
        command.Parameters.AddWithValue("@SiblingStoreId", layer.SiblingStoreId);
        command.Parameters.AddWithValue("@WarehouseStoreId", layer.WarehouseStoreId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(0)) continue;
            rows.Add(new BatchSaleRow(
                Sku: reader.GetString(0).Trim(),
                SoldUnits: Convert.ToInt32(reader.GetValue(1)),
                SoldNet: Convert.ToDecimal(reader.GetValue(2)),
                CompletedSoldUnits: Convert.ToInt32(reader.GetValue(3)),
                CurrentStock: Convert.ToInt32(reader.GetValue(4)),
                ActiveDiscountIds: reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        _logger.LogInformation("Refreshed {Count} approved-batch SKUs from {Db} (since {Since:u}).",
            rows.Count, layer.OperationalDatabase, sinceUtc);
        return rows;
    }

    /// <summary>
    /// Guards the operational DB name before it is concatenated into the SQL. Values come from our
    /// own seeded Layer table, but validating keeps this safe if a layer is ever edited by hand.
    /// </summary>
    private static string SafeDbName(string name) => SourceQueries.SafeDbName(name);
}
