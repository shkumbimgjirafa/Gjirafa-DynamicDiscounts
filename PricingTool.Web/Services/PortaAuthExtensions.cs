using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using PricingTool.Data;
using PricingTool.Data.Services;
using PricingTool.Web.Options;

namespace PricingTool.Web.Services;

/// <summary>
/// Wires Gjirafa's Porta SSO (OpenID Connect authorization-code flow + PKCE) onto a cookie session,
/// mirroring the sibling Finance app. Porta only authenticates the person; their Analyst/Manager role
/// is read from the local Users allowlist (see <see cref="PricingToolDbContext.Users"/>) on every
/// sign-in. A signed-in email that isn't in the table — or is inactive — is denied.
///
/// When Porta isn't configured (the local-dev default — see <see cref="PortaOptions.IsConfigured"/>)
/// it falls back to <see cref="DevAuthHandler"/>, the no-auth shim that signs every request in as a
/// "demo" Analyst+Manager so the app is usable without bouncing through Porta.
/// </summary>
public static class PortaAuthExtensions
{
    public static IServiceCollection AddPortaAuthentication(
        this IServiceCollection services, IConfiguration config)
    {
        var porta = new PortaOptions();
        config.GetSection(PortaOptions.SectionName).Bind(porta);
        services.Configure<PortaOptions>(config.GetSection(PortaOptions.SectionName));

        if (!porta.IsConfigured)
        {
            // No Porta credentials → keep the dev no-auth shim. The [Authorize(Roles = …)] markers
            // still resolve because the shim grants both roles.
            services
                .AddAuthentication(DevAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, null);
            return services;
        }

        services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(options =>
            {
                options.Cookie.Name = ".PricingTool.Auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
                options.LoginPath = "/Account/Login";
                options.LogoutPath = "/Account/Logout";
                options.AccessDeniedPath = "/Account/AccessDenied";
            })
            .AddOpenIdConnect(options =>
            {
                options.Authority = porta.Authority;
                options.ClientId = porta.ClientId;
                options.ClientSecret = porta.ClientSecret;
                options.CallbackPath = porta.CallbackPath;
                options.SignedOutCallbackPath = porta.SignedOutCallbackPath;

                // Authorization-code flow with PKCE (UsePkce defaults true on .NET 8).
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.UsePkce = true;
                options.SaveTokens = true;
                // Pull email/profile from the userinfo endpoint, like the Finance app's fetchUserInfo().
                options.GetClaimsFromUserInfoEndpoint = true;
                options.MapInboundClaims = false;

                options.Scope.Clear();
                foreach (var scope in porta.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    options.Scope.Add(scope);
                if (!options.Scope.Contains("openid"))
                    options.Scope.Add("openid");

                options.Events = new OpenIdConnectEvents
                {
                    // Authorize against the local Users allowlist after Porta authenticates.
                    OnTokenValidated = OnTokenValidatedAsync,
                    OnAccessDenied = ctx =>
                    {
                        ctx.HandleResponse();
                        ctx.Response.Redirect("/Account/AccessDenied");
                        return Task.CompletedTask;
                    },
                };
            });

        return services;
    }

    private static async Task OnTokenValidatedAsync(TokenValidatedContext ctx)
    {
        var principal = ctx.Principal;
        var email = (principal?.FindFirstValue("email")
                     ?? principal?.FindFirstValue(ClaimTypes.Email)
                     ?? "").Trim().ToLowerInvariant();

        var db = ctx.HttpContext.RequestServices.GetRequiredService<PricingToolDbContext>();
        var user = email.Length == 0
            ? null
            : await db.Users.FirstOrDefaultAsync(u => u.Email == email);

        if (user is null || !user.Active)
        {
            // Not in the allowlist (or deactivated): refuse the sign-in and show the access-denied page.
            ctx.HandleResponse();
            ctx.Response.Redirect("/Account/AccessDenied");
            return;
        }

        user.LastLoginUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // Replace whatever Porta sent with the authoritative local identity: the app role plus a
        // stable display name. [Authorize(Roles = …)] reads the ClaimTypes.Role claims added here.
        var identity = (ClaimsIdentity)principal!.Identity!;
        identity.AddClaim(new Claim(ClaimTypes.Role, user.Role));
        if (identity.FindFirst(ClaimTypes.Name) is null)
            identity.AddClaim(new Claim(ClaimTypes.Name, user.Name ?? email));
    }
}
