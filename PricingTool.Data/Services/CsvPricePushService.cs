using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PricingTool.Core.Abstractions;
using PricingTool.Core.Options;

namespace PricingTool.Data.Services;

/// <summary>
/// v1 push integration (open decision #6): exports approved prices as a CSV file the platform
/// team consumes. Swap this registration for a real NopCommerce write-back when that mechanism
/// is decided — the rest of the tool only knows IPricePushService.
/// </summary>
public class CsvPricePushService : IPricePushService
{
    private readonly PricingEngineOptions _options;
    private readonly ILogger<CsvPricePushService> _logger;

    public CsvPricePushService(IOptions<PricingEngineOptions> options, ILogger<CsvPricePushService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PushResult> PushAsync(IReadOnlyList<ApprovedPrice> prices, CancellationToken ct = default)
    {
        if (prices.Count == 0) return new PushResult(false, "Nothing to push.");

        Directory.CreateDirectory(_options.PushExportDirectory);
        var fileName = $"approved-prices-{DateTime.UtcNow:yyyyMMdd-HHmmss}-run{prices[0].RunId}.csv";
        var path = Path.Combine(_options.PushExportDirectory, fileName);

        var sb = new StringBuilder();
        sb.AppendLine("Sku,OldPrice,CurrentPrice,NewPrice,RunId,ProposalId,ApprovedBy,ExportedAtUtc");
        foreach (var p in prices)
        {
            sb.AppendLine(string.Join(",",
                Csv(p.Sku),
                p.OldPrice.ToString("0.00", CultureInfo.InvariantCulture),
                p.CurrentPrice.ToString("0.00", CultureInfo.InvariantCulture),
                p.NewPrice.ToString("0.00", CultureInfo.InvariantCulture),
                p.RunId.ToString(CultureInfo.InvariantCulture),
                p.ProposedPriceId.ToString(CultureInfo.InvariantCulture),
                Csv(p.ApprovedBy),
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
        }

        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, ct);
        _logger.LogInformation("Exported {Count} approved prices to {Path}.", prices.Count, path);
        return new PushResult(true, $"Exported {prices.Count} prices to {Path.GetFullPath(path)}");
    }

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
}
