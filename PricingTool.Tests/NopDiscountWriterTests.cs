using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Storage;
using PricingTool.Agent.Tools;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.NopIntegration;
using PricingTool.NopIntegration.Sandbox;

namespace PricingTool.Tests;

/// <summary>
/// The discount publisher against a real SQL Server sandbox with the exact live DDL (a throwaway LocalDB
/// database): every case of plan section 5 "Proof that it's ready".
/// </summary>
[Collection("NopSandbox")]
public class NopDiscountWriterTests : IClassFixture<LocalDbFixture>, IDisposable
{
    private const string G50 = "shop_sandbox_GjirafaEcommerce";
    private const string Mall = "shop_sandbox_GjirafaMall";
    private static readonly DateTime Now = new(2026, 10, 30, 18, 0, 0, DateTimeKind.Utc);

    private readonly LocalDbFixture _fixture;
    private readonly AgentDbContext _db;
    private readonly PricingToolDbContext _tool;
    private readonly FakeClock _clock = new(Now);
    private static bool _created;

    public NopDiscountWriterTests(LocalDbFixture fixture)
    {
        _fixture = fixture;
        _db = new AgentDbContext(new DbContextOptionsBuilder<AgentDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _tool = new PricingToolDbContext(new DbContextOptionsBuilder<PricingToolDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _tool.Layers.AddRange(
            Layer(1, "GjirafaMall", "KS", "GjirafaMall", 2),
            Layer(4, "Gjirafa50", "KS", "GjirafaEcommerce", 2),
            Layer(6, "Gjirafa50", "AL", "GjirafaEcommerce", 3));
        _tool.SaveChanges();
        if (!LocalDb.Available) return;
        using var conn = _fixture.Open();
        lock (typeof(NopDiscountWriterTests))
        {
            if (!_created || !SchemaExists(conn)) { SandboxSeeder.CreateAsync(conn, SandboxSchema.LoadSnapshot()).GetAwaiter().GetResult(); _created = true; }
        }
        foreach (var s in new[] { G50, Mall })
            foreach (var t in new[] { "Discount_AppliedToProducts", "Discount", "Product" })
                Exec(conn, $"TRUNCATE TABLE [{s}].[{t}]");
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

    private static Layer Layer(int id, string brand, string country, string db, int store) => new()
    {
        Id = id, Brand = brand, CountryCode = country, DisplayName = $"{brand} {country}", OperationalDatabase = db, StoreId = store,
        Currency = "EUR", IsActive = true,
    };

    private NopDiscountWriter Writer(Action<PublisherOptions>? configure = null, IKillSwitch? killSwitch = null)
    {
        var options = new PublisherOptions { ChunkSize = 5000, RollingExpiryDays = 7, RoundTripRollbackPct = 5m };
        configure?.Invoke(options);
        var o = Options.Create(options);
        return new NopDiscountWriter(o, new AgentDataSource(_fixture.ConnectionString), _db, _tool,
            new ShopCacheInvalidator(o, new ConfigurationBuilder().Build()), NullLogger<NopDiscountWriter>.Instance, _clock, killSwitch);
    }

    private sealed class PulledSwitch : IKillSwitch
    {
        public Task<KillSwitchState> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(new KillSwitchState(true, "pulled by manager@test at 2026-10-30 18:00 UTC"));
    }

    /// <summary>The product map the canary reads its category from (Gjirafa50: L1 category).</summary>
    private void MapProducts(params (int Layer, int ProductId, string Sku, string Category)[] rows)
    {
        using var c = _fixture.Open();
        Exec(c, "IF SCHEMA_ID('hk') IS NULL EXEC('CREATE SCHEMA hk');");
        Exec(c, """
            IF OBJECT_ID('hk.Products') IS NULL
                CREATE TABLE hk.Products (LayerId int NOT NULL, ProductId int NOT NULL, Sku nvarchar(400) NULL,
                    CategoryL1 nvarchar(400) NOT NULL, MainGroup nvarchar(100) NOT NULL, PRIMARY KEY (LayerId, ProductId));
            DELETE FROM hk.Products;
            """);
        foreach (var r in rows)
            Exec(c, $"INSERT INTO hk.Products VALUES ({r.Layer}, {r.ProductId}, N'{r.Sku}', N'{r.Category}', N'Others')");
    }

    // ---- sandbox helpers -------------------------------------------------------------------------------

    private static bool SchemaExists(SqlConnection c) =>
        (int)new SqlCommand($"SELECT COUNT(*) FROM sys.tables WHERE SCHEMA_NAME(schema_id) = '{G50}'", c).ExecuteScalar()! > 0;

    private static void Exec(SqlConnection c, string sql)
    {
        using var cmd = new SqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    private void Product(int id, string sku, bool has = false, string? storeIds = null, string schema = G50)
    {
        using var c = _fixture.Open();
        using var cmd = new SqlCommand($"SET IDENTITY_INSERT [{schema}].[Product] ON; INSERT INTO [{schema}].[Product] (Id, Sku, Deleted, HasDiscountsApplied, HasDiscountsAppliedStoreIds) VALUES (@Id, @Sku, 0, @Has, @Ids); SET IDENTITY_INSERT [{schema}].[Product] OFF;", c);
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Sku", sku);
        cmd.Parameters.AddWithValue("@Has", has);
        cmd.Parameters.AddWithValue("@Ids", (object?)storeIds ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>A public amount deal the team made (not ours), mapped to the given products.</summary>
    private int TeamDeal(string name, decimal amount, int[] productIds, int store = 2, DateTime? end = null, string schema = G50)
    {
        using var c = _fixture.Open();
        using var cmd = new SqlCommand($"""
            INSERT INTO [{schema}].[Discount] (Name, DiscountTypeId, UsePercentage, DiscountPercentage, DiscountAmount, StartDateUtc, EndDateUtc,
                RequiresCouponCode, IsCumulative, DiscountLimitationId, LimitationTimes, AppliedToSubCategories, StoreId, DiscountApplyTypeId,
                DiscountPlatformTypeId, IsVendorDiscount{(schema == Mall ? ", DisplayAvailabilityTime" : "")})
            OUTPUT inserted.Id
            VALUES (@Name, 2, 0, 0, @Amount, @Start, @End, 0, 0, 0, 0, 0, @Store, 2, 0, 0{(schema == Mall ? ", 0" : "")})
            """, c);
        cmd.Parameters.AddWithValue("@Name", name);
        cmd.Parameters.AddWithValue("@Amount", amount);
        cmd.Parameters.AddWithValue("@Start", Now.AddDays(-10));
        cmd.Parameters.AddWithValue("@End", end ?? Now.AddDays(20));
        cmd.Parameters.AddWithValue("@Store", store);
        var id = (int)cmd.ExecuteScalar()!;
        foreach (var p in productIds) Exec(c, $"INSERT INTO [{schema}].[Discount_AppliedToProducts] VALUES ({id}, {p})");
        return id;
    }

    private sealed record DiscountRow(int Id, string Name, decimal Amount, DateTime? Start, DateTime? End, int TypeId, bool UsePct, short ApplyType,
        short Platform, bool Coupon, bool Cumulative, int LimitationId, int Times, bool SubCats, bool Vendor, int Store, string? Comment, int Mappings);

    private List<DiscountRow> Discounts(string schema = G50)
    {
        using var c = _fixture.Open();
        using var cmd = new SqlCommand($"""
            SELECT d.Id, d.Name, d.DiscountAmount, d.StartDateUtc, d.EndDateUtc, d.DiscountTypeId, d.UsePercentage, d.DiscountApplyTypeId,
                   d.DiscountPlatformTypeId, d.RequiresCouponCode, d.IsCumulative, d.DiscountLimitationId, d.LimitationTimes,
                   d.AppliedToSubCategories, d.IsVendorDiscount, d.StoreId, d.AdminComment,
                   (SELECT COUNT(*) FROM [{schema}].[Discount_AppliedToProducts] m WHERE m.Discount_Id = d.Id)
            FROM [{schema}].[Discount] d ORDER BY d.Id
            """, c);
        using var r = cmd.ExecuteReader();
        var list = new List<DiscountRow>();
        while (r.Read())
            list.Add(new DiscountRow(r.GetInt32(0), r.GetString(1), r.GetDecimal(2), r.IsDBNull(3) ? null : r.GetDateTime(3), r.IsDBNull(4) ? null : r.GetDateTime(4),
                r.GetInt32(5), r.GetBoolean(6), r.GetInt16(7), r.GetInt16(8), r.GetBoolean(9), r.GetBoolean(10), r.GetInt32(11), r.GetInt32(12),
                r.GetBoolean(13), r.GetBoolean(14), r.GetInt32(15), r.IsDBNull(16) ? null : r.GetString(16), r.GetInt32(17)));
        return list;
    }

    private (bool Has, string? Ids) Flags(int productId, string schema = G50)
    {
        using var c = _fixture.Open();
        using var cmd = new SqlCommand($"SELECT HasDiscountsApplied, HasDiscountsAppliedStoreIds FROM [{schema}].[Product] WHERE Id = {productId}", c);
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetBoolean(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    private static PublishRequest Regular(int layer, params (string Sku, decimal Price)[] items) =>
        new(layer, items.Select(i => new PublishItem(i.Sku, i.Price)).ToList());

    // ---- the cases ---------------------------------------------------------------------------------------

    [LocalDbFact]
    public async Task Create_writes_the_discount_the_mapping_and_the_flags_exactly_as_the_module_does()
    {
        Product(101, "SKU-A");

        var outcome = await Writer().PublishAsync(Regular(4, ("SKU-A", 49.50m)) with { EngineRunId = 1098 });

        Assert.Equal(("Published", 1, 0, 1, 0), (outcome.Status, outcome.Created, outcome.RoundTripMismatches, outcome.RoundTripChecked, outcome.Skipped));
        var d = Assert.Single(Discounts());
        Assert.Equal(("ai-price-4-SKU-A", 49.50m, Now, Now.Date.AddDays(8)), (d.Name, d.Amount, d.Start!.Value, d.End!.Value));
        Assert.Equal((2, false, (short)2, (short)0, false, false, 0, 0, false, false, 2, 1),
            (d.TypeId, d.UsePct, d.ApplyType, d.Platform, d.Coupon, d.Cumulative, d.LimitationId, d.Times, d.SubCats, d.Vendor, d.Store, d.Mappings));
        Assert.Equal($"pricing-tool batch {outcome.BatchId}; run 1098", d.Comment);
        Assert.Equal((true, "2"), Flags(101));
        var audit = await _db.PublishAudits.OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(new[] { "DiscountCreated", "ProductFlags" }, audit.Select(a => a.Change));
        Assert.All(audit, a => Assert.True(a.Applied));
        Assert.Equal(d.Id, audit[0].DiscountId);
        Assert.Equal(((bool?)false, (string?)null), (audit[1].OldHasDiscountsApplied, audit[1].OldStoreIds));
    }

    [LocalDbFact]
    public async Task GjirafaMall_discounts_carry_DisplayAvailabilityTime()
    {
        Product(201, "MALL-1", schema: Mall);

        var outcome = await Writer().PublishAsync(Regular(1, ("MALL-1", 9.99m)));

        Assert.Equal(1, outcome.Created);
        Assert.Equal("ai-price-1-MALL-1", Assert.Single(Discounts(Mall)).Name);
        using var c = _fixture.Open();
        Assert.False((bool)new SqlCommand($"SELECT DisplayAvailabilityTime FROM [{Mall}].[Discount]", c).ExecuteScalar()!);
    }

    // ---- the switches: any one stops the publish before a shop row is touched ---------------------------

    [LocalDbFact]
    public async Task A_pulled_kill_switch_stops_the_publish_and_writes_nothing()
    {
        Product(101, "SKU-A");

        var outcome = await Writer(killSwitch: new PulledSwitch()).PublishAsync(Regular(4, ("SKU-A", 49.50m)));

        Assert.Equal(("Stopped", 0, 1), (outcome.Status, outcome.Created, outcome.Skipped));
        Assert.Empty(Discounts());
        Assert.Equal((false, (string?)null), Flags(101));
        var batch = await _db.PublishBatches.SingleAsync();
        Assert.Contains("kill switch (pulled by manager@test", batch.Note);
        Assert.Empty(await _db.PublishAudits.ToListAsync());
    }

    [LocalDbFact]
    public async Task A_layer_missing_from_EnabledLayers_is_not_published()
    {
        Product(101, "SKU-A");

        var outcome = await Writer(o => o.EnabledLayers = new() { 1 }).PublishAsync(Regular(4, ("SKU-A", 49.50m)));

        Assert.Equal("Stopped", outcome.Status);
        Assert.Empty(Discounts());
        Assert.Contains("Layer 4 is not in Publisher:EnabledLayers (1)", (await _db.PublishBatches.SingleAsync()).Note);
    }

    [Fact]
    public void An_empty_EnabledLayers_means_every_layer_on_the_sandbox_and_none_live()
    {
        Assert.True(new PublisherOptions { Target = PublisherOptions.SandboxTarget }.IsLayerEnabled(4));
        Assert.False(new PublisherOptions { Target = PublisherOptions.LiveTarget }.IsLayerEnabled(4));
        Assert.True(new PublisherOptions { Target = PublisherOptions.LiveTarget, EnabledLayers = new() { 4 } }.IsLayerEnabled(4));
    }

    [LocalDbFact]
    public async Task The_canary_publishes_only_its_category_and_at_most_its_cap()
    {
        Product(301, "CAN-1");
        Product(302, "CAN-2");
        Product(303, "CAN-3");
        MapProducts((4, 301, "CAN-1", "Gaming"), (4, 302, "CAN-2", "Gaming"), (4, 303, "CAN-3", "Laptopë"));

        var outcome = await Writer(o => o.Canary = new CanaryOptions { Enabled = true, Category = "Gaming", MaxSkus = 1 })
            .PublishAsync(Regular(4, ("CAN-1", 9.50m), ("CAN-2", 9.50m), ("CAN-3", 9.50m)));

        Assert.Equal(("Published", 1, 2), (outcome.Status, outcome.Created, outcome.Skipped));
        Assert.Equal("ai-price-4-CAN-1", Assert.Single(Discounts()).Name);
        Assert.Contains("1 outside the category and 1 over the cap left out", (await _db.PublishBatches.SingleAsync()).Note);
    }

    [LocalDbFact]
    public async Task A_canary_without_a_category_stops_the_publish()
    {
        Product(101, "SKU-A");

        var outcome = await Writer(o => o.Canary = new CanaryOptions { Enabled = true }).PublishAsync(Regular(4, ("SKU-A", 49.50m)));

        Assert.Equal("Stopped", outcome.Status);
        Assert.Empty(Discounts());
    }

    [LocalDbFact]
    public async Task A_new_price_updates_the_same_discount_in_place()
    {
        Product(101, "SKU-A");
        await Writer().PublishAsync(Regular(4, ("SKU-A", 49.50m)));
        var first = Assert.Single(Discounts());

        var outcome = await Writer().PublishAsync(Regular(4, ("SKU-A", 47.00m)));

        Assert.Equal((0, 1), (outcome.Created, outcome.Updated));
        var d = Assert.Single(Discounts());
        Assert.Equal((first.Id, 47.00m, 1), (d.Id, d.Amount, d.Mappings));
        var update = await _db.PublishAudits.SingleAsync(a => a.BatchId == outcome.BatchId && a.Change == "DiscountUpdated");
        Assert.Equal((49.50m, 47.00m), (update.OldDiscountAmount!.Value, update.NewDiscountAmount!.Value));
    }

    [LocalDbFact]
    public async Task The_same_publish_twice_the_same_day_changes_nothing()
    {
        Product(101, "SKU-A");
        Product(102, "SKU-B");
        await Writer().PublishAsync(Regular(4, ("SKU-A", 10m), ("SKU-B", 20m)));
        var before = Discounts();
        var audits = await _db.PublishAudits.CountAsync();

        _clock.Now = Now.AddHours(3);
        var again = await Writer().PublishAsync(Regular(4, ("SKU-A", 10m), ("SKU-B", 20m)));

        Assert.Equal((0, 0, 2, 0), (again.Created, again.Updated, again.Unchanged, again.RoundTripMismatches));
        Assert.Equal(before, Discounts());
        Assert.Equal(audits, await _db.PublishAudits.CountAsync());
    }

    [LocalDbFact]
    public async Task Each_publish_renews_the_rolling_expiry()
    {
        Product(101, "SKU-A");
        await Writer().PublishAsync(Regular(4, ("SKU-A", 10m)));

        _clock.Now = Now.AddDays(3);
        var outcome = await Writer().PublishAsync(Regular(4, ("SKU-A", 10m)));

        Assert.Equal(1, outcome.Updated);
        var d = Assert.Single(Discounts());
        Assert.Equal((10m, Now, Now.Date.AddDays(3 + 8)), (d.Amount, d.Start!.Value, d.End!.Value));   // start kept, end moved
    }

    [LocalDbFact]
    public async Task Expire_ends_our_discount_and_never_deletes_it()
    {
        Product(101, "SKU-A");
        await Writer().PublishAsync(Regular(4, ("SKU-A", 10m)));

        _clock.Now = Now.AddHours(1);
        var batch = await Writer().ExpireAsync(4, new[] { "SKU-A" });

        var d = Assert.Single(Discounts());
        Assert.Equal(Now.AddHours(1).AddSeconds(-1), d.End);
        Assert.True((await _db.PublishAudits.SingleAsync(a => a.BatchId == batch)).Applied);
    }

    [LocalDbFact]
    public async Task A_campaign_is_its_own_time_boxed_discount_and_leaves_the_regular_price_alone()
    {
        Product(101, "SKU-A");
        await Writer().PublishAsync(Regular(4, ("SKU-A", 50m)));
        var start = Now.AddDays(5);
        var end = Now.AddDays(8);

        var outcome = await Writer().PublishAsync(new PublishRequest(4, new[] { new PublishItem("SKU-A", 40m) })
        {
            Kind = PublishKinds.Campaign, CampaignKey = "bf26", CampaignStartUtc = start, CampaignEndUtc = end,
        });

        Assert.Equal((1, 0), (outcome.Created, outcome.RoundTripMismatches));             // checked at the campaign's start
        var all = Discounts();
        Assert.Equal(new[] { ("ai-price-4-SKU-A", 50m), ("ai-campaign-bf26-SKU-A", 40m) }, all.Select(d => (d.Name, d.Amount)));
        Assert.Equal((start, end), (all[1].Start!.Value, all[1].End!.Value));
    }

    [LocalDbFact]
    public async Task The_store_is_merged_into_the_flags_and_no_other_store_is_dropped()
    {
        Product(101, "SKU-A", has: true, storeIds: "2");
        Product(102, "SKU-B", has: true, storeIds: "1:3");
        Product(103, "SKU-C", has: true, storeIds: "3");

        await Writer().PublishAsync(Regular(6, ("SKU-A", 10m)));                       // AL layer: store 3
        await Writer().PublishAsync(Regular(4, ("SKU-B", 10m), ("SKU-C", 10m)));      // KS layer: store 2

        Assert.Equal((true, "2:3"), Flags(101));
        Assert.Equal((true, "1:2:3"), Flags(102));
        Assert.Equal((true, "2:3"), Flags(103));
    }

    [LocalDbFact]
    public async Task A_cheaper_deal_we_did_not_create_wins_and_is_left_alone()
    {
        Product(101, "SKU-A");
        var team = TeamDeal("black-friday-25", 45m, new[] { 101 });

        var outcome = await Writer().PublishAsync(Regular(4, ("SKU-A", 49.50m)));

        Assert.Equal((0, 1), (outcome.Created, outcome.Skipped));
        var ex = Assert.Single(outcome.Exceptions);
        Assert.Equal("cheaper-deal", ex.Kind);
        Assert.Contains($"#{team}", ex.Detail);
        Assert.Equal(new[] { "black-friday-25" }, Discounts().Select(d => d.Name));
        Assert.Equal((false, (string?)null), Flags(101));
    }

    [LocalDbFact]
    public async Task Supersede_on_end_dates_a_team_regular_discount_on_that_product_only()
    {
        Product(101, "SKU-A");
        Product(102, "SKU-B");
        Product(103, "SKU-C");
        var regular = TeamDeal("oct-disc-ks-26", 45m, new[] { 101 });
        var shared = TeamDeal("oct-disc-ks-26-bundle", 45m, new[] { 102, 103 });     // covers two products: never superseded
        var campaign = TeamDeal("black-friday-25", 40m, new[] { 103 });

        var outcome = await Writer(o => { o.SupersedeTeamRegularDiscounts = true; o.TeamRegularNamePatterns.Add("%-disc-%"); })
            .PublishAsync(Regular(4, ("SKU-A", 49m), ("SKU-B", 49m), ("SKU-C", 49m)));

        Assert.Equal(1, outcome.Created);
        var d = Discounts().ToDictionary(x => x.Id);
        Assert.Equal(Now.AddSeconds(-1), d[regular].End);                              // ended: ours applies to SKU-A now
        Assert.Equal(Now.AddDays(20), d[shared].End);
        Assert.Equal(Now.AddDays(20), d[campaign].End);
        Assert.Equal(new[] { "SKU-B", "SKU-C" }, outcome.Exceptions.Select(e => e.Sku).Order());
        var sup = await _db.PublishAudits.SingleAsync(a => a.Change == "DiscountSuperseded");
        Assert.Equal((regular, Now.AddDays(20)), (sup.DiscountId!.Value, sup.OldEndDateUtc!.Value));
    }

    [LocalDbFact]
    public async Task Supersede_off_leaves_the_team_regular_discount_and_skips_the_sku()
    {
        Product(101, "SKU-A");
        var regular = TeamDeal("oct-disc-ks-26", 45m, new[] { 101 });

        var outcome = await Writer(o => o.TeamRegularNamePatterns.Add("%-disc-%")).PublishAsync(Regular(4, ("SKU-A", 49m)));

        Assert.Equal((0, "cheaper-deal"), (outcome.Created, outcome.Exceptions.Single().Kind));
        Assert.Equal(Now.AddDays(20), Discounts().Single(x => x.Id == regular).End);
    }

    [LocalDbFact]
    public async Task Rollback_restores_every_row_exactly_and_expires_what_it_created()
    {
        Product(101, "SKU-A", has: true, storeIds: "1");
        Product(102, "SKU-B");
        await Writer().PublishAsync(Regular(4, ("SKU-A", 30m)));
        var original = Discounts().Single();

        _clock.Now = Now.AddDays(1);
        var batch = await Writer().PublishAsync(Regular(4, ("SKU-A", 28m), ("SKU-B", 15m)));
        Assert.Equal((1, 1), (batch.Created, batch.Updated));

        _clock.Now = Now.AddDays(1).AddHours(1);
        var restored = await Writer().RollbackAsync(batch.BatchId);

        Assert.Equal(3, restored);                                                       // A's update, B's creation, B's flags
        var after = Discounts().ToDictionary(d => d.Name);
        Assert.Equal((original.Amount, original.Start, original.End), (after["ai-price-4-SKU-A"].Amount, after["ai-price-4-SKU-A"].Start, after["ai-price-4-SKU-A"].End));
        Assert.Equal(Now.AddDays(1).AddHours(1).AddSeconds(-1), after["ai-price-4-SKU-B"].End);   // expired, still there
        Assert.Equal((false, (string?)null), Flags(102));
        Assert.Equal((true, "1:2"), Flags(101));                                         // A's flags came from the first batch
        Assert.Equal("RolledBack", (await _db.PublishBatches.SingleAsync(b => b.Id == batch.BatchId)).Status);
    }

    [LocalDbFact]
    public async Task An_unknown_sku_is_reported_and_the_rest_is_published()
    {
        Product(101, "SKU-A");

        var outcome = await Writer().PublishAsync(Regular(4, ("SKU-A", 10m), ("NO-SUCH-SKU", 10m)));

        Assert.Equal((1, 1), (outcome.Created, outcome.Skipped));
        Assert.Equal(("NO-SUCH-SKU", "unresolved-sku"), (outcome.Exceptions.Single().Sku, outcome.Exceptions.Single().Kind));
    }

    [LocalDbFact]
    public async Task A_price_at_or_above_the_shelf_price_is_not_written_as_a_discount()
    {
        Product(101, "SKU-A");

        var outcome = await Writer().PublishAsync(new PublishRequest(4, new[] { new PublishItem("SKU-A", 60m, ShelfPrice: 59.5m) }));

        Assert.Equal("at-or-above-shelf", outcome.Exceptions.Single().Kind);
        Assert.Empty(Discounts());
    }

    [LocalDbFact]
    public async Task A_failure_in_the_middle_of_a_chunk_rolls_that_chunk_back_and_keeps_the_others()
    {
        foreach (var (id, sku) in new[] { (101, "SKU-A"), (102, "SKU-B"), (103, "SKU-C"), (104, "SKU-D") }) Product(id, sku);
        var writer = Writer(o => o.ChunkSize = 2);
        writer.BeforeChunkCommit = chunk => chunk == 2 ? throw new InvalidOperationException("connection lost (test)") : Task.CompletedTask;

        var outcome = await writer.PublishAsync(Regular(4, ("SKU-A", 10m), ("SKU-B", 11m), ("SKU-C", 12m), ("SKU-D", 13m)));

        Assert.Equal(new[] { "ai-price-4-SKU-A", "ai-price-4-SKU-B" }, Discounts().Select(d => d.Name));
        Assert.Equal((false, (string?)null), Flags(103));
        Assert.Equal(new[] { "SKU-C", "SKU-D" }, outcome.Exceptions.Where(e => e.Kind == "chunk-failed").Select(e => e.Sku));
        Assert.All(await _db.PublishAudits.Where(a => a.Sku == "SKU-C" || a.Sku == "SKU-D").ToListAsync(), a => Assert.False(a.Applied));
        Assert.Equal((2, 2, 0), (outcome.Created, outcome.RoundTripChecked, outcome.RoundTripMismatches));
    }

    [LocalDbFact]
    public async Task Too_many_prices_that_do_not_read_back_roll_the_batch_back_automatically()
    {
        Product(101, "SKU-A");
        Product(102, "SKU-B");
        var writer = Writer();
        // A cheaper deal lands on SKU-A between our decision and our commit (as a parallel push would).
        writer.BeforeChunkCommit = _ => { TeamDeal("late-push", 5m, new[] { 101 }); return Task.CompletedTask; };

        var outcome = await writer.PublishAsync(Regular(4, ("SKU-A", 10m), ("SKU-B", 20m)));

        Assert.True(outcome.RolledBack);
        Assert.Equal(("RolledBack", 1), (outcome.Status, outcome.RoundTripMismatches));
        Assert.Contains(outcome.Exceptions, e => e.Kind == "round-trip" && e.Sku == "SKU-A" && e.Detail.Contains("late-push"));
        Assert.All(Discounts().Where(d => d.Name.StartsWith("ai-")), d => Assert.Equal(Now.AddSeconds(-1), d.End));   // ours expired again
    }
}
