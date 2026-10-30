using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Storage;
using PricingTool.Agent.Tools;
using PricingTool.Data;
using PricingTool.NopIntegration.Sandbox;

namespace PricingTool.NopIntegration;

public static class PublishKinds
{
    public const string Regular = "Regular";
    public const string Campaign = "Campaign";
}

/// <summary>One price to publish: the final price the shopper should pay, and the shelf price when known
/// (a discount can only lower a price, so a price at or above the shelf can't be published as one).</summary>
public sealed record PublishItem(string Sku, decimal Price, decimal? ShelfPrice = null);

public sealed record PublishRequest(int LayerId, IReadOnlyList<PublishItem> Items)
{
    public string Kind { get; init; } = PublishKinds.Regular;
    /// <summary>Campaigns: the key in ai-campaign-&lt;key&gt;-&lt;sku&gt; and the window the discount is live in.</summary>
    public string? CampaignKey { get; init; }
    public DateTime? CampaignStartUtc { get; init; }
    public DateTime? CampaignEndUtc { get; init; }
    public long? EngineRunId { get; init; }
    /// <summary>DiscountPlatformTypeId 2 (app only) instead of 0 (web and app).</summary>
    public bool AppOnly { get; init; }
}

public sealed record PublishOutcome(
    long BatchId, string Status, int Created, int Updated, int Unchanged, int Skipped,
    int RoundTripChecked, int RoundTripMismatches, bool RolledBack, IReadOnlyList<PublishException> Exceptions);

/// <summary>
/// Writes prices into a shop's NOP discount tables the way nopCommerce's discount module does when an admin
/// creates a product discount (plan section 5, "Discount publishing"; field values from the team's live
/// pushes): a Discount row, its Discount_AppliedToProducts row, and the product's discount flags. One
/// <c>ai-price-&lt;layer&gt;-&lt;sku&gt;</c> discount per SKU per store, updated in place and renewed on a rolling
/// expiry; campaigns as time-boxed <c>ai-campaign-&lt;key&gt;-&lt;sku&gt;</c> discounts. Nothing is ever deleted.
///
/// Safety, in order: the previous state of every row it will touch goes to ai.PublishAudit first; each chunk
/// (~5k SKUs) is one short transaction; a SKU that already has a cheaper public deal we didn't create is left
/// alone (a team regular discount is end-dated only when SupersedeTeamRegularDiscounts is on, and only if it
/// covers that one product); afterwards the round-trip check reads the winning deal back, and too many
/// mismatches roll the batch back. Only configuration differs between the sandbox and the shop.
/// </summary>
public sealed class NopDiscountWriter
{
    private const string OurPrefix = "ai-";
    private const string CampaignPrefix = "ai-campaign-";
    private readonly PublisherOptions _options;
    private readonly AgentDataSource _tool;
    private readonly AgentDbContext _db;
    private readonly PricingToolDbContext _toolDb;
    private readonly IShopCacheInvalidator _cache;
    private readonly ILogger<NopDiscountWriter> _logger;
    private readonly TimeProvider _clock;
    private readonly IKillSwitch? _killSwitch;

    /// <summary>Tests only: runs inside each chunk's transaction after its writes, before the commit.</summary>
    internal Func<int, Task>? BeforeChunkCommit { get; set; }

    public NopDiscountWriter(IOptions<PublisherOptions> options, AgentDataSource tool, AgentDbContext db, PricingToolDbContext toolDb,
        IShopCacheInvalidator cache, ILogger<NopDiscountWriter> logger, TimeProvider? clock = null, IKillSwitch? killSwitch = null)
    {
        _options = options.Value;
        _tool = tool;
        _db = db;
        _toolDb = toolDb;
        _cache = cache;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _killSwitch = killSwitch;
    }

    public static string NameFor(PublishRequest r, string sku) =>
        r.Kind == PublishKinds.Campaign ? $"ai-campaign-{r.CampaignKey}-{sku}" : $"ai-price-{r.LayerId}-{sku}";

