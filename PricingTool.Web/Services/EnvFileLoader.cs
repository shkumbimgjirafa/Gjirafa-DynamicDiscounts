using Microsoft.Data.SqlClient;

namespace PricingTool.Web.Services;

/// <summary>
/// Minimal .env loader for local development. Walks up from the content root to find a
/// <c>.env</c> file (gitignored) and, when the live source host is filled in, builds the
/// read-only <c>SourceReadOnly</c> connection string from its parts and switches the app
/// out of demo mode. Leave the host blank to stay on the demo data generator.
/// </summary>
public static class EnvFileLoader
{
    public static void ApplyDotEnv(WebApplicationBuilder builder)
    {
        var path = FindEnvFile(builder.Environment.ContentRootPath);
        if (path is null) return;

        var values = Parse(path);

        var host = Get(values, "SOURCE_DB_HOST");
        if (string.IsNullOrWhiteSpace(host))
            return; // No host provided — keep whatever appsettings says (demo by default).

        var csb = new SqlConnectionStringBuilder
        {
            DataSource = host,
            InitialCatalog = Get(values, "SOURCE_DB_NAME") is { Length: > 0 } db ? db : "GjirafaMall",
            UserID = Get(values, "SOURCE_DB_USER") ?? "",
            Password = Get(values, "SOURCE_DB_PASSWORD") ?? "",
            TrustServerCertificate = true,
            ApplicationIntent = ApplicationIntent.ReadOnly,
            ConnectTimeout = 15,
        };

        var overrides = new Dictionary<string, string?>
        {
            ["ConnectionStrings:SourceReadOnly"] = csb.ConnectionString,
            // Filling in a real host means: use the SQL reader against live data.
            ["PricingEngine:UseDemoData"] = "false",
        };

        var mode = Get(values, "SOURCE_DATASET_MODE");
        if (!string.IsNullOrWhiteSpace(mode))
            overrides["SourceDataset:Mode"] = mode;

        builder.Configuration.AddInMemoryCollection(overrides);
    }

    private static string? FindEnvFile(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, ".env");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static Dictionary<string, string> Parse(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];

            result[key] = value;
        }
        return result;
    }

    private static string? Get(Dictionary<string, string> values, string key)
        => values.TryGetValue(key, out var v) ? v : null;
}
