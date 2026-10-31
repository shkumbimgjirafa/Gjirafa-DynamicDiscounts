using Microsoft.Data.SqlClient;

namespace PricingTool.Tests;

/// <summary>Runs only where SQL Server LocalDB answers (this machine; not a bare CI agent).</summary>
public sealed class LocalDbFactAttribute : FactAttribute
{
    public LocalDbFactAttribute()
    {
        if (!LocalDb.Available) Skip = "SQL Server LocalDB is not available here.";
    }
}

/// <summary>The theory form of <see cref="LocalDbFactAttribute"/>.</summary>
public sealed class LocalDbTheoryAttribute : TheoryAttribute
{
    public LocalDbTheoryAttribute()
    {
        if (!LocalDb.Available) Skip = "SQL Server LocalDB is not available here.";
    }
}

public static class LocalDb
{
    public const string Server = "Server=(localdb)\\mssqllocaldb;Trusted_Connection=True;TrustServerCertificate=True;";
    public const string Hackathon = Server + "Database=PricingToolHackathon;";

    private static readonly Lazy<bool> Probe = new(() =>
    {
        try
        {
            using var conn = new SqlConnection(Server + "Connect Timeout=5;");
            conn.Open();
            return true;
        }
        catch (SqlException)
        {
            return false;
        }
    });

    public static bool Available => Probe.Value;
}

/// <summary>A throwaway database for one test class: created empty, dropped afterwards.</summary>
public sealed class LocalDbFixture : IDisposable
{
    public string Name { get; } = "PricingToolTests_" + Guid.NewGuid().ToString("N")[..12];
    public string ConnectionString => LocalDb.Server + $"Database={Name};";

    public LocalDbFixture()
    {
        if (!LocalDb.Available) return;
        Exec(LocalDb.Server, $"CREATE DATABASE [{Name}]");
    }

    public SqlConnection Open()
    {
        var conn = new SqlConnection(ConnectionString);
        conn.Open();
        return conn;
    }

    public void Dispose()
    {
        if (!LocalDb.Available) return;
        SqlConnection.ClearAllPools();
        Exec(LocalDb.Server, $"IF DB_ID('{Name}') IS NOT NULL BEGIN ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}]; END");
    }

    public static void Exec(string connectionString, string sql)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        cmd.ExecuteNonQuery();
    }
}
