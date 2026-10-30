using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using PricingTool.Data.Services;

namespace PricingTool.Web.Services;

/// <summary>
/// Local-dev no-authentication shim. Used only when Porta SSO is NOT configured (see
/// <see cref="PortaAuthExtensions"/> / <see cref="Options.PortaOptions.IsConfigured"/>) — i.e. the
/// default local run with no Authority/ClientId. It auto-authenticates every request as a single
/// "demo" user holding both the Analyst and Manager roles, so the whole app is usable with no login
/// screen. Once Porta is configured this handler is not registered and the real OIDC scheme + Users
/// allowlist take over; the [Authorize(Roles = ...)] markers enforce against the assigned role.
/// </summary>
public class DevAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "DevNoAuth";

    public DevAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "demo"),
            new Claim(ClaimTypes.Role, PricingRoles.Analyst),
            new Claim(ClaimTypes.Role, PricingRoles.Manager),
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
