namespace PricingTool.Web.Options;

/// <summary>
/// Porta SSO (OpenID Connect) configuration, bound from the "Porta" section of configuration
/// (appsettings / environment, e.g. <c>Porta__Authority</c>). Mirrors the sibling Finance app's
/// PORTA_* environment variables.
///
/// When <see cref="IsConfigured"/> is false (no Authority / ClientId — the local-dev default),
/// the app falls back to the <see cref="Services.DevAuthHandler"/> no-auth shim so it stays fully
/// usable without bouncing through Porta. Fill these in (prod, via the k8s Secret) to turn on real SSO.
/// </summary>
public class PortaOptions
{
    public const string SectionName = "Porta";

    /// <summary>OIDC authority / issuer base URL, e.g. https://login.gjirafa.com — discovery is read from {Authority}/.well-known/openid-configuration.</summary>
    public string? Authority { get; set; }

    /// <summary>OIDC client (application) id registered in Porta for this app.</summary>
    public string? ClientId { get; set; }

    /// <summary>OIDC client secret.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Redirect path the OIDC middleware listens on. The full redirect URI registered in Porta must be
    /// {public origin}{CallbackPath}, e.g. https://dynamic-pricing.gjirafa.dev/signin-oidc.
    /// </summary>
    public string CallbackPath { get; set; } = "/signin-oidc";

    /// <summary>Path the middleware listens on for the post-logout redirect from Porta.</summary>
    public string SignedOutCallbackPath { get; set; } = "/signout-callback-oidc";

    /// <summary>Space-separated scopes requested. "openid" is always included.</summary>
    public string Scope { get; set; } = "openid profile email";

    /// <summary>Seeded as the initial Manager on first startup (only when the Users table is empty).</summary>
    public string InitialAdminEmail { get; set; } = "";

    /// <summary>True when enough is configured to wire real SSO; otherwise the dev no-auth shim is used.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(ClientId);
}
