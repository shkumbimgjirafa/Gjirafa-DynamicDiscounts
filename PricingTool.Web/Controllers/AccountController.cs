using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PricingTool.Web.Options;

namespace PricingTool.Web.Controllers;

/// <summary>
/// Sign-in / sign-out surface for Porta SSO. The cookie handler redirects unauthenticated users to
/// <c>Login</c>; the "Sign in with Porta" button challenges the OIDC handler, which bounces to Porta
/// and back. When Porta isn't configured (local dev) the DevAuthHandler already authenticates every
/// request, so these endpoints just send the user home.
/// </summary>
[AllowAnonymous]
public class AccountController : Controller
{
    private readonly PortaOptions _porta;

    public AccountController(IOptions<PortaOptions> porta) => _porta = porta.Value;

    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (!_porta.IsConfigured || User.Identity?.IsAuthenticated == true)
            return LocalRedirectOrHome(returnUrl);

        ViewData["ReturnUrl"] = SafeReturn(returnUrl);
        return View();
    }

    [HttpGet]
    public IActionResult SignIn(string? returnUrl = null)
    {
        if (!_porta.IsConfigured)
            return LocalRedirectOrHome(returnUrl);

        return Challenge(
            new AuthenticationProperties { RedirectUri = SafeReturn(returnUrl) },
            OpenIdConnectDefaults.AuthenticationScheme);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Logout()
    {
        if (!_porta.IsConfigured)
            return Redirect("/");

        // Clear the local cookie and end the Porta session (redirects back via SignedOutCallbackPath).
        return SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            CookieAuthenticationDefaults.AuthenticationScheme,
            OpenIdConnectDefaults.AuthenticationScheme);
    }

    [HttpGet]
    public IActionResult AccessDenied() => View();

    private IActionResult LocalRedirectOrHome(string? returnUrl) =>
        Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl!) : Redirect("/");

    private string SafeReturn(string? returnUrl) =>
        Url.IsLocalUrl(returnUrl) ? returnUrl! : "/";
}
