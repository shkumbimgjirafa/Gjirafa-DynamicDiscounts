using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PricingTool.Agent.Storage;
using PricingTool.Agent.Tools;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.NopIntegration;
using PricingTool.NopIntegration.Sandbox;

namespace PricingTool.Tests;

/// <summary>
/// The sandbox mirror (Prompt 10 part B) against a throwaway LocalDB database: a schema with the live DDL stands in for the
/// live shop. The live shop's other discounts, mappings, flags and regular prices come in; ours never change; a deal that leaves
/// the live shop is end-dated, not deleted; mirror → publish → rollback leaves the mirrored rows as mirrored; retention.
/// </summary>
[Collection("NopSandbox")]
public class SandboxMirrorTests : IClassFixture<LocalDbFixture>, IDisposable
{
    private const string Db = "GjirafaEcommerce";
    private const string Box = "shop_sandbox_GjirafaEcommerce";
    private const string Live = "mirror_live_GjirafaEcommerce";
    private const long Run = 1109;
    private static readonly DateTime Now = new(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc);

    private readonly LocalDbFixture _fixture;
    private readonly AgentDbContext _db;
    private readonly PricingToolDbContext _tool;
    private readonly FakeClock _clock = new(Now);

    public SandboxMirrorTests(LocalDbFixture fixture)
    {
        _fixture = fixture;
        _db = new AgentDbContext(new DbContextOptionsBuilder<AgentDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _tool = new PricingToolDbContext(new DbContextOptionsBuilder<PricingToolDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _tool.Layers.Add(new Layer { Id = 4, Brand = "Gjirafa50", CountryCode = "KS", DisplayName = "Gjirafa50 KS", OperationalDatabase = Db, StoreId = 2, Currency = "EUR", IsActive = true });
        foreach (var (product, sku) in new[] { (101, "SKU-A"), (102, "SKU-B"), (103, "SKU-C") })
            _tool.ProposedPrices.Add(new ProposedPrice { PricingRunId = Run, ProductId = product, Sku = sku });
        _tool.SaveChanges();
        if (!LocalDb.Available) return;
        using var c = _fixture.Open();
        var snapshot = SandboxSchema.LoadSnapshot();
        if ((int)new SqlCommand($"SELECT COUNT(*) FROM sys.tables WHERE SCHEMA_NAME(schema_id) = '{Box}'", c).ExecuteScalar()! == 0)
        {
            SandboxSeeder.CreateAsync(c, snapshot).GetAwaiter().GetResult();
            Exec(c, $"IF SCHEMA_ID('{Live}') IS NULL EXEC('CREATE SCHEMA [{Live}]');");
            foreach (var t in SandboxSchema.Tables)
                foreach (var statement in SandboxSchema.TableStatements(snapshot, Db, t, Live)) Exec(c, statement);
        }
        Exec(c, $"IF OBJECT_ID('[{Box}].[{SandboxMirror.MapTable}]') IS NOT NULL DROP TABLE [{Box}].[{SandboxMirror.MapTable}];");
        foreach (var s in new[] { Box, Live })
            foreach (var t in SandboxSchema.Tables) Exec(c, $"TRUNCATE TABLE [{s}].[{t}]");
    }

    public void Dispose()
    {
        _db.Dispose();
        _tool.Dispose();
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now;
        public FakeClock(DateTime now) => Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private SandboxMirror Mirror() => new(new ConfigurationBuilder().Build(), Options.Create(new PublisherOptions()), new AgentDataSource(_fixture.ConnectionString),
        _db, _tool, NullLogger<SandboxMirror>.Instance, _clock, new MirrorSource(_fixture.ConnectionString, _ => $"[{Live}]."));

    private NopDiscountWriter Writer(Action<PublisherOptions>? configure = null)
    {
        var options = new PublisherOptions { ChunkSize = 5000, RollingExpiryDays = 7, RoundTripRollbackPct = 5m };
        configure?.Invoke(options);
        var o = Options.Create(options);
        return new NopDiscountWriter(o, new AgentDataSource(_fixture.ConnectionString), _db, _tool,
            new ShopCacheInvalidator(o, new ConfigurationBuilder().Build()), NullLogger<NopDiscountWriter>.Instance, _clock);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------

    private static void Exec(SqlConnection c, string sql)
    {
        using var cmd = new SqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    private void Exec(string sql)
    {
        using var c = _fixture.Open();
        Exec(c, sql);
    }

    private T Scalar<T>(string sql)
    {
        using var c = _fixture.Open();
        return (T)new SqlCommand(sql, c).ExecuteScalar()!;
    }

    private void Product(string schema, int id, string sku, bool has = false, string? storeIds = null) =>
        Exec($"SET IDENTITY_INSERT [{schema}].[Product] ON; INSERT INTO [{schema}].[Product] (Id, Sku, HasTierPrices, Deleted, HasDiscountsApplied, HasDiscountsAppliedStoreIds) " +
             $"VALUES ({id}, N'{sku}', 0, 0, {(has ? 1 : 0)}, {(storeIds is null ? "NULL" : $"N'{storeIds}'")}); SET IDENTITY_INSERT [{schema}].[Product] OFF;");

    /// <summary>A discount row; with <paramref name="id"/> it keeps that id (the live shop's own ids).</summary>
    private int Discount(string schema, string name, decimal amount, int[] products, DateTime? start = null, DateTime? end = null, int? id = null)
    {
        using var c = _fixture.Open();
        var cols = "Name, DiscountTypeId, UsePercentage, DiscountPercentage, DiscountAmount, StartDateUtc, EndDateUtc, RequiresCouponCode, IsCumulative, " +
                   "DiscountLimitationId, LimitationTimes, AppliedToSubCategories, StoreId, DiscountApplyTypeId, DiscountPlatformTypeId, IsVendorDiscount";
        using var cmd = new SqlCommand(
            (id is null ? "" : $"SET IDENTITY_INSERT [{schema}].[Discount] ON; ") +
            $"INSERT INTO [{schema}].[Discount] ({(id is null ? "" : "Id, ")}{cols}) OUTPUT inserted.Id " +
            $"VALUES ({(id is null ? "" : $"{id}, ")}@Name, 2, 0, 0, @Amount, @Start, @End, 0, 0, 0, 0, 0, 2, 2, 0, 0);" +
            (id is null ? "" : $" SET IDENTITY_INSERT [{schema}].[Discount] OFF;"), c);
        cmd.Parameters.AddWithValue("@Name", name);
        cmd.Parameters.AddWithValue("@Amount", amount);
        cmd.Parameters.AddWithValue("@Start", start ?? Now.AddDays(-10));
        cmd.Parameters.AddWithValue("@End", end ?? Now.AddDays(20));
        var newId = (int)cmd.ExecuteScalar()!;
        foreach (var p in products) Exec(c, $"INSERT INTO [{schema}].[Discount_AppliedToProducts] VALUES ({newId}, {p})");
        return newId;
    }

    private int Tier(string schema, int productId, decimal price, decimal oldPrice)
    {
        using var c = _fixture.Open();
        return (int)new SqlCommand($"INSERT INTO [{schema}].[TierPrice] (CustomerRoleId, ProductId, StoreId, Quantity, Price, OldPrice) OUTPUT inserted.Id " +
                                   $"VALUES (NULL, {productId}, 2, 0, {price}, {oldPrice})", c).ExecuteScalar()!;
    }

    private sealed record Row(int Id, string Name, decimal Amount, DateTime? Start, DateTime? End, string Products);

    private List<Row> Rows()
    {
        using var c = _fixture.Open();
        using var cmd = new SqlCommand($"""
            SELECT d.Id, d.Name, d.DiscountAmount, d.StartDateUtc, d.EndDateUtc,
                   ISNULL((SELECT STRING_AGG(CAST(m.Product_Id AS nvarchar(10)), ',') WITHIN GROUP (ORDER BY m.Product_Id)
                           FROM [{Box}].[Discount_AppliedToProducts] m WHERE m.Discount_Id = d.Id), N'')
            FROM [{Box}].[Discount] d ORDER BY d.Id
            """, c);
        using var r = cmd.ExecuteReader();
        var rows = new List<Row>();
        while (r.Read())
            rows.Add(new Row(r.GetInt32(0), r.GetString(1), r.GetDecimal(2), r.IsDBNull(3) ? null : r.GetDateTime(3), r.IsDBNull(4) ? null : r.GetDateTime(4), r.GetString(5)));
        return rows;
    }

    private (decimal Price, decimal Old) BoxTier(int productId)
    {
        using var c = _fixture.Open();
        using var r = new SqlCommand($"SELECT TOP 1 Price, OldPrice FROM [{Box}].[TierPrice] WHERE ProductId = {productId} AND StoreId = 2 ORDER BY Id DESC", c).ExecuteReader();
        Assert.True(r.Read());
        return (r.GetDecimal(0), r.GetDecimal(1));
    }

    private (bool Has, string? Ids) Flags(int productId)
    {
        using var c = _fixture.Open();
        using var r = new SqlCommand($"SELECT HasDiscountsApplied, HasDiscountsAppliedStoreIds FROM [{Box}].[Product] WHERE Id = {productId}", c).ExecuteReader();
        Assert.True(r.Read());
        return (r.GetBoolean(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    private void Audit(int? productId, string change, int? discountId = null)
    {
        if (!_db.PublishBatches.Any(b => b.Id == 1))
            _db.PublishBatches.Add(new PublishBatch { Id = 1, LayerId = 4, Db = Db, StoreId = 2, Target = "Sandbox", ShopSchema = Box, Kind = PublishKinds.Regular, Status = "Published", StartedUtc = Now.AddDays(-1) });
        _db.PublishAudits.Add(new PublishAudit { BatchId = 1, Sku = "x", ProductId = productId, Change = change, DiscountId = discountId, Applied = true, CreatedUtc = Now.AddDays(-1) });
        _db.SaveChanges();
    }

    // ---- the cases ---------------------------------------------------------------------------------------------------------

    [LocalDbFact]
    public async Task The_live_shops_deals_flags_and_prices_come_in_and_ours_never_change()
    {
        Product(Box, 101, "SKU-A", has: true, storeIds: "2");
        Product(Box, 102, "SKU-B");
        var ours = Discount(Box, "ai-price-4-SKU-A", 49m, new[] { 101 });
        Tier(Box, 101, 50m, 0m);
        Tier(Box, 102, 50m, 0m);
        Audit(101, "TierPriceUpdated");                                                              // the publisher wrote 101's regular price
        Product(Live, 101, "SKU-A", has: true, storeIds: "1:3");
        Product(Live, 102, "SKU-B", has: true, storeIds: "2");
        Product(Live, 103, "SKU-C", has: true, storeIds: "2");
        Discount(Live, "nov-deals-26-SKU-A", 45m, new[] { 101 }, id: ours);                          // the live shop's own id, the same as ours
        Discount(Live, "techtober-2026", 40m, new[] { 102, 103 }, id: 500);
        Discount(Live, "sept-disc-26-SKU-B", 41m, new[] { 102 }, end: Now.AddDays(-1), id: 600);    // ended: not mirrored
        Tier(Live, 101, 60m, 0m);
        Tier(Live, 102, 55m, 60m);
        Tier(Live, 103, 30m, 0m);

        var r = await Mirror().RunAsync(4, Run);

        var rows = Rows();
        Assert.Equal(new Row(ours, "ai-price-4-SKU-A", 49m, Now.AddDays(-10), Now.AddDays(20), "101"), rows.Single(x => x.Id == ours));
        var nov = rows.Single(x => x.Name == "nov-deals-26-SKU-A");
        Assert.NotEqual(ours, nov.Id);                                                               // a new sandbox id, mapped to the live one
        Assert.Equal((45m, "101"), (nov.Amount, nov.Products));
        Assert.Equal("102,103", rows.Single(x => x.Name == "techtober-2026").Products);
        Assert.DoesNotContain(rows, x => x.Name.StartsWith("sept-disc", StringComparison.Ordinal));
        Assert.Equal(2, Scalar<int>($"SELECT COUNT(*) FROM [{Box}].[{SandboxMirror.MapTable}]"));     // ours isn't in the map
        Assert.Equal((true, "1:2:3"), Flags(101));                                                   // merged: the publisher's store 2 stays
        Assert.Equal((true, "2"), Flags(102));
        Assert.Equal((true, "2"), Flags(103));                                                       // a product the sandbox lacked
        Assert.Equal((50m, 0m), BoxTier(101));                                                       // the publisher's regular price stays
        Assert.Equal((55m, 60m), BoxTier(102));
        Assert.Equal((30m, 0m), BoxTier(103));
        Assert.Equal((2, 2, 0, 1, 1, 1, 1), (r.LiveDiscounts, r.Inserted, r.Updated, r.ProductsAdded, r.TierRefreshed, r.TierInserted, r.TierOurs));

        var again = await Mirror().RunAsync(4, Run);                                                 // the next day, nothing changed live
        Assert.Equal((0, 0, 0, 0, 0), (again.Inserted, again.Updated, again.MappingsAdded, again.TierRefreshed, again.EndDated));
        Assert.Equal(rows, Rows());
    }

    [LocalDbFact]
    public async Task A_team_deal_that_leaves_the_live_shop_is_end_dated_not_deleted()
    {
        Product(Box, 102, "SKU-B");
        Product(Live, 102, "SKU-B");
        Discount(Live, "techtober-2026", 40m, new[] { 102 }, id: 500);
        Discount(Live, "weekend-deals-oct-2026", 42m, new[] { 102 }, id: 501);
        await Mirror().RunAsync(4, Run);

        Exec($"UPDATE [{Live}].[Discount] SET EndDateUtc = '{Now.AddHours(1):yyyy-MM-ddTHH:mm:ss}' WHERE Id = 500;" +
             $"DELETE FROM [{Live}].[Discount_AppliedToProducts] WHERE Discount_Id = 501; DELETE FROM [{Live}].[Discount] WHERE Id = 501;");
        _clock.Now = Now.AddHours(2);
        var r = await Mirror().RunAsync(4, Run);

        var rows = Rows();
        Assert.Equal(2, rows.Count);                                                                 // both kept
        Assert.Equal(Now.AddHours(1), rows.Single(x => x.Name == "techtober-2026").End);             // as the live shop has it
        Assert.Equal(Now.AddHours(2), rows.Single(x => x.Name == "weekend-deals-oct-2026").End);     // gone from the live shop: ended now
        Assert.Equal(2, r.EndDated);
    }

    [LocalDbFact]
    public async Task Mirror_then_publish_then_rollback_leaves_the_mirrored_rows_as_mirrored()
    {
        Product(Box, 101, "SKU-A");
        Product(Box, 102, "SKU-B");
        Tier(Box, 101, 55m, 0m);
        Tier(Box, 102, 55m, 0m);
        Product(Live, 101, "SKU-A");
        Product(Live, 102, "SKU-B");
        Discount(Live, "oct-deals-26-SKU-A", 45m, new[] { 101 }, id: 700);                          // the calculator's own earlier push
        Discount(Live, "techtober-2026", 40m, new[] { 102 }, id: 701);                               // a team deal
        _tool.Approvals.Add(new Approval { Id = 32, LayerId = 4, ApprovedUtc = Now.AddDays(-24), ApprovedBy = "team", CreatedUtc = Now.AddDays(-24) });
        _tool.ApprovalItems.Add(new ApprovalItem { ApprovalId = 32, Sku = "SKU-A", OldPrice = 50m, NewPrice = 45m });
        _tool.SaveChanges();
        await Mirror().RunAsync(4, Run);
        var mirrored = Rows();

        var writer = Writer(o => o.SupersedeTeamRegularDiscounts = true);
        var outcome = await writer.PublishAsync(new PublishRequest(4, new List<PublishItem> { new("SKU-A", 49m), new("SKU-B", 49m) }));
        Assert.Equal(Now.AddSeconds(-1), Rows().Single(x => x.Name == "oct-deals-26-SKU-A").End);    // superseded: ours applies to SKU-A
        Assert.Equal("cheaper-deal", Assert.Single(outcome.Exceptions).Kind);                         // the team deal is left as it is

        await writer.RollbackAsync(outcome.BatchId);

        Assert.Equal(mirrored, Rows().Where(x => !x.Name.StartsWith("ai-", StringComparison.Ordinal)).ToList());
        Assert.Equal(0, (await Mirror().RunAsync(4, Run)).Updated);                                   // and the next mirror finds nothing to change
    }

    [LocalDbFact]
    public async Task Retention_deletes_only_mirrored_copies_ended_over_14_days_ago_that_no_audit_names()
    {
        Product(Box, 101, "SKU-A");
        var old = Discount(Box, "aug-disc-26-SKU-A", 40m, new[] { 101 }, end: Now.AddDays(-20));
        var named = Discount(Box, "sept-disc-26-SKU-A", 41m, new[] { 101 }, end: Now.AddDays(-20));
        var recent = Discount(Box, "oct-disc-26-SKU-A", 42m, new[] { 101 }, end: Now.AddDays(-5));
        var ours = Discount(Box, "ai-price-4-SKU-A", 49m, new[] { 101 }, end: Now.AddDays(-20));
        Audit(101, "DiscountSuperseded", named);

        var r = await Mirror().RunAsync(4, Run);

        Assert.Equal(1, r.Deleted);
        Assert.Equal(new[] { named, recent, ours }, Rows().Select(x => x.Id));
        Assert.Equal(0, Scalar<int>($"SELECT COUNT(*) FROM [{Box}].[Discount_AppliedToProducts] WHERE Discount_Id = {old}"));
    }
}
