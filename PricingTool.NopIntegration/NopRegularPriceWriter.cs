using System.Data;
using Microsoft.Data.SqlClient;

namespace PricingTool.NopIntegration;

/// <summary>The store's regular price as the engine reads it: the newest <c>TierPrice</c> row of the product and store.</summary>
public sealed record TierRow(int Id, decimal Price, decimal OldPrice)
{
    /// <summary>The reference (strike-through) price the engine derives: OldPrice, or Price when OldPrice is 0.</summary>
    public decimal Reference => OldPrice != 0 ? OldPrice : Price;
}

/// <summary>What the regular-price writer will do with one price: raise the row, insert one, only end our discount (the
/// price is the regular price already), or refuse with an exception kind and the reason.</summary>
public sealed record TierDecision(string Action, decimal NewPrice, decimal NewOldPrice, string? ExceptionKind = null, string? Detail = null)
{
    public const string Raise = "raise", Insert = "insert", AtTier = "at-tier", Refuse = "refuse";
}

/// <summary>
/// The regular-price writer (prompt 07): a price above the store's regular price (the newest <c>TierPrice</c> row, which
/// is what the engine's <c>#tier</c> reads) can't be a discount, so it is written to the tier row itself, in place
/// (<c>Price</c>; never <c>ProfitMargin</c>, never <c>Product.Price</c>, which isn't the store's price). It runs inside
/// <see cref="NopDiscountWriter"/>'s chunk transaction, behind the same switches, and one rule routes each price: at or
/// above the regular price here, below it the discount path. Our own <c>ai-price</c> discount on a raised SKU is ended in
/// the same transaction (or it would keep the old price on the shelf); a cheaper team deal is left alone and the raise is
/// counted as "raised under a team deal". <c>OldPrice</c> stays, except that a new price above a non-zero OldPrice lifts it
/// to the new price (no strike-through on a raise; never an invented reference price). No row for the store: one is
/// inserted (no role, quantity 0, no dates, OldPrice 0, ProfitMargin NULL) and <c>Product.HasTierPrices</c> set.
/// </summary>
public static class NopRegularPriceWriter
{
    /// <summary>
    /// The decision for one price at or above the regular price (pure). Refused: no ceiling given, or a price above it
    /// (max(anchor, current) of the run, checked again here whatever the engine did); a row that changed since the run
    /// (its reference price isn't the run's OldPrice, or its price is under the run's current price) unless it still holds
    /// our own last write (the sandbox: the engine reads the shop, not the sandbox, so our earlier raises are ours, not a
    /// change). The run keeps the shelf and reference prices, not the tier price behind a deal: a change hidden behind a
    /// deal on a row with a non-zero OldPrice isn't caught here, only by the transaction's own re-read.
    /// </summary>
    public static TierDecision Decide(PublishItem item, TierRow? tier, (decimal Price, decimal OldPrice)? ourLastWrite)
    {
        if (item.Ceiling is not { } ceiling)
            return Refused("no-ceiling", "A raise needs the run's ceiling, max(anchor, current); none was given: not written.");
        if (item.Price > ceiling)
            return Refused("above-ceiling", $"Price {item.Price:0.00} is above max(anchor, current) = {ceiling:0.00}: refused.");
        if (tier is null)
            return new TierDecision(TierDecision.Insert, item.Price, 0m);
        var ours = ourLastWrite is { } w && w.Price == tier.Price && w.OldPrice == tier.OldPrice;
        if (!ours && item.RunCurrentPrice is { } current && item.RunOldPrice is { } old && (tier.Reference != old || tier.Price < current))
            return Refused("changed-since-run", $"The shop's regular price is {tier.Price:0.00} (strike-through {tier.OldPrice:0.00}); the run saw " +
                                                $"{current:0.00} on the shelf and {old:0.00} as the reference: changed since the run, left as it is.");
        if (item.Price == tier.Price)
            return new TierDecision(TierDecision.AtTier, tier.Price, tier.OldPrice);
        return new TierDecision(TierDecision.Raise, item.Price, tier.OldPrice != 0 && item.Price > tier.OldPrice ? item.Price : tier.OldPrice);

        static TierDecision Refused(string kind, string detail) => new(TierDecision.Refuse, 0m, 0m, kind, detail);
    }

