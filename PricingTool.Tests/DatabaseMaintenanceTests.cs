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

        using (var check = _fixture.Open())
            Assert.Equal(2, (int)new SqlCommand("SELECT COUNT(*) FROM ai.PublishException", check).ExecuteScalar()!);
        Assert.Equal(1, await m.ExceptionRetentionAsync(days: 3));                                 // the size valve's window
    }

    [LocalDbFact]
    public async Task The_size_valves_run_retention_keeps_one_run_per_layer_and_the_exported_run()
    {
        using (var c = _fixture.Open())
            new SqlCommand("""
                IF SCHEMA_ID('PricingTool') IS NULL EXEC('CREATE SCHEMA PricingTool');
                CREATE TABLE PricingTool.PricingRuns (Id bigint PRIMARY KEY, LayerId int NOT NULL, StartedUtc datetime2 NOT NULL);
                CREATE TABLE PricingTool.ProposedPrices (Id bigint IDENTITY PRIMARY KEY, PricingRunId bigint NOT NULL);
                CREATE TABLE PricingTool.AlgorithmVotes (Id bigint IDENTITY PRIMARY KEY, ProposedPriceId bigint NOT NULL);
                CREATE TABLE PricingTool.PriceChangeOutcomes (Id bigint IDENTITY PRIMARY KEY, ProposedPriceId bigint NULL);
                CREATE TABLE PricingTool.DailySnapshots (Id bigint IDENTITY PRIMARY KEY, LayerId int NOT NULL, SnapshotDate date NOT NULL);
                INSERT INTO PricingTool.PricingRuns VALUES (1, 4, '2026-11-17'), (2, 4, '2026-11-18'), (3, 4, '2026-11-19'), (4, 4, '2026-11-20');
                INSERT INTO PricingTool.ProposedPrices (PricingRunId) VALUES (1), (2), (3), (4);
                INSERT INTO PricingTool.AlgorithmVotes (ProposedPriceId) SELECT Id FROM PricingTool.ProposedPrices;
                INSERT INTO PricingTool.DailySnapshots (LayerId, SnapshotDate) VALUES (4, '2026-11-17'), (4, '2026-11-18'), (4, '2026-11-19'), (4, '2026-11-20');
                """, c).ExecuteNonQuery();
        var m = new DatabaseMaintenance(new AgentDataSource(_fixture.ConnectionString), Options.Create(new AgentOptions()), new FakeClock());

        var (proposals, snapshots) = await m.RetentionAsync(1, new long[] { 2 });                   // run 2 is behind the latest export

        Assert.Equal((2, 1), (proposals, snapshots));
        using var check = _fixture.Open();
        Assert.Equal("2,4", (string)new SqlCommand("SELECT STRING_AGG(CAST(PricingRunId AS varchar(5)), ',') WITHIN GROUP (ORDER BY PricingRunId) FROM PricingTool.ProposedPrices", check).ExecuteScalar()!);
        Assert.Equal(2, (int)new SqlCommand("SELECT COUNT(*) FROM PricingTool.AlgorithmVotes", check).ExecuteScalar()!);
    }
}