    public async Task<PublishOutcome> PublishAsync(PublishRequest request, CancellationToken ct = default)
    {
        var now = Truncate(_clock.GetUtcNow().UtcDateTime);
        var (shop, layer) = await ShopForLayerAsync(request.LayerId, ct);
        var window = Window(request, now);

        var batch = new PublishBatch
        {
            LayerId = request.LayerId, Db = layer.Db, StoreId = layer.StoreId, Target = _options.Target, ShopSchema = shop.Schema,
            Kind = request.Kind, CampaignKey = request.CampaignKey, EngineRunId = request.EngineRunId, Status = "Running",
            StartedUtc = now, Requested = request.Items.Count,
        };
        _db.PublishBatches.Add(batch);
        await _db.SaveChangesAsync(ct);

        // The switches: any one of them stops the publish before a single shop row is touched.
        if (await StopReasonAsync(request.LayerId, ct) is { } stop)
            return await StopAsync(batch, stop);

        var exceptions = new List<PublishException>();
        var items = new List<PublishItem>();
        foreach (var g in request.Items.GroupBy(i => i.Sku.Trim(), StringComparer.Ordinal))
        {
            var item = g.Last() with { Sku = g.Key };
            if (item.Price <= 0) exceptions.Add(Exception(batch, item.Sku, "invalid-price", $"Price {item.Price} is not a price."));
            else if (NameFor(request, item.Sku).Length > 400) exceptions.Add(Exception(batch, item.Sku, "sku-too-long", "The discount name would exceed nvarchar(400)."));
            else items.Add(item);
        }

        var canaryLeftOut = 0;
        if (_options.Canary.Enabled)
        {
            var canary = _options.Canary;
            var inCategory = await CanarySkusAsync(layer, canary.Category!, ct);
            var outside = items.Count(i => !inCategory.Contains(i.Sku));
            var kept = items.Where(i => inCategory.Contains(i.Sku)).OrderBy(i => i.Sku, StringComparer.Ordinal)
                .Take(Math.Max(0, canary.MaxSkus)).ToList();
            canaryLeftOut = items.Count - kept.Count;
            batch.Note = $"Canary: {kept.Count} of {items.Count} prices published ('{canary.Category}', at most {canary.MaxSkus}); " +
                         $"{outside} outside the category and {canaryLeftOut - outside} over the cap left out.";
            items = kept;
        }

        var written = new List<(PublishItem Item, int ProductId, string Name)>();
        await using (var conn = new SqlConnection(shop.ConnectionString))
        {
            await conn.OpenAsync(ct);
            var chunkNo = 0;
            foreach (var chunk in items.OrderBy(i => i.Sku, StringComparer.Ordinal).Chunk(Math.Max(1, _options.ChunkSize)))
            {
                chunkNo++;
                var result = await PublishChunkAsync(conn, shop.Schema, layer, request, batch, window, now, chunk, chunkNo, exceptions, ct);
                written.AddRange(result);
            }

            await _cache.InvalidateAsync(layer.Db, written.Select(w => w.ProductId).Distinct().ToList(), ct);

            // Round-trip: the winning public deal on each product must be ours, at our price.
            var check = request.Kind == PublishKinds.Campaign ? window.Start : now;
            var mismatches = await VerifyAsync(conn, shop.Schema, layer.StoreId, written, check, batch, exceptions, ct);
            batch.RoundTripChecked = written.Count;
            batch.RoundTripMismatches = mismatches;
        }

        _db.PublishExceptions.AddRange(exceptions);
        batch.Skipped = exceptions.Select(e => e.Sku).Distinct().Count(sku => written.All(w => w.Item.Sku != sku)) + canaryLeftOut;
        batch.Status = written.Count == 0 && items.Count > 0 ? "Failed" : "Published";
        batch.FinishedUtc = Truncate(_clock.GetUtcNow().UtcDateTime);
        await _db.SaveChangesAsync(CancellationToken.None);

        var rolledBack = false;
        if (batch.RoundTripChecked > 0 && 100m * batch.RoundTripMismatches / batch.RoundTripChecked > _options.RoundTripRollbackPct)
        {
            _logger.LogWarning("Batch {Batch}: {Mismatch} of {Checked} prices did not read back; rolling back.",
                batch.Id, batch.RoundTripMismatches, batch.RoundTripChecked);
            await RollbackAsync(batch.Id, ct);
            rolledBack = true;
        }

        return new PublishOutcome(batch.Id, rolledBack ? "RolledBack" : batch.Status, batch.Created, batch.Updated, batch.Unchanged,
            batch.Skipped, batch.RoundTripChecked, batch.RoundTripMismatches, rolledBack, exceptions);
    }

    private sealed record ProductState(int Id, bool HasDiscountsApplied, string? StoreIds);
    private sealed record OurDiscount(int Id, int ProductId, decimal Amount, DateTime? Start, DateTime? End);
    private sealed record OtherDeal(int Id, int ProductId, string Name, decimal Amount, DateTime? End, int Products);

