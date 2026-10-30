using Microsoft.Data.SqlClient;
using PricingTool.Agent.Checker;
using PricingTool.Agent.Storage;
using PricingTool.Agent.Tools;

namespace PricingTool.Tests;

/// <summary>
/// hk.ProductForProposal against real SQL Server. The cases that motivated it (GjirafaMall KS run #1101): a Mall
/// row synced from Gjirafa50 carries the Gjirafa50 product id, which is either missing from the Mall map or,
/// because the two shop databases number products independently, the id of an unrelated Mall product.
/// </summary>
public class ProductMapSqlTests : IClassFixture<LocalDbFixture>
{
    private const int Mall = 1, Gj50 = 4;
    private const long Run = 1101;
    private readonly LocalDbFixture _db;

    public ProductMapSqlTests(LocalDbFixture db) => _db = db;

    private void Seed()
    {
        LocalDbFixture.Exec(_db.ConnectionString, "IF SCHEMA_ID('hk') IS NULL EXEC('CREATE SCHEMA hk');");
        LocalDbFixture.Exec(_db.ConnectionString, "IF SCHEMA_ID('PricingTool') IS NULL EXEC('CREATE SCHEMA PricingTool');");
        LocalDbFixture.Exec(_db.ConnectionString, """
            DROP TABLE IF EXISTS hk.Products, PricingTool.ProposedPrices, PricingTool.Layers;
            CREATE TABLE hk.Products (
                LayerId int NOT NULL, ProductId int NOT NULL, Sku nvarchar(400) NULL, Brand nvarchar(400) NULL,
                CategoryL1 nvarchar(400) NOT NULL, CategoryL2 nvarchar(400) NULL, MainGroup nvarchar(100) NOT NULL,
                IsGj50Listed bit NOT NULL, CONSTRAINT PK_hk_Products PRIMARY KEY (LayerId, ProductId));
            CREATE TABLE PricingTool.Layers (Id int NOT NULL PRIMARY KEY, Brand nvarchar(100) NOT NULL);
            CREATE TABLE PricingTool.ProposedPrices (Id bigint NOT NULL PRIMARY KEY, PricingRunId bigint NOT NULL,
                LayerId int NOT NULL, ProductId int NULL, Sku nvarchar(64) NOT NULL, HasChange bit NOT NULL);

            INSERT INTO PricingTool.Layers VALUES (1, N'GjirafaMall'), (4, N'Gjirafa50');
            INSERT INTO hk.Products (LayerId, ProductId, Sku, CategoryL1, MainGroup, IsGj50Listed) VALUES
                (1, 1003670, N'SKU-SYNC',      N'Sport',      N'Sport',      1),  -- Mall's own listing of a synced SKU
                (4,  285259, N'SKU-SYNC',      N'Gaming',     N'Gaming',     0),  -- the Gj50 product whose id the Mall row carries
                (1,  931247, N'SKU-COLLIDE',   N'Sport',      N'Sport',      1),  -- Mall's listing of a second synced SKU...
                (1,  403412, N'SKU-UNRELATED', N'Teknologji', N'Teknologji', 0),  -- ...whose Gj50 id 403412 is also this Mall product
                (1,     500, N'SKU-OWN',       N'Sport',      N'Sport',      0),
                (1,     600, N'SKU-OTHER',     N'Teknologji', N'Teknologji', 0),
                (1,     700, N'SKU-TWO',       N'Teknologji', N'Teknologji', 0),  -- the proposal's own product...
                (1,     701, N'SKU-TWO',       N'Sport',      N'Sport',      0);  -- ...and another listing of its SKU
            INSERT INTO PricingTool.ProposedPrices VALUES
                (1, 1101, 1,  285259, N'SKU-SYNC',    1),  -- synced: Gj50 id not in the Mall map
                (2, 1101, 1,     500, N'SKU-OWN',     1),
                (3, 1101, 1,     600, N'SKU-OTHER',   1),
                (4, 1101, 1,     700, N'SKU-TWO',     1),
                (5, 1101, 1,  999999, N'SKU-NOWHERE', 1),
                (6, 1101, 1,     500, N'SKU-OWN',     0),  -- unchanged: never in the holdout set
                (7, 1101, 1,  403412, N'SKU-COLLIDE', 1);  -- synced: Gj50 id collides with an unrelated Mall product
            """);
        ProductMapSql.EnsureAsync(_db.ConnectionString).GetAwaiter().GetResult();
    }