    /// <summary>The newest tier row per product for the store (the engine's <c>#tier</c>: highest Id first).</summary>
    public static async Task<Dictionary<int, TierRow>> ReadAsync(SqlConnection conn, SqlTransaction? tx, string schema, int storeId,
        IReadOnlyCollection<int> productIds, CancellationToken ct)
    {
        var rows = new Dictionary<int, TierRow>();
        if (productIds.Count == 0) return rows;
        await Ids(conn, tx, "#tierq", productIds, ct);
        await using var cmd = new SqlCommand($"""
            SELECT x.ProductId, x.Id, x.Price, x.OldPrice FROM (
                SELECT tp.ProductId, tp.Id, tp.Price, tp.OldPrice, ROW_NUMBER() OVER (PARTITION BY tp.ProductId ORDER BY tp.Id DESC) AS rn
                FROM #tierq q JOIN [{schema}].[TierPrice] tp ON tp.ProductId = q.Id
                WHERE tp.StoreId = @Store
            ) x WHERE x.rn = 1
            """, conn, tx) { CommandTimeout = 120 };
        cmd.Parameters.AddWithValue("@Store", storeId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows[r.GetInt32(0)] = new TierRow(r.GetInt32(1), r.GetDecimal(2), r.GetDecimal(3));
        return rows;
    }

    /// <summary>The price a shopper pays, computed the way the engine does (SourceQueries: <c>#tier</c>, <c>#disc</c>, the
    /// CurrentPrice case): the lowest public product deal live at <paramref name="at"/> (a percentage taken off the
    /// reference price), where it is under the regular price; otherwise the regular price. Null without a tier row and deal.</summary>
    public static async Task<Dictionary<int, (decimal? Shelf, decimal? Regular, string? Deal)>> ShelfPricesAsync(SqlConnection conn, SqlTransaction? tx,
        string schema, int storeId, IReadOnlyCollection<int> productIds, DateTime at, CancellationToken ct)
    {
        var result = new Dictionary<int, (decimal?, decimal?, string?)>();
        if (productIds.Count == 0) return result;
        await Ids(conn, tx, "#shelfq", productIds, ct);
        await using var cmd = new SqlCommand($"""
            SELECT q.Id, t.Price, d.DiscountedPrice, d.Name,
                   CASE WHEN d.DiscountedPrice IS NULL THEN t.Price WHEN t.Price IS NULL THEN d.DiscountedPrice
                        WHEN d.DiscountedPrice < t.Price THEN d.DiscountedPrice ELSE t.Price END
            FROM #shelfq q
            OUTER APPLY (SELECT TOP 1 tp.Price, tp.OldPrice FROM [{schema}].[TierPrice] tp
                         WHERE tp.ProductId = q.Id AND tp.StoreId = @Store ORDER BY tp.Id DESC) t
            OUTER APPLY (SELECT TOP 1 x.DiscountedPrice, x.Name FROM (
                             SELECT dc.Name, IIF(dc.UsePercentage = 1, ISNULL(NULLIF(t.OldPrice, 0), t.Price) * (1 - dc.DiscountPercentage / 100.0),
                                                 dc.DiscountAmount) AS DiscountedPrice, dc.Id
                             FROM [{schema}].[Discount_AppliedToProducts] dap JOIN [{schema}].[Discount] dc ON dc.Id = dap.Discount_Id
                             WHERE dap.Product_Id = q.Id AND dc.StoreId = @Store AND dc.RequiresCouponCode = 0 AND dc.DiscountApplyTypeId = 2
                               AND @At BETWEEN ISNULL(dc.StartDateUtc, '1900-01-01') AND ISNULL(dc.EndDateUtc, '2999-01-01')
                         ) x ORDER BY x.DiscountedPrice, x.Id) d
            """, conn, tx) { CommandTimeout = 120 };
        cmd.Parameters.AddWithValue("@Store", storeId);
        cmd.Parameters.Add("@At", SqlDbType.DateTime2).Value = at;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result[r.GetInt32(0)] = (r.IsDBNull(4) ? null : Convert.ToDecimal(r.GetValue(4)), r.IsDBNull(1) ? null : r.GetDecimal(1), r.IsDBNull(3) ? null : r.GetString(3));
        return result;
    }

    /// <summary>
    /// The chunk's tier writes, inside its transaction (#tw filled by the caller): a raise updates the row only while it
    /// still holds the price and OldPrice read before (someone else's change in between is never overwritten); an insert
    /// only while the store still has no row; HasTierPrices set on the inserted products; then our ai-price discounts end
    /// on every SKU whose regular price now applies. Returns the raised and inserted rows (TierPriceId, ProductId) and the
    /// discounts ended.
    /// </summary>
    public static string WriteSql(string schema) => $"""
        DROP TABLE IF EXISTS #traised, #tinserted;
        CREATE TABLE #traised (Id int NOT NULL, ProductId int NOT NULL);
        CREATE TABLE #tinserted (Id int NOT NULL, ProductId int NOT NULL);

        UPDATE tp SET Price = w.NewPrice, OldPrice = w.NewOldPrice
        OUTPUT inserted.Id, inserted.ProductId INTO #traised (Id, ProductId)
        FROM [{schema}].[TierPrice] tp JOIN #tw w ON w.TierId = tp.Id
        WHERE w.Action = 'raise' AND tp.Price = w.ExpPrice AND tp.OldPrice = w.ExpOldPrice;

        INSERT INTO [{schema}].[TierPrice] (CustomerRoleId, ProductId, StoreId, Quantity, Price, StartDateTimeUtc, EndDateTimeUtc, OldPrice, ProfitMargin)
        OUTPUT inserted.Id, inserted.ProductId INTO #tinserted (Id, ProductId)
        SELECT NULL, w.ProductId, @Store, 0, w.NewPrice, NULL, NULL, 0, NULL
        FROM #tw w
        WHERE w.Action = 'insert' AND NOT EXISTS (SELECT 1 FROM [{schema}].[TierPrice] t WHERE t.ProductId = w.ProductId AND t.StoreId = @Store);

        UPDATE p SET HasTierPrices = 1 FROM [{schema}].[Product] p JOIN #tinserted i ON i.ProductId = p.Id WHERE p.HasTierPrices = 0;

        UPDATE d SET EndDateUtc = @Now
        FROM [{schema}].[Discount] d JOIN #tw w ON w.OurDiscountId = d.Id
        WHERE w.Action = 'at-tier' OR w.ProductId IN (SELECT ProductId FROM #traised UNION SELECT ProductId FROM #tinserted);

        SELECT 'raised', Id, ProductId FROM #traised
        UNION ALL SELECT 'inserted', Id, ProductId FROM #tinserted;
        """;

    public static DataTable WriteTable()
    {
        var t = new DataTable();
        foreach (var (col, type) in new[] { ("Sku", typeof(string)), ("ProductId", typeof(int)), ("Action", typeof(string)), ("TierId", typeof(int)),
                     ("NewPrice", typeof(decimal)), ("NewOldPrice", typeof(decimal)), ("ExpPrice", typeof(decimal)), ("ExpOldPrice", typeof(decimal)), ("OurDiscountId", typeof(int)) })
            t.Columns.Add(col, type);
        return t;
    }

    public const string WriteTableDdl = """
        DROP TABLE IF EXISTS #tw;
        CREATE TABLE #tw (Sku nvarchar(400) NOT NULL, ProductId int NOT NULL, Action varchar(10) NOT NULL, TierId int NULL,
                          NewPrice decimal(18,4) NOT NULL, NewOldPrice decimal(18,4) NOT NULL, ExpPrice decimal(18,4) NULL,
                          ExpOldPrice decimal(18,4) NULL, OurDiscountId int NULL);
        """;

    private static async Task Ids(SqlConnection conn, SqlTransaction? tx, string table, IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        await using (var create = new SqlCommand($"DROP TABLE IF EXISTS {table}; CREATE TABLE {table} (Id int NOT NULL PRIMARY KEY);", conn, tx))
            await create.ExecuteNonQueryAsync(ct);
        var t = new DataTable();
        t.Columns.Add("Id", typeof(int));
        foreach (var id in ids.Distinct()) t.Rows.Add(id);
        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx) { DestinationTableName = table, BulkCopyTimeout = 120 };
        await bulk.WriteToServerAsync(t, ct);
    }
}
