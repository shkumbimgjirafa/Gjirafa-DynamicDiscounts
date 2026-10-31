using Microsoft.Data.SqlClient;
using PricingTool.NopIntegration.Sandbox;

namespace PricingTool.Tests;

/// <summary>
/// The schema contract: the shop sandbox must have exactly the live discount DDL (the snapshot committed in
/// PricingTool.NopIntegration/Sandbox/live-discount-schema.json). Built in a throwaway database, then the
/// real sandbox in PricingToolHackathon is checked against the same snapshot.
/// </summary>
public class SandboxContractTests : IClassFixture<LocalDbFixture>
{
    private readonly LocalDbFixture _db;
    private static readonly IReadOnlyList<LiveSchemaRow> Snapshot = SandboxSchema.LoadSnapshot();

    public SandboxContractTests(LocalDbFixture db) => _db = db;

    [Fact]
    public void The_snapshot_has_both_shops_with_the_live_discount_columns()
    {
        Assert.Equal(21, Snapshot.Count(r => r is { Db: "GjirafaEcommerce", Kind: "column", Table: "Discount" }));
        Assert.Equal(22, Snapshot.Count(r => r is { Db: "GjirafaMall", Kind: "column", Table: "Discount" }));
        Assert.Contains(Snapshot, r => r is { Db: "GjirafaMall", Table: "Discount", Name: "DisplayAvailabilityTime" });
        Assert.Contains(Snapshot, r => r is { Kind: "column", Table: "Product", Name: "HasDiscountsAppliedStoreIds", MaxLength: 40 });
    }

    [Fact]
    public void The_snapshot_has_each_shops_tier_price_table_with_its_own_decimal_types()
    {
        foreach (var db in new[] { "GjirafaMall", "GjirafaEcommerce" })
        {
            Assert.Equal(10, Snapshot.Count(r => r.Db == db && r is { Kind: "column", Table: "TierPrice" }));
            Assert.Contains(Snapshot, r => r.Db == db && r is { Kind: "index", Table: "TierPrice", Name: "PK_TierPrice", IndexColumns: "Id" });
            Assert.Contains(Snapshot, r => r.Db == db && r is { Kind: "column", Table: "Product", Name: "HasTierPrices", IsNullable: false });
        }
        Assert.Contains(Snapshot, r => r is { Db: "GjirafaMall", Table: "TierPrice", Name: "Price", Precision: 18, Scale: 4 });
        Assert.Contains(Snapshot, r => r is { Db: "GjirafaEcommerce", Table: "TierPrice", Name: "Price", Precision: 18, Scale: 2 });
        Assert.Contains(Snapshot, r => r is { Db: "GjirafaMall", Kind: "index", Name: "IX_TierPrice_StoreId_ProductId", IndexColumns: "StoreId,ProductId,Price (incl),OldPrice (incl)" });
        Assert.Contains(Snapshot, r => r is { Db: "GjirafaEcommerce", Kind: "index", Name: "IX_TierPrice_CustomerRoleId" });
    }

    [Theory]
    [InlineData("nvarchar", -1, null, null, "[X] nvarchar(max) NULL")]
    [InlineData("nvarchar", 400, 0, 0, "[X] nvarchar(400) NULL")]
    [InlineData("decimal", 9, 18, 4, "[X] decimal(18,4) NULL")]
    [InlineData("datetime2", 8, 27, 7, "[X] datetime2(7) NULL")]
    [InlineData("smallint", 2, 5, 0, "[X] smallint NULL")]
    public void Columns_are_rendered_exactly(string type, int? maxLength, int? precision, int? scale, string expected)
    {
        Assert.Equal(expected, SandboxSchema.ColumnDefinition(new LiveSchemaRow("D", "column", "T", 1, "X", type, maxLength, precision, scale, true, false, null)));
    }

    [Fact]
    public void Index_columns_split_into_keys_and_included()
    {
        const string ix = "StoreId,EndDateUtc,Name (incl),Id (incl)";
        Assert.Equal("[StoreId], [EndDateUtc]", SandboxSchema.KeyList(ix));
        Assert.Equal("[Name], [Id]", SandboxSchema.IncludedList(ix));
    }

