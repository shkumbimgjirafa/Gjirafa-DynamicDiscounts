using PricingTool.NopIntegration.Sandbox;

namespace PricingTool.NopIntegration;

/// <summary>
/// Settings for writing the shop's discounts (section "Publisher"). Going live is a change here, never in
/// code: Target = Live with each shop's write connection (a secret) and schema "dbo". During the hackathon
/// Target stays Sandbox, which only ever writes to the shop_sandbox_* schemas.
/// </summary>
public class PublisherOptions
{
    public const string SectionName = "Publisher";
    public const string SandboxTarget = "Sandbox";
    public const string LiveTarget = "Live";

    public string Target { get; set; } = SandboxTarget;

    /// <summary>Per shop database (GjirafaMall, GjirafaEcommerce): where its discount tables are.</summary>
    public Dictionary<string, ShopConnection> Shops { get; set; } = new();

    /// <summary>SKUs per transaction: short transactions keep locks on the live 3.2M-row Discount table short.</summary>
    public int ChunkSize { get; set; } = 5000;

    /// <summary>ai-price discounts end this many days ahead and are renewed by every publish: if the system ever
    /// stops, its prices fall back to the shelf price within this many days.</summary>
    public int RollingExpiryDays { get; set; } = 7;

    /// <summary>When on, a cheaper team regular discount on the SKU (name matching <see cref="TeamRegularNamePatterns"/>)
    /// is end-dated so ours applies. Off by default: the commercial owner decides before go-live. Campaign
    /// discounts we didn't create are never touched either way.</summary>
    public bool SupersedeTeamRegularDiscounts { get; set; }

    /// <summary>SQL LIKE patterns of the team's regular monthly discount names, e.g. "%-disc-%".</summary>
    public List<string> TeamRegularNamePatterns { get; set; } = new();

    /// <summary>Above this share of round-trip mismatches in a batch, the batch is rolled back automatically.</summary>
    public decimal RoundTripRollbackPct { get; set; } = 5m;

    /// <summary>None (sandbox) / WaitForExpiry / HttpEndpoint: how the shop's discount cache learns of a change.</summary>
    public string CacheInvalidation { get; set; } = "None";
    public int CacheWaitSeconds { get; set; } = 60;
    public string? CacheEndpointUrl { get; set; }

    public ShopConnection ShopFor(string db, string toolConnectionString)
    {
        var shop = Shops.TryGetValue(db, out var s) ? s : new ShopConnection();
        if (string.Equals(Target, SandboxTarget, StringComparison.OrdinalIgnoreCase))
        {
            var schema = string.IsNullOrWhiteSpace(shop.Schema) ? SandboxSchema.SchemaFor(db) : shop.Schema;
            if (!schema.StartsWith(SandboxSchema.SchemaPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Publisher:Target is Sandbox, but shop {db} points at schema '{schema}': the sandbox only writes to {SandboxSchema.SchemaPrefix}*.");
            return new ShopConnection { ConnectionString = string.IsNullOrWhiteSpace(shop.ConnectionString) ? toolConnectionString : shop.ConnectionString, Schema = schema };
        }
        if (!string.Equals(Target, LiveTarget, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Publisher:Target '{Target}' is neither Sandbox nor Live.");
        if (string.IsNullOrWhiteSpace(shop.ConnectionString) || string.IsNullOrWhiteSpace(shop.Schema))
            throw new InvalidOperationException($"Publisher:Target is Live, but shop {db} has no write connection or schema configured.");
        return shop;
    }
}

public class ShopConnection
{
    /// <summary>Live: the least-privilege write login's connection string, from secrets. Sandbox: empty = the tool DB.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Live: "dbo". Sandbox: empty = shop_sandbox_&lt;Db&gt;.</summary>
    public string Schema { get; set; } = "";
}
