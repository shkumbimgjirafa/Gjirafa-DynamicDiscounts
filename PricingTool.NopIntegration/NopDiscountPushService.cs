using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PricingTool.Core.Abstractions;
using PricingTool.Data;

namespace PricingTool.NopIntegration;

/// <summary>
/// The tool's push integration point (<see cref="IPricePushService"/>) backed by <see cref="NopDiscountWriter"/>:
/// approved proposals become ai-price discounts in the configured shop (the sandbox during the hackathon),
/// one batch per layer. The proposal's old price is the shelf price, so a price at or above it is reported
/// instead of written.
/// </summary>
public sealed class NopDiscountPushService : IPricePushService
{
    private readonly NopDiscountWriter _writer;
    private readonly PricingToolDbContext _db;

    public NopDiscountPushService(NopDiscountWriter writer, PricingToolDbContext db)
    {
        _writer = writer;
        _db = db;
    }

    public async Task<PushResult> PushAsync(IReadOnlyList<ApprovedPrice> prices, CancellationToken ct = default)
    {
        if (prices.Count == 0) return new PushResult(false, "Nothing to push.");
        var ids = prices.Select(p => p.ProposedPriceId).ToList();
        var layerOf = await _db.ProposedPrices.AsNoTracking().Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.LayerId, ct);

        var parts = new List<string>();
        var ok = true;
        foreach (var group in prices.GroupBy(p => layerOf.GetValueOrDefault(p.ProposedPriceId)))
        {
            if (group.Key == 0) { ok = false; parts.Add($"{group.Count()} prices with no proposal row"); continue; }
            var outcome = await _writer.PublishAsync(new PublishRequest(group.Key,
                group.Select(p => new PublishItem(p.Sku, p.NewPrice, p.OldPrice)).ToList()) { EngineRunId = group.First().RunId }, ct);
            ok &= outcome.Status == "Published";
            parts.Add($"layer {group.Key}: batch #{outcome.BatchId} {outcome.Status}, {outcome.Created} created, {outcome.Updated} updated, " +
                      $"{outcome.Unchanged} unchanged, {outcome.Skipped} skipped, round-trip {outcome.RoundTripChecked - outcome.RoundTripMismatches}/{outcome.RoundTripChecked}");
        }
        return new PushResult(ok, string.Join("; ", parts));
    }
}

public static class NopPublisherServiceCollectionExtensions
{
    /// <summary>Registers the discount writer (section "Publisher"). The tool's push goes through it only when
    /// Publisher:ReplaceCsvPush is on; otherwise the CSV export stays the push.</summary>
    public static IServiceCollection AddNopPublisher(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<PublisherOptions>(config.GetSection(PublisherOptions.SectionName));
        services.AddSingleton<IShopCacheInvalidator, ShopCacheInvalidator>();
        services.AddScoped<NopDiscountWriter>();
        services.AddScoped<PricingTool.Agent.Operations.ICheckedPricePublisher, CheckedPricePublisher>();
        services.AddScoped<PricingTool.Agent.Operations.IRaiseRestorer, NopRaiseRestorer>();
        services.Configure<CostWatch.CostWatcherOptions>(config.GetSection(CostWatch.CostWatcherOptions.SectionName));
        services.AddScoped<CostWatch.ICostPublisher, CostWatch.WriterCostPublisher>();
        services.AddScoped<CostWatch.CostWatcher>();
        services.AddScoped<CostWatch.ReplicaCostFeed>();
        services.AddScoped<PricingTool.Agent.Campaigns.ICampaignPublisher, CampaignPublisher>();
        if (config.GetValue<bool>("Publisher:ReplaceCsvPush"))
            services.AddScoped<IPricePushService, NopDiscountPushService>();
        return services;
    }
}
