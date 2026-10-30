using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace PricingTool.NopIntegration;

/// <summary>
/// nopCommerce caches discounts and prices; its admin clears that cache on save. A writer outside the store
/// app has to make the change visible itself. The round-trip check confirms the price either way.
/// </summary>
public interface IShopCacheInvalidator
{
    Task InvalidateAsync(string db, IReadOnlyCollection<int> productIds, CancellationToken ct = default);
}

/// <summary>Picks the mode from Publisher:CacheInvalidation: None (sandbox), WaitForExpiry, HttpEndpoint.</summary>
public sealed class ShopCacheInvalidator : IShopCacheInvalidator
{
    private readonly PublisherOptions _options;
    private readonly IConfiguration _config;

    public ShopCacheInvalidator(IOptions<PublisherOptions> options, IConfiguration config)
    {
        _options = options.Value;
        _config = config;
    }

    public async Task InvalidateAsync(string db, IReadOnlyCollection<int> productIds, CancellationToken ct = default)
    {
        switch (_options.CacheInvalidation)
        {
            case "None":
                return;
            case "WaitForExpiry":
                // The shop's cache time: the round-trip check runs after it.
                await Task.Delay(TimeSpan.FromSeconds(_options.CacheWaitSeconds), ct);
                return;
            case "HttpEndpoint":
                // Not exercised yet: the platform team names the endpoint before go-live (GO-LIVE.md step 2).
                var url = _options.CacheEndpointUrl ?? throw new InvalidOperationException("Publisher:CacheEndpointUrl is not set.");
                var token = _config["Publisher:CacheEndpointToken"] ?? throw new InvalidOperationException("Publisher:CacheEndpointToken (a secret) is not set.");
                using (var client = new HttpClient())
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(new { db, productIds }) };
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    (await client.SendAsync(request, ct)).EnsureSuccessStatusCode();
                }
                return;
            default:
                throw new InvalidOperationException($"Publisher:CacheInvalidation '{_options.CacheInvalidation}' is not None, WaitForExpiry or HttpEndpoint.");
        }
    }
}
