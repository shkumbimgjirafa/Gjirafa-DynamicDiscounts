using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PricingTool.Agent;
using PricingTool.Agent.Operations;
using PricingTool.Agent.Tools;

namespace PricingTool.Tests;

/// <summary>The cycle's retention of publish exceptions, on a throwaway LocalDB database.</summary>
public class DatabaseMaintenanceTests : IClassFixture<LocalDbFixture>
{
    private static readonly DateTime Now = new(2026, 11, 20, 7, 0, 0, DateTimeKind.Utc);
    private readonly LocalDbFixture _fixture;

    public DatabaseMaintenanceTests(LocalDbFixture fixture) => _fixture = fixture;

    private sealed class FakeClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [LocalDbFact]
    public async Task Publish_exceptions_older_than_14_days_are_deleted_and_the_rest_kept()
    {
        using (var c = _fixture.Open())
        {
            new SqlCommand("""
                IF SCHEMA_ID('ai') IS NULL EXEC('CREATE SCHEMA ai');
                CREATE TABLE ai.PublishException (Id bigint IDENTITY PRIMARY KEY, BatchId bigint NOT NULL, Sku nvarchar(100) NOT NULL,
                    Kind nvarchar(40) NOT NULL, Detail nvarchar(max) NOT NULL, CreatedUtc datetime2 NOT NULL);
                """, c).ExecuteNonQuery();
            foreach (var days in new[] { 30, 15, 14.5, 13, 1 })
            {
                using var cmd = new SqlCommand("INSERT INTO ai.PublishException (BatchId, Sku, Kind, Detail, CreatedUtc) VALUES (1, 'SKU', 'cheaper-deal', '', @At)", c);
                cmd.Parameters.AddWithValue("@At", Now.AddDays(-days));
                cmd.ExecuteNonQuery();
            }
        }
        var m = new DatabaseMaintenance(new AgentDataSource(_fixture.ConnectionString), Options.Create(new AgentOptions()), new FakeClock());

        Assert.Equal(3, await m.ExceptionRetentionAsync());

        using var check = _fixture.Open();
        Assert.Equal(2, (int)new SqlCommand("SELECT COUNT(*) FROM ai.PublishException", check).ExecuteScalar()!);
    }
}