    private async Task<List<(PublishItem, int, string)>> PublishChunkAsync(SqlConnection conn, string schema, LayerRef layer,
        PublishRequest request, PublishBatch batch, (DateTime Start, DateTime End) window, DateTime now, PublishItem[] chunk, int chunkNo,
        List<PublishException> exceptions, CancellationToken ct)
    {
        // 1. Read: products by SKU, our discounts on them, and other public amount deals active in the window.
        await Exec(conn, null, "DROP TABLE IF EXISTS #req; CREATE TABLE #req (Sku nvarchar(400) NOT NULL PRIMARY KEY, Name nvarchar(400) NOT NULL);", ct);
        var req = new DataTable();
        req.Columns.Add("Sku", typeof(string));
        req.Columns.Add("Name", typeof(string));
        foreach (var i in chunk) req.Rows.Add(i.Sku, NameFor(request, i.Sku));
        await BulkCopy(conn, null, "#req", req, ct);

        var products = new Dictionary<string, List<ProductState>>(StringComparer.Ordinal);
        await using (var cmd = new SqlCommand($"""
            SELECT r.Sku, p.Id, p.HasDiscountsApplied, p.HasDiscountsAppliedStoreIds
            FROM #req r JOIN [{schema}].[Product] p ON p.Sku = r.Sku AND p.Deleted = 0
            """, conn))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
            {
                if (!products.TryGetValue(rd.GetString(0), out var list)) products[rd.GetString(0)] = list = new();
                list.Add(new ProductState(rd.GetInt32(1), rd.GetBoolean(2), rd.IsDBNull(3) ? null : rd.GetString(3)));
            }

        var ours = new Dictionary<string, OurDiscount>(StringComparer.Ordinal);
        var others = new List<OtherDeal>();
        await using (var cmd = new SqlCommand($"""
            SELECT r.Sku, d.Id, dap.Product_Id, d.DiscountAmount, d.StartDateUtc, d.EndDateUtc
            FROM #req r
            JOIN [{schema}].[Product] p ON p.Sku = r.Sku AND p.Deleted = 0
            JOIN [{schema}].[Discount_AppliedToProducts] dap ON dap.Product_Id = p.Id
            JOIN [{schema}].[Discount] d ON d.Id = dap.Discount_Id
            WHERE d.Name = r.Name AND d.StoreId = @Store;

            SELECT d.Id, dap.Product_Id, d.Name, d.DiscountAmount, d.EndDateUtc,
                   (SELECT COUNT(*) FROM [{schema}].[Discount_AppliedToProducts] x WHERE x.Discount_Id = d.Id)
            FROM #req r
            JOIN [{schema}].[Product] p ON p.Sku = r.Sku AND p.Deleted = 0
            JOIN [{schema}].[Discount_AppliedToProducts] dap ON dap.Product_Id = p.Id
            JOIN [{schema}].[Discount] d ON d.Id = dap.Discount_Id
            WHERE d.StoreId = @Store AND d.DiscountApplyTypeId = 2 AND d.RequiresCouponCode = 0 AND d.UsePercentage = 0
              AND d.Name NOT LIKE 'ai-%'
              AND ISNULL(d.StartDateUtc, '1900-01-01') <= @To AND ISNULL(d.EndDateUtc, '2999-01-01') >= @From;
            """, conn))
        {
            cmd.Parameters.AddWithValue("@Store", layer.StoreId);
            cmd.Parameters.Add("@From", SqlDbType.DateTime2).Value = request.Kind == PublishKinds.Campaign ? window.Start : now;
            cmd.Parameters.Add("@To", SqlDbType.DateTime2).Value = request.Kind == PublishKinds.Campaign ? window.End : now;
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                ours[rd.GetString(0)] = new OurDiscount(rd.GetInt32(1), rd.GetInt32(2), rd.GetDecimal(3),
                    rd.IsDBNull(4) ? null : rd.GetDateTime(4), rd.IsDBNull(5) ? null : rd.GetDateTime(5));
            await rd.NextResultAsync(ct);
            while (await rd.ReadAsync(ct))
                others.Add(new OtherDeal(rd.GetInt32(0), rd.GetInt32(1), rd.GetString(2), rd.GetDecimal(3),
                    rd.IsDBNull(4) ? null : rd.GetDateTime(4), rd.GetInt32(5)));
        }

        // 2. Decide per SKU, and write the previous state of everything to be touched before touching it.
        var rows = new List<(PublishItem Item, ProductState Product, string Name, OurDiscount? Ours, bool Write, string? NewStoreIds)>();
        var supersede = new List<OtherDeal>();
        var audits = new List<PublishAudit>();
        foreach (var item in chunk)
        {
            var name = NameFor(request, item.Sku);
            if (!products.TryGetValue(item.Sku, out var found)) { exceptions.Add(Exception(batch, item.Sku, "unresolved-sku", "No live product with this SKU in the shop.")); continue; }
            if (found.Count > 1) { exceptions.Add(Exception(batch, item.Sku, "ambiguous-sku", $"{found.Count} live products share this SKU.")); continue; }
            var product = found[0];
            if (item.ShelfPrice is { } shelf && item.Price >= shelf)
            {
                exceptions.Add(Exception(batch, item.Sku, "at-or-above-shelf", $"Price {item.Price:0.00} is not below the shelf price {shelf:0.00}: a discount can't raise a price; the regular price needs changing."));
                continue;
            }
            var cheaper = others.Where(o => o.ProductId == product.Id && o.Amount <= item.Price).OrderBy(o => o.Amount).ToList();
            if (cheaper.Count > 0)
            {
                var canSupersede = request.Kind == PublishKinds.Regular && _options.SupersedeTeamRegularDiscounts
                    && cheaper.All(o => o.Products == 1 && IsTeamRegular(o.Name));
                if (!canSupersede)
                {
                    var c = cheaper[0];
                    exceptions.Add(Exception(batch, item.Sku, "cheaper-deal",
                        $"Discount #{c.Id} '{c.Name}' at {c.Amount:0.00} is cheaper than {item.Price:0.00} and is not ours; left as it is."));
                    continue;
                }
                foreach (var c in cheaper)
                {
                    supersede.Add(c);
                    audits.Add(Audit(batch, item.Sku, product.Id, "DiscountSuperseded", now, a => { a.DiscountId = c.Id; a.DiscountName = c.Name; a.OldDiscountAmount = c.Amount; a.OldEndDateUtc = c.End; a.NewEndDateUtc = Expired(now); }));
                }
            }

            ours.TryGetValue(item.Sku, out var existing);
            var start = request.Kind == PublishKinds.Campaign ? window.Start : existing?.Start ?? now;
            var unchanged = existing is not null && Math.Round(existing.Amount, 4) == Math.Round(item.Price, 4)
                && existing.End == window.End && existing.Start == start;
            var newIds = StoreIdList.Merge(product.StoreIds, layer.StoreId);
            var flagsChange = !product.HasDiscountsApplied || newIds != product.StoreIds;

            if (existing is null)
                audits.Add(Audit(batch, item.Sku, product.Id, "DiscountCreated", now, a => { a.DiscountName = name; a.NewDiscountAmount = item.Price; a.NewStartDateUtc = start; a.NewEndDateUtc = window.End; }));
            else if (!unchanged)
                audits.Add(Audit(batch, item.Sku, product.Id, "DiscountUpdated", now, a => { a.DiscountId = existing.Id; a.DiscountName = name; a.OldDiscountAmount = existing.Amount; a.OldStartDateUtc = existing.Start; a.OldEndDateUtc = existing.End; a.NewDiscountAmount = item.Price; a.NewStartDateUtc = start; a.NewEndDateUtc = window.End; }));
            if (flagsChange)
                audits.Add(Audit(batch, item.Sku, product.Id, "ProductFlags", now, a => { a.OldHasDiscountsApplied = product.HasDiscountsApplied; a.OldStoreIds = product.StoreIds; a.NewStoreIds = newIds; }));

            rows.Add((item, product, name, existing, existing is null || !unchanged || flagsChange, flagsChange ? newIds : null));
        }
        _db.PublishAudits.AddRange(audits);
        await _db.SaveChangesAsync(ct);

        // 3. Write the chunk in one short transaction: update in place, insert new (with their mapping), merge flags.
        var writes = rows.Where(r => r.Write).ToList();
        var created = new Dictionary<string, int>(StringComparer.Ordinal);
        if (writes.Count > 0 || supersede.Count > 0)
        {
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
            try
            {
                await Exec(conn, tx, """
                    DROP TABLE IF EXISTS #w, #created, #sup;
                    CREATE TABLE #w (Sku nvarchar(400) NOT NULL, ProductId int NOT NULL, Name nvarchar(400) NOT NULL, Price decimal(18,4) NOT NULL,
                                     ExistingId int NULL, StartUtc datetime2 NOT NULL, EndUtc datetime2 NOT NULL, NewStoreIds nvarchar(40) NULL);
                    CREATE TABLE #created (Id int NOT NULL, Name nvarchar(400) NOT NULL);
                    CREATE TABLE #sup (Id int NOT NULL PRIMARY KEY);
                    """, ct);
                var w = new DataTable();
                foreach (var (col, type) in new[] { ("Sku", typeof(string)), ("ProductId", typeof(int)), ("Name", typeof(string)), ("Price", typeof(decimal)),
                             ("ExistingId", typeof(int)), ("StartUtc", typeof(DateTime)), ("EndUtc", typeof(DateTime)), ("NewStoreIds", typeof(string)) })
                    w.Columns.Add(col, type);
                foreach (var r in writes)
                    w.Rows.Add(r.Item.Sku, r.Product.Id, r.Name, r.Item.Price, (object?)r.Ours?.Id ?? DBNull.Value,
                        request.Kind == PublishKinds.Campaign ? window.Start : r.Ours?.Start ?? now, window.End, (object?)r.NewStoreIds ?? DBNull.Value);
                await BulkCopy(conn, tx, "#w", w, ct);
                var sup = new DataTable();
                sup.Columns.Add("Id", typeof(int));
                foreach (var id in supersede.Select(s => s.Id).Distinct()) sup.Rows.Add(id);
                await BulkCopy(conn, tx, "#sup", sup, ct);

                var display = layer.HasDisplayAvailabilityTime ? ", DisplayAvailabilityTime" : "";
                var displayValue = layer.HasDisplayAvailabilityTime ? ", 0" : "";
                await using (var cmd = new SqlCommand($"""
                    UPDATE d SET DiscountAmount = w.Price, StartDateUtc = w.StartUtc, EndDateUtc = w.EndUtc, AdminComment = @Comment
                    FROM [{schema}].[Discount] d JOIN #w w ON w.ExistingId = d.Id
                    WHERE d.DiscountAmount <> w.Price OR ISNULL(d.EndDateUtc, '1900-01-01') <> w.EndUtc OR ISNULL(d.StartDateUtc, '1900-01-01') <> w.StartUtc;

                    INSERT INTO [{schema}].[Discount]
                        (Name, CouponCode, AdminComment, DiscountTypeId, UsePercentage, DiscountPercentage, DiscountAmount, MaximumDiscountAmount,
                         StartDateUtc, EndDateUtc, RequiresCouponCode, IsCumulative, DiscountLimitationId, LimitationTimes, MaximumDiscountedQuantity,
                         AppliedToSubCategories, StoreId, DiscountApplyTypeId, DiscountPlatformTypeId, IsVendorDiscount{display})
                    OUTPUT inserted.Id, inserted.Name INTO #created (Id, Name)
                    SELECT w.Name, NULL, @Comment, 2, 0, 0, w.Price, NULL,
                           w.StartUtc, w.EndUtc, 0, 0, 0, 0, NULL,
                           0, @Store, 2, @Platform, 0{displayValue}
                    FROM #w w WHERE w.ExistingId IS NULL;

                    INSERT INTO [{schema}].[Discount_AppliedToProducts] (Discount_Id, Product_Id)
                    SELECT c.Id, w.ProductId FROM #created c JOIN #w w ON w.Name = c.Name;

                    UPDATE p SET HasDiscountsApplied = 1, HasDiscountsAppliedStoreIds = w.NewStoreIds
                    FROM [{schema}].[Product] p JOIN #w w ON w.ProductId = p.Id
                    WHERE w.NewStoreIds IS NOT NULL;

                    UPDATE d SET EndDateUtc = @Now FROM [{schema}].[Discount] d JOIN #sup s ON s.Id = d.Id;

                    SELECT Id, Name FROM #created;
                    """, conn, tx))
                {
                    cmd.CommandTimeout = 300;
                    cmd.Parameters.AddWithValue("@Comment", $"pricing-tool batch {batch.Id}" + (request.EngineRunId is { } run ? $"; run {run}" : ""));
                    cmd.Parameters.AddWithValue("@Store", layer.StoreId);
                    cmd.Parameters.AddWithValue("@Platform", (short)(request.AppOnly ? 2 : 0));
                    cmd.Parameters.Add("@Now", SqlDbType.DateTime2).Value = Expired(now);
                    await using var rd = await cmd.ExecuteReaderAsync(ct);
                    while (await rd.ReadAsync(ct)) created[rd.GetString(1)] = rd.GetInt32(0);
                }
                if (BeforeChunkCommit is not null) await BeforeChunkCommit(chunkNo);
                await tx.CommitAsync(ct);
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException)
            {
                await tx.RollbackAsync(CancellationToken.None);
                _logger.LogWarning(ex, "Batch {Batch} chunk {Chunk} failed and was rolled back; nothing in it was written.", batch.Id, chunkNo);
                foreach (var r in rows)
                    exceptions.Add(Exception(batch, r.Item.Sku, "chunk-failed", $"Chunk {chunkNo} was rolled back: {ex.Message}"));
                return new List<(PublishItem, int, string)>();
            }
        }

        // 4. The writes happened: mark their audit rows, with the ids of the discounts just created.
        foreach (var a in audits)
        {
            a.Applied = true;
            if (a.Change == "DiscountCreated" && a.DiscountName is { } n && created.TryGetValue(n, out var id)) a.DiscountId = id;
        }
        batch.Created += rows.Count(r => r.Ours is null);
        batch.Updated += audits.Count(a => a.Change == "DiscountUpdated");
        batch.Unchanged += rows.Count(r => r.Ours is not null && audits.All(a => !(a.Change == "DiscountUpdated" && a.Sku == r.Item.Sku)));
        await _db.SaveChangesAsync(CancellationToken.None);
        return rows.Select(r => (r.Item, r.Product.Id, r.Name)).ToList();
    }

