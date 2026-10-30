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
