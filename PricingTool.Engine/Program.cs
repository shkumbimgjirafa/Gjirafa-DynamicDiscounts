using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PricingTool.Core.Options;
using PricingTool.Data;
using PricingTool.Data.Services;
using PricingTool.Engine;

var runOnce = args.Contains("--run-now");
var fitElasticity = args.Contains("--fit-elasticity");
var weeklyReport = args.Contains("--weekly-report");

// Optional "--layer <code>" filter for --run-now (e.g. --layer MK or --layer GjirafaMall/MK).
string? layerArg = null;
var layerIdx = Array.IndexOf(args, "--layer");
if (layerIdx >= 0 && layerIdx + 1 < args.Length) layerArg = args[layerIdx + 1];

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddPricingTool(builder.Configuration);
builder.Services.Configure<WeeklyPcsReportOptions>(builder.Configuration.GetSection(WeeklyPcsReportOptions.Section));
if (!runOnce && !fitElasticity && !weeklyReport)
{
    builder.Services.AddHostedService<ScheduledRunWorker>();
    builder.Services.AddHostedService<ElasticityFitWorker>();
    builder.Services.AddHostedService<ApprovedBatchRefreshWorker>();
    builder.Services.AddHostedService<WeeklyPcsReportWorker>();
}

var host = builder.Build();

// Apply migrations and seed band/schedule defaults before the scheduler starts.
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PricingToolDbContext>();
    var startupLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    await DbInitializer.MigrateWithRetryAsync(db, startupLogger);

    var options = scope.ServiceProvider.GetRequiredService<IOptions<PricingEngineOptions>>().Value;
    await DbSeeder.SeedCoreAsync(db, options);

    if (options.UseDemoData)
    {
        await scope.ServiceProvider.GetRequiredService<DemoHistoryBackfill>().EnsureBackfilledAsync();
        await scope.ServiceProvider.GetRequiredService<DemoOutcomeSeeder>().EnsureSeededAsync();
    }
}

// `--run-now`: execute one pricing run per targeted layer immediately and exit (ops/cron escape
// hatch). With no "--layer" filter it runs ALL active layers sequentially.
if (runOnce)
{
    using var scope = host.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<PricingToolDbContext>();
    var orchestrator = scope.ServiceProvider.GetRequiredService<PricingRunOrchestrator>();

    // Materialize the (tiny) active-layer set, then match the filter in memory so the comparison
    // is reliably case-insensitive regardless of the database collation.
    var targets = await db.Layers.AsNoTracking()
        .Where(l => l.IsActive).OrderBy(l => l.SortOrder).ToListAsync();
    if (!string.IsNullOrWhiteSpace(layerArg))
    {
        var code = layerArg.Trim();
        // Accept "MK", "GjirafaMall/MK", or the display name (case-insensitive).
        targets = targets.Where(l =>
            l.CountryCode.Equals(code, StringComparison.OrdinalIgnoreCase) ||
            $"{l.Brand}/{l.CountryCode}".Equals(code, StringComparison.OrdinalIgnoreCase) ||
            l.DisplayName.Equals(code, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    if (targets.Count == 0)
    {
        Console.WriteLine(layerArg is null
            ? "No active layers to run."
            : $"No active layer matched '{layerArg}'.");
        return;
    }

    foreach (var layer in targets)
    {
        var run = await orchestrator.ExecuteRunAsync(Environment.UserName + " (cli)", isOnDemand: true, layer.Id);
        Console.WriteLine(
            $"[{layer.DisplayName}] Run #{run.Id}: {run.Status} — {run.SkuCount} SKUs, {run.ProposalCount} proposals, " +
            $"{run.SkippedCount} skipped, {run.ErrorCount} errors.");
    }
    return;
}

// `--fit-elasticity`: recompute per-(layer, SKU) elasticities for the targeted layers, then exit
// (ops/cron escape hatch). With no "--layer" filter it fits ALL active layers sequentially.
if (fitElasticity)
{
    using var scope = host.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<PricingToolDbContext>();
    var fit = scope.ServiceProvider.GetRequiredService<ElasticityFitService>();

    var targets = await db.Layers.AsNoTracking()
        .Where(l => l.IsActive).OrderBy(l => l.SortOrder).ToListAsync();
    if (!string.IsNullOrWhiteSpace(layerArg))
    {
        var code = layerArg.Trim();
        targets = targets.Where(l =>
            l.CountryCode.Equals(code, StringComparison.OrdinalIgnoreCase) ||
            $"{l.Brand}/{l.CountryCode}".Equals(code, StringComparison.OrdinalIgnoreCase) ||
            l.DisplayName.Equals(code, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    foreach (var layer in targets)
    {
        var fitted = await fit.FitLayerAsync(layer.Id);
        Console.WriteLine($"[{layer.DisplayName}] Elasticity fit: {fitted} SKUs fitted.");
    }
    return;
}

// `--weekly-report [--to <addr>] [--out <file.csv>] [--probe-smtp]`: build the weekly pcs/gjirafa50 report once
// and email it (to --to only, when given), or just write the CSV with --out, or only test the SMTP login with
// --probe-smtp. Never touches the worker's sent-week state.
if (weeklyReport)
{
    string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    var opts = host.Services.GetRequiredService<IOptions<WeeklyPcsReportOptions>>().Value;
    if (args.Contains("--probe-smtp"))
    {
        // Login check only: the password's shape and the SMTP server's reply to AUTH. Sends nothing.
        Console.WriteLine($"Password: {WeeklyPcsReport.DescribePassword(opts.SmtpPassword)}");
        Console.WriteLine($"AUTH reply: {await WeeklyPcsReport.ProbeSmtpAuthAsync(opts, CancellationToken.None)}");
        return;
    }
    var report = await WeeklyPcsReport.BuildAsync(builder.Configuration, opts,
        WeeklyPcsReport.ToLocal(DateTime.UtcNow, opts.TimeZone), CancellationToken.None);
    Console.WriteLine($"Week {report.Label}: {string.Join("; ", report.Summary)}");

    var outFile = Arg("--out");
    if (outFile is not null)
    {
        await File.WriteAllBytesAsync(outFile, report.Csv);
        Console.WriteLine($"Wrote {outFile}");
        return;
    }
    var to = Arg("--to");
    await WeeklyPcsReport.SendAsync(opts, report, to ?? opts.To, to is null ? opts.Cc : "", "", CancellationToken.None);
    Console.WriteLine($"Emailed to {to ?? opts.To}");
    return;
}

await host.RunAsync();