    /// <summary>For every published product, the lowest public amount deal live at <paramref name="at"/> must be
    /// ours and at our price. Returns the number of mismatches (each also an exception row).</summary>
    private async Task<int> VerifyAsync(SqlConnection conn, string schema, int storeId, IReadOnlyList<(PublishItem Item, int ProductId, string Name)> written,
        DateTime at, PublishBatch batch, List<PublishException> exceptions, CancellationToken ct)
    {
        if (written.Count == 0) return 0;
        await Exec(conn, null, "DROP TABLE IF EXISTS #rt; CREATE TABLE #rt (ProductId int NOT NULL PRIMARY KEY);", ct);
        var t = new DataTable();
        t.Columns.Add("ProductId", typeof(int));
        foreach (var id in written.Select(w => w.ProductId).Distinct()) t.Rows.Add(id);
        await BulkCopy(conn, null, "#rt", t, ct);

        var winners = new Dictionary<int, (string Name, decimal Amount)>();
        await using (var cmd = new SqlCommand($"""
            SELECT x.ProductId, x.Name, x.DiscountAmount FROM (
                SELECT rt.ProductId, d.Name, d.DiscountAmount,
                       ROW_NUMBER() OVER (PARTITION BY rt.ProductId ORDER BY d.DiscountAmount, d.Id) AS rn
                FROM #rt rt
                JOIN [{schema}].[Discount_AppliedToProducts] dap ON dap.Product_Id = rt.ProductId
                JOIN [{schema}].[Discount] d ON d.Id = dap.Discount_Id
                WHERE d.StoreId = @Store AND d.DiscountApplyTypeId = 2 AND d.RequiresCouponCode = 0 AND d.UsePercentage = 0
                  AND @At BETWEEN ISNULL(d.StartDateUtc, '1900-01-01') AND ISNULL(d.EndDateUtc, '2999-01-01')
            ) x WHERE x.rn = 1
            """, conn))
        {
            cmd.Parameters.AddWithValue("@Store", storeId);
            cmd.Parameters.Add("@At", SqlDbType.DateTime2).Value = at;
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) winners[rd.GetInt32(0)] = (rd.GetString(1), rd.GetDecimal(2));
        }