    [LocalDbFact]
    public async Task A_sandbox_built_from_the_snapshot_matches_it_and_a_changed_column_is_caught()
    {
        await using var conn = _db.Open();
        await SandboxSeeder.CreateAsync(conn, Snapshot);

        foreach (var db in new[] { "GjirafaMall", "GjirafaEcommerce" })
            Assert.Empty(SandboxContract.Differences(Snapshot, await SandboxContract.ReadActualAsync(conn, db), db));

        await using (var alter = new SqlCommand("ALTER TABLE [shop_sandbox_GjirafaEcommerce].[Discount] ALTER COLUMN [Name] nvarchar(200) NOT NULL;" +
                                                "DROP INDEX [IX_Discount_CouponCode_Dates_Active] ON [shop_sandbox_GjirafaMall].[Discount];", conn))
            await alter.ExecuteNonQueryAsync();
        Assert.Contains("GjirafaEcommerce.Discount.Name: length 200, live 400",
            SandboxContract.Differences(Snapshot, await SandboxContract.ReadActualAsync(conn, "GjirafaEcommerce"), "GjirafaEcommerce"));
        Assert.Contains("GjirafaMall.Discount: index IX_Discount_CouponCode_Dates_Active missing",
            SandboxContract.Differences(Snapshot, await SandboxContract.ReadActualAsync(conn, "GjirafaMall"), "GjirafaMall"));

        await using (var alter = new SqlCommand("ALTER TABLE [shop_sandbox_GjirafaEcommerce].[TierPrice] ALTER COLUMN [Price] decimal(18,4) NOT NULL;", conn))
            await alter.ExecuteNonQueryAsync();
        Assert.Contains("GjirafaEcommerce.TierPrice.Price: precision/scale 18,4, live 18,2",
            SandboxContract.Differences(Snapshot, await SandboxContract.ReadActualAsync(conn, "GjirafaEcommerce"), "GjirafaEcommerce"));
    }

    [LocalDbFact]
    public async Task Adding_the_price_tables_to_an_older_sandbox_keeps_its_products_and_matches_the_snapshot()
    {
        await using var conn = _db.Open();
        var discountsOnly = Snapshot.Where(r => r.Table != "TierPrice" && r.Name != "HasTierPrices").ToList();
        await SandboxSeeder.CreateAsync(conn, discountsOnly);
        await using (var seed = new SqlCommand("""
            SET IDENTITY_INSERT [shop_sandbox_GjirafaMall].[Product] ON;
            INSERT INTO [shop_sandbox_GjirafaMall].[Product] (Id, Sku, Deleted, HasDiscountsApplied, HasDiscountsAppliedStoreIds) VALUES (7, N'M-7', 0, 1, N'2'), (9, N'M-9', 0, 0, NULL);
            SET IDENTITY_INSERT [shop_sandbox_GjirafaMall].[Product] OFF;
            IF SCHEMA_ID('hk') IS NULL EXEC('CREATE SCHEMA hk');
            DROP TABLE IF EXISTS hk.TierPricesKs;
            CREATE TABLE hk.TierPricesKs (Db varchar(30) NOT NULL, TierPriceId int NOT NULL, ProductId int NOT NULL, StoreId int NOT NULL, CustomerRoleId int NULL,
                Quantity int NOT NULL, Price decimal(18,4) NOT NULL, OldPrice decimal(18,4) NOT NULL, ProfitMargin decimal(6,4) NULL);
            INSERT INTO hk.TierPricesKs VALUES ('GjirafaMall', 501, 7, 2, NULL, 0, 19.99, 24.99, 1.1);
            """, conn))
            await seed.ExecuteNonQueryAsync();

        var results = await SandboxSeeder.AddPricesAsync(conn, Snapshot);

        Assert.Equal((2, 1), (results.Single(r => r.Db == "GjirafaMall").Products, results.Single(r => r.Db == "GjirafaMall").TierPrices));
        foreach (var db in new[] { "GjirafaMall", "GjirafaEcommerce" })
            Assert.Empty(SandboxContract.Differences(Snapshot, await SandboxContract.ReadActualAsync(conn, db), db));
        await using var read = new SqlCommand("SELECT Id, HasTierPrices, HasDiscountsApplied, HasDiscountsAppliedStoreIds FROM [shop_sandbox_GjirafaMall].[Product] ORDER BY Id", conn);
        var rows = new List<(int, bool, bool, string?)>();
        await using (var r = await read.ExecuteReaderAsync())
            while (await r.ReadAsync()) rows.Add((r.GetInt32(0), r.GetBoolean(1), r.GetBoolean(2), r.IsDBNull(3) ? null : r.GetString(3)));
        Assert.Equal(new[] { (7, true, true, (string?)"2"), (9, false, false, null) }, rows);
        await using var tier = new SqlCommand("SELECT Id, Price, OldPrice FROM [shop_sandbox_GjirafaMall].[TierPrice]", conn);
        await using var t = await tier.ExecuteReaderAsync();
        Assert.True(await t.ReadAsync());
        Assert.Equal((501, 19.99m, 24.99m), (t.GetInt32(0), t.GetDecimal(1), t.GetDecimal(2)));
    }

    [LocalDbFact]
    public async Task The_real_sandbox_in_the_hackathon_database_matches_the_live_snapshot()
    {
        await using var conn = new SqlConnection(LocalDb.Hackathon);
        await conn.OpenAsync();
        await using var exists = new SqlCommand("SELECT COUNT(*) FROM sys.tables WHERE SCHEMA_NAME(schema_id) LIKE 'shop[_]sandbox[_]%'", conn);
        if ((int)(await exists.ExecuteScalarAsync())! == 0) return;   // not built on this machine yet

        foreach (var db in new[] { "GjirafaMall", "GjirafaEcommerce" })
            Assert.Empty(SandboxContract.Differences(Snapshot, await SandboxContract.ReadActualAsync(conn, db), db));
    }
}