    private (bool SameProductId, string MainGroup, int ProductId)? Lookup(int layer, int productId, string sku)
    {
        using var conn = _db.Open();
        using var cmd = new SqlCommand("SELECT SameProductId, MainGroup, ProductId FROM hk.ProductForProposal(@L, @P, @S)", conn);
        cmd.Parameters.AddWithValue("@L", layer);
        cmd.Parameters.AddWithValue("@P", productId);
        cmd.Parameters.AddWithValue("@S", sku);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetBoolean(0), r.GetString(1), r.GetInt32(2)) : null;
    }

    [LocalDbFact]
    public void SyncedMallRow_FindsItsMallListingBySku()
    {
        Seed();
        Assert.Equal((false, "Sport", 1003670), Lookup(Mall, 285259, "SKU-SYNC"));
    }

    [LocalDbFact]
    public void SyncedMallRow_WhoseIdCollidesWithAnotherMallProduct_StillFollowsItsSku()
    {
        Seed();
        // A ProductId join would return Mall product 403412 (Teknologji), an unrelated item.
        Assert.Equal((false, "Sport", 931247), Lookup(Mall, 403412, "SKU-COLLIDE"));
    }

    [LocalDbFact]
    public void OwnProductId_ChoosesAmongItsSkusListings_AndLayersNeverMix()
    {
        Seed();
        Assert.Equal((true, "Teknologji", 700), Lookup(Mall, 700, "SKU-TWO"));    // its own product, not the Sport listing
        Assert.Equal((true, "Gaming", 285259), Lookup(Gj50, 285259, "SKU-SYNC")); // the Gj50 layer keeps its own row
        Assert.Null(Lookup(Mall, 999999, "SKU-NOWHERE"));
    }

    [LocalDbFact]
    public async Task HoldoutLookup_HoldsOutSyncedRowsInAHoldoutCategory()
    {
        Seed();
        var held = await new HoldoutLookup(new AgentDataSource(_db.ConnectionString))
            .HoldoutProposalIdsAsync(Run, Mall, new[] { "Sport", "Vegla Pune" });
        Assert.Equal(new long[] { 1, 2, 7 }, held.OrderBy(i => i).ToArray());
    }

    [LocalDbFact]
    public async Task Ensure_IsIdempotent_AndAddsTheSkuIndex()
    {
        Seed();
        await ProductMapSql.EnsureAsync(_db.ConnectionString);
        await using var conn = _db.Open();
        await using var cmd = new SqlCommand(
            $"SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('hk.Products') AND name = '{ProductMapSql.SkuIndexName}'", conn);
        Assert.Equal(1, (int)(await cmd.ExecuteScalarAsync())!);
    }
}

/// <summary>Before the product map is loaded, ensuring the lookup is a no-op rather than an error.</summary>
public class ProductMapSqlWithoutMapTests : IClassFixture<LocalDbFixture>
{
    private readonly LocalDbFixture _db;

    public ProductMapSqlWithoutMapTests(LocalDbFixture db) => _db = db;

    [LocalDbFact]
    public async Task Ensure_DoesNothing_WhenTheMapIsNotLoaded()
    {
        await ProductMapSql.EnsureAsync(_db.ConnectionString);
        await using var conn = _db.Open();
        await using var cmd = new SqlCommand("SELECT OBJECT_ID('hk.ProductForProposal')", conn);
        Assert.IsType<DBNull>(await cmd.ExecuteScalarAsync());
    }
}