        var mismatches = 0;
        foreach (var (item, productId, name) in written)
        {
            if (winners.TryGetValue(productId, out var w) && w.Name == name && Math.Round(w.Amount, 2) == Math.Round(item.Price, 2)) continue;
            // On its days our own campaign discount is meant to win over the regular ai-price one: not a mismatch (a whole day's
            // publish would otherwise roll back on every campaign SKU).
            if (w.Name is { } winner && winner.StartsWith(CampaignPrefix, StringComparison.Ordinal) && !name.StartsWith(CampaignPrefix, StringComparison.Ordinal)) continue;
            mismatches++;
            exceptions.Add(Exception(batch, item.Sku, "round-trip", winners.TryGetValue(productId, out w)
                ? $"Reads back '{w.Name}' at {w.Amount:0.00}, expected ours at {item.Price:0.00}."
                : $"No live public deal reads back; expected ours at {item.Price:0.00}."));
        }
        return mismatches;
    }

    /// <summary>Ends our ai-price discounts on these SKUs now (never deletes them): the shelf price applies
    /// again. Audited like a publish, so it can be rolled back too. Returns the batch id.</summary>
    public Task<long> ExpireAsync(int layerId, IReadOnlyCollection<string> skus, CancellationToken ct = default) =>
        ExpireAsync(new PublishRequest(layerId, Array.Empty<PublishItem>()), skus, ct);

    /// <summary>Ends our ai-campaign-&lt;key&gt; discounts on these SKUs now, before the campaign's end (never deletes them):
    /// the regular price (or our ai-price discount) applies again. Audited, so it can be rolled back. Returns the batch id.</summary>
    public Task<long> ExpireCampaignAsync(int layerId, string campaignKey, IReadOnlyCollection<string> skus, CancellationToken ct = default) =>
        ExpireAsync(new PublishRequest(layerId, Array.Empty<PublishItem>()) { Kind = PublishKinds.Campaign, CampaignKey = campaignKey }, skus, ct);

    private async Task<long> ExpireAsync(PublishRequest probe, IReadOnlyCollection<string> skus, CancellationToken ct)
    {
        var layerId = probe.LayerId;
        var now = Truncate(_clock.GetUtcNow().UtcDateTime);
        var (shop, layer) = await ShopForLayerAsync(layerId, ct);
        var batch = new PublishBatch
        {
            LayerId = layerId, Db = layer.Db, StoreId = layer.StoreId, Target = _options.Target, ShopSchema = shop.Schema,
            Kind = "Expire", Status = "Running", StartedUtc = now, Requested = skus.Count,
        };
        _db.PublishBatches.Add(batch);
        await _db.SaveChangesAsync(ct);

        await using var conn = new SqlConnection(shop.ConnectionString);
        await conn.OpenAsync(ct);
        await Exec(conn, null, "DROP TABLE IF EXISTS #req; CREATE TABLE #req (Sku nvarchar(400) NOT NULL PRIMARY KEY, Name nvarchar(400) NOT NULL);", ct);
        var req = new DataTable();
        req.Columns.Add("Sku", typeof(string));
        req.Columns.Add("Name", typeof(string));
        foreach (var sku in skus.Distinct(StringComparer.Ordinal)) req.Rows.Add(sku, NameFor(probe, sku));
        await BulkCopy(conn, null, "#req", req, ct);

        var targets = new List<PublishAudit>();
        await using (var cmd = new SqlCommand($"""
            SELECT DISTINCT r.Sku, p.Id, d.Id, d.Name, d.DiscountAmount, d.StartDateUtc, d.EndDateUtc
            FROM #req r
            JOIN [{shop.Schema}].[Product] p ON p.Sku = r.Sku AND p.Deleted = 0
            JOIN [{shop.Schema}].[Discount_AppliedToProducts] dap ON dap.Product_Id = p.Id
            JOIN [{shop.Schema}].[Discount] d ON d.Id = dap.Discount_Id
            WHERE d.Name = r.Name AND d.StoreId = @Store AND ISNULL(d.EndDateUtc, '2999-01-01') > @Now
            """, conn))
        {
            cmd.Parameters.AddWithValue("@Store", layer.StoreId);
            cmd.Parameters.Add("@Now", SqlDbType.DateTime2).Value = now;
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                targets.Add(Audit(batch, rd.GetString(0), rd.GetInt32(1), "DiscountUpdated", now, a =>
                {
                    a.DiscountId = rd.GetInt32(2); a.DiscountName = rd.GetString(3);
                    a.OldDiscountAmount = rd.GetDecimal(4); a.NewDiscountAmount = rd.GetDecimal(4);
                    a.OldStartDateUtc = rd.IsDBNull(5) ? null : rd.GetDateTime(5); a.NewStartDateUtc = a.OldStartDateUtc;
                    a.OldEndDateUtc = rd.IsDBNull(6) ? null : rd.GetDateTime(6); a.NewEndDateUtc = Expired(now);
                }));
        }
        _db.PublishAudits.AddRange(targets);
        await _db.SaveChangesAsync(ct);

        if (targets.Count > 0)
        {
            await using var cmd = new SqlCommand(
                $"UPDATE [{shop.Schema}].[Discount] SET EndDateUtc = @Now WHERE Id IN ({string.Join(",", targets.Select(t => t.DiscountId!.Value))})", conn);
            cmd.Parameters.Add("@Now", SqlDbType.DateTime2).Value = Expired(now);
            await cmd.ExecuteNonQueryAsync(ct);
            foreach (var t in targets) t.Applied = true;
        }
        batch.Updated = targets.Count;
        batch.Status = "Published";
        batch.FinishedUtc = now;
        await _db.SaveChangesAsync(CancellationToken.None);
        return batch.Id;
    }

    /// <summary>Restores every row the batch changed, from its audit: updated discounts get their old amount
    /// and dates back, created ones are expired (never deleted), superseded team discounts get their end date
    /// back, product flags are restored exactly. One transaction.</summary>
    public async Task<int> RollbackAsync(long batchId, CancellationToken ct = default)
    {
        var batch = await _db.PublishBatches.SingleAsync(b => b.Id == batchId, ct);
        var audits = await _db.PublishAudits.Where(a => a.BatchId == batchId && a.Applied && a.RolledBackUtc == null).OrderByDescending(a => a.Id).ToListAsync(ct);
        var now = Truncate(_clock.GetUtcNow().UtcDateTime);
        var shop = _options.ShopFor(batch.Db, _tool.ConnectionString);
        if (!string.Equals(shop.Schema, batch.ShopSchema, StringComparison.Ordinal))
            throw new InvalidOperationException($"Batch {batchId} was written to '{batch.ShopSchema}', but the publisher now points at '{shop.Schema}'.");

        await using var conn = new SqlConnection(shop.ConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        var s = shop.Schema;
        foreach (var a in audits)
        {
            var (sql, bind) = a.Change switch
            {
                "DiscountUpdated" => ($"UPDATE [{s}].[Discount] SET DiscountAmount = @Amount, StartDateUtc = @Start, EndDateUtc = @End WHERE Id = @Id",
                    (Action<SqlParameterCollection>)(p => { p.AddWithValue("@Amount", a.OldDiscountAmount!.Value); Dt(p, "@Start", a.OldStartDateUtc); Dt(p, "@End", a.OldEndDateUtc); p.AddWithValue("@Id", a.DiscountId!.Value); })),
                "DiscountCreated" => ($"UPDATE [{s}].[Discount] SET EndDateUtc = @End WHERE Id = @Id",
                    p => { Dt(p, "@End", Expired(now)); p.AddWithValue("@Id", a.DiscountId!.Value); }),
                "DiscountSuperseded" => ($"UPDATE [{s}].[Discount] SET EndDateUtc = @End WHERE Id = @Id",
                    p => { Dt(p, "@End", a.OldEndDateUtc); p.AddWithValue("@Id", a.DiscountId!.Value); }),
                "ProductFlags" => ($"UPDATE [{s}].[Product] SET HasDiscountsApplied = @Flag, HasDiscountsAppliedStoreIds = @Ids WHERE Id = @Id",
                    p => { p.AddWithValue("@Flag", a.OldHasDiscountsApplied ?? false); p.AddWithValue("@Ids", (object?)a.OldStoreIds ?? DBNull.Value); p.AddWithValue("@Id", a.ProductId!.Value); }),
                _ => throw new InvalidOperationException($"Audit #{a.Id}: unknown change '{a.Change}'."),
            };
            await using var cmd = new SqlCommand(sql, conn, tx);
            bind(cmd.Parameters);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);

        foreach (var a in audits) a.RolledBackUtc = now;
        batch.Status = "RolledBack";
        batch.RolledBackUtc = now;
        await _db.SaveChangesAsync(CancellationToken.None);
        return audits.Count;
    }

    private sealed record LayerRef(int LayerId, string Db, int StoreId, bool HasDisplayAvailabilityTime, bool IsMall);

    private async Task<(ShopConnection Shop, LayerRef Layer)> ShopForLayerAsync(int layerId, CancellationToken ct)
    {
        var layer = await _toolDb.Layers.AsNoTracking().SingleAsync(l => l.Id == layerId, ct);
        var shop = _options.ShopFor(layer.OperationalDatabase, _tool.ConnectionString);
        var hasDisplay = SandboxSchema.LoadSnapshot().Any(r => r.Db == layer.OperationalDatabase && r.Kind == "column"
            && r.Table == "Discount" && r.Name == "DisplayAvailabilityTime");
        return (shop, new LayerRef(layerId, layer.OperationalDatabase, layer.StoreId, hasDisplay,
            string.Equals(layer.Brand, "GjirafaMall", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Why this publish may not run, or null: the kill switch, the layer not enabled, or a canary
    /// without a category.</summary>
    private async Task<string?> StopReasonAsync(int layerId, CancellationToken ct)
    {
        if (_killSwitch is not null && await _killSwitch.GetAsync(ct) is { Stopped: true } ks)
            return $"Stopped by the kill switch ({ks.Reason}).";
        if (!_options.IsLayerEnabled(layerId))
            return _options.EnabledLayers.Count == 0
                ? $"Publisher:Target is {_options.Target} and Publisher:EnabledLayers is empty: no layer may publish."
                : $"Layer {layerId} is not in Publisher:EnabledLayers ({string.Join(", ", _options.EnabledLayers)}).";
        if (_options.Canary.Enabled && string.IsNullOrWhiteSpace(_options.Canary.Category))
            return "Publisher:Canary is on but names no category.";
        return null;
    }

    /// <summary>Records the publish as Stopped with the reason; nothing was written to the shop.</summary>
    private async Task<PublishOutcome> StopAsync(PublishBatch batch, string reason)
    {
        batch.Status = "Stopped";
        batch.Skipped = batch.Requested;
        batch.Note = reason;
        batch.FinishedUtc = Truncate(_clock.GetUtcNow().UtcDateTime);
        await _db.SaveChangesAsync(CancellationToken.None);
        _logger.LogWarning("Publish batch {Batch} for layer {Layer} stopped: {Reason}", batch.Id, batch.LayerId, reason);
        return new PublishOutcome(batch.Id, batch.Status, 0, 0, 0, batch.Skipped, 0, 0, false, Array.Empty<PublishException>());
    }

    /// <summary>The layer's SKUs in the canary category, from the product map (GjirafaMall: main group,
    /// Gjirafa50: L1 category, the holdout's convention).</summary>
    private async Task<HashSet<string>> CanarySkusAsync(LayerRef layer, string category, CancellationToken ct)
    {
        var skus = new HashSet<string>(StringComparer.Ordinal);
        await using var conn = new SqlConnection(_tool.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT Sku FROM hk.Products WHERE LayerId = @L AND Sku IS NOT NULL AND (CASE WHEN @Mall = 1 THEN MainGroup ELSE CategoryL1 END) = @Cat",
            conn) { CommandTimeout = 120 };
        cmd.Parameters.AddWithValue("@L", layer.LayerId);
        cmd.Parameters.AddWithValue("@Mall", layer.IsMall);
        cmd.Parameters.AddWithValue("@Cat", category);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) skus.Add(r.GetString(0).Trim());
        return skus;
    }

    private (DateTime Start, DateTime End) Window(PublishRequest r, DateTime now)
    {
        if (r.Kind == PublishKinds.Regular)
            return (now, now.Date.AddDays(_options.RollingExpiryDays + 1));      // midnight: the same day's re-run is idempotent
        if (r.Kind != PublishKinds.Campaign) throw new ArgumentException($"Unknown publish kind '{r.Kind}'.");
        if (string.IsNullOrWhiteSpace(r.CampaignKey) || r.CampaignStartUtc is not { } start || r.CampaignEndUtc is not { } end || end <= start)
            throw new ArgumentException("A campaign needs a key and a start before its end.");
        return (Truncate(start), Truncate(end));
    }

    private bool IsTeamRegular(string name) =>
        !name.StartsWith(OurPrefix, StringComparison.OrdinalIgnoreCase)
        && _options.TeamRegularNamePatterns.Any(p => Regex.IsMatch(name,
            "^" + Regex.Escape(p).Replace("%", ".*").Replace("_", ".") + "$", RegexOptions.IgnoreCase));

    private static PublishAudit Audit(PublishBatch b, string sku, int productId, string change, DateTime now, Action<PublishAudit> set)
    {
        var a = new PublishAudit { BatchId = b.Id, Sku = sku, ProductId = productId, Change = change, CreatedUtc = now };
        set(a);
        return a;
    }

    private PublishException Exception(PublishBatch b, string sku, string kind, string detail) =>
        new() { BatchId = b.Id, Sku = sku, Kind = kind, Detail = detail.Length > 1000 ? detail[..1000] : detail, CreatedUtc = _clock.GetUtcNow().UtcDateTime };

    /// <summary>nopCommerce treats a discount as live while EndDateUtc >= now, so anything we end, ends a second
    /// before now: inactive at once, also for the round-trip check that follows.</summary>
    private static DateTime Expired(DateTime now) => now.AddSeconds(-1);

    private static DateTime Truncate(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    private static void Dt(SqlParameterCollection p, string name, DateTime? value) =>
        p.Add(name, SqlDbType.DateTime2).Value = (object?)value ?? DBNull.Value;

    private static async Task Exec(SqlConnection conn, SqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task BulkCopy(SqlConnection conn, SqlTransaction? tx, string table, DataTable rows, CancellationToken ct)
    {
        if (rows.Rows.Count == 0) return;
        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx) { DestinationTableName = table, BulkCopyTimeout = 120 };
        foreach (DataColumn c in rows.Columns) bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
        await bulk.WriteToServerAsync(rows, ct);
    }
}
