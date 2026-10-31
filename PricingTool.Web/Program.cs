using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PricingTool.Agent;
using PricingTool.Core.Options;
using PricingTool.Data;
using PricingTool.Data.Services;
using PricingTool.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// This build runs only in the Hackathon environment. Any other one falls back to appsettings.json: the local database
// "PricingTool" (the main repo's development database, which the app migrates and seeds on start) and the .env loader.
// On 31 Oct a start with the "http" launch profile (Development) did exactly that; it was reverted by hand.
if (!builder.Environment.IsEnvironment("Hackathon"))
{
    throw new InvalidOperationException(
        $"This build runs only with ASPNETCORE_ENVIRONMENT=Hackathon, not '{builder.Environment.EnvironmentName}': " +
        "dotnet run --project PricingTool.Web --launch-profile hackathon");
}
// The replica connection lives in user-secrets, which ASP.NET only loads by itself in Development. No .env is ever read.
builder.Configuration.AddUserSecrets(typeof(Program).Assembly, optional: true);

builder.Services.AddPricingTool(builder.Configuration);
builder.Services.AddPricingAgent(builder.Configuration);
// The publisher's switches, shown on the Agent page. The web's own push stays the CSV export; the writer is here only so a
// person can roll a publish back (the Campaigns tab's veto window); publishing runs in the engine host and AgentTools.
builder.Services.Configure<PricingTool.NopIntegration.PublisherOptions>(
    builder.Configuration.GetSection(PricingTool.NopIntegration.PublisherOptions.SectionName));
builder.Services.AddSingleton<PricingTool.NopIntegration.IShopCacheInvalidator, PricingTool.NopIntegration.ShopCacheInvalidator>();
builder.Services.AddScoped<PricingTool.NopIntegration.NopDiscountWriter>();
// A campaign planned from the chat publishes like any other: time-boxed, to the sandbox, stoppable until it starts.
builder.Services.AddScoped<PricingTool.Agent.Campaigns.ICampaignPublisher, PricingTool.NopIntegration.CampaignPublisher>();
builder.Services.AddControllersWithViews(o => o.Filters.Add<LayerContextFilter>());
builder.Services.AddSingleton<RunLauncher>();
// "Run week" on the Run tab: the whole loop for one layer, publishing only to the shop sandbox.
builder.Services.AddScoped<PricingTool.NopIntegration.IRunWeekSteps, PricingTool.NopIntegration.RunWeekSteps>();
builder.Services.AddScoped<PricingTool.NopIntegration.RunWeekLoop>();
builder.Services.AddSingleton<RunWeekLauncher>();
// The Run and Chat tabs: the current run's report, the agents' activity, and chat turns run in the background.
builder.Services.AddScoped<AgentActivity>();
builder.Services.AddScoped<RunReportService>();
builder.Services.AddSingleton<ChatTurnLauncher>();
// The Evidence page: numbers.json, and its SQL figures recomputed live as a read-only database user.
builder.Services.Configure<EvidenceOptions>(builder.Configuration.GetSection(EvidenceOptions.SectionName));
builder.Services.AddScoped<EvidenceService>();

// Liveness/readiness endpoint for the container orchestrator (see k8s probes). Kept
// dependency-free so it answers as soon as Kestrel is up, independent of DB health.
builder.Services.AddHealthChecks();

// Behind the nginx ingress, TLS is terminated upstream and the original scheme/IP arrive
// in X-Forwarded-* headers. Trust them so request.Scheme is "https" (correct redirect/
// cookie behaviour). The proxy is an in-cluster pod, not loopback, so the default known-
// network restrictions must be cleared — the ingress is the only path in.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

// Multi-layer: session holds the selected layer; CurrentLayerService resolves it per request.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentLayerService>();
builder.Services.AddSession(o =>
{
    o.Cookie.IsEssential = true;
    o.IdleTimeout = TimeSpan.FromDays(7);
});

// Authentication: real Gjirafa Porta SSO (OpenID Connect) when configured, else the DevAuthHandler
// no-auth shim for local dev. Authorization is the local Users allowlist (email → Analyst/Manager);
// the [Authorize(Roles = ...)] markers on the controllers enforce it. See PortaAuthExtensions.
builder.Services.AddPortaAuthentication(builder.Configuration);

var app = builder.Build();

// Must run before anything that inspects the scheme/host (redirects, cookies, etc.).
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// In production the nginx ingress terminates TLS and force-redirects http->https; the
// container only listens on plain HTTP, so an in-app redirect would loop and break the
// kubelet probes. Locally, Kestrel has an https port, so keep the redirect for dev parity.
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();

// Open, dependency-free health endpoint for k8s probes.
app.MapHealthChecks("/healthz");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Run}/{action=Index}/{id?}");

// Migrate + seed on startup (bands, schedule defaults, demo history, and the initial Porta admin).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PricingToolDbContext>();
    var startupLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    await DbInitializer.MigrateWithRetryAsync(db, startupLogger);
    await scope.ServiceProvider.MigrateAgentDbAsync();

    var options = scope.ServiceProvider.GetRequiredService<IOptions<PricingEngineOptions>>().Value;
    if (app.Environment.IsEnvironment("Hackathon"))
    {
        // Which configuration source supplied the replica connection (the source's name, never its value).
        var origin = ((IConfigurationRoot)builder.Configuration).Providers
            .LastOrDefault(p => p.TryGet("ConnectionStrings:SourceReadOnly", out _))?.ToString() ?? "not configured";
        startupLogger.LogInformation("Hackathon environment: replica connection from {Origin}; demo data {Demo}.",
            origin, options.UseDemoData ? "on" : "off");
    }
    await DbSeeder.SeedCoreAsync(db, options);

    // Seed the initial Manager (Porta:InitialAdminEmail; empty in this repo: none) so the app is usable before anyone is
    // added via the Users admin screen. Only inserts when the Users table is empty.
    var porta = scope.ServiceProvider.GetRequiredService<IOptions<PricingTool.Web.Options.PortaOptions>>().Value;
    await DbSeeder.SeedInitialUserAsync(db, porta.InitialAdminEmail);

    if (options.UseDemoData)
    {
        await scope.ServiceProvider.GetRequiredService<DemoHistoryBackfill>().EnsureBackfilledAsync();
        await scope.ServiceProvider.GetRequiredService<DemoOutcomeSeeder>().EnsureSeededAsync();
    }
}

app.Run();
