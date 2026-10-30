namespace PricingTool.Data.Entities;

/// <summary>
/// An authorized user of the tool. Porta SSO (OpenID Connect) only *authenticates* the
/// person (proves their email); their *role* — Analyst or Manager — comes from this table,
/// exactly like the sibling Finance app. A user who signs in via Porta but is not present
/// here (or is inactive) is denied access. The initial Manager (admin@example.test) is seeded
/// on startup so the system is usable before anyone is added via the Users admin screen.
/// </summary>
public class User
{
    public int Id { get; set; }

    /// <summary>Gjirafa account email. Matched case-insensitively (stored lower-cased).</summary>
    public string Email { get; set; } = "";

    /// <summary>Display name (from Porta profile if not set explicitly).</summary>
    public string? Name { get; set; }

    /// <summary>"Analyst" or "Manager" — see <see cref="Services.PricingRoles"/>.</summary>
    public string Role { get; set; } = Services.PricingRoles.Analyst;

    /// <summary>Inactive users keep their history but cannot sign in.</summary>
    public bool Active { get; set; } = true;

    public DateTime CreatedUtc { get; set; }

    public DateTime? LastLoginUtc { get; set; }
}
