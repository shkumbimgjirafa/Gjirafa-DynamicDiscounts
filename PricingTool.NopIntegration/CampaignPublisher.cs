using PricingTool.Agent.Campaigns;

namespace PricingTool.NopIntegration;

/// <summary>Publishes an accepted campaign plan as time-boxed ai-campaign-&lt;key&gt;-&lt;sku&gt; discounts through the writer
/// (the sandbox during the hackathon): they start and end with the campaign by themselves, and a cheaper deal we didn't
/// create is left alone and listed as an exception.</summary>
public sealed class CampaignPublisher : ICampaignPublisher
{
    private readonly NopDiscountWriter _writer;
    public CampaignPublisher(NopDiscountWriter writer) => _writer = writer;

    public async Task<(long BatchId, string Status)> PublishAsync(int layerId, long campaignKey, DateTime startUtc, DateTime endUtc,
        IReadOnlyList<(string Sku, decimal Price, decimal Shelf)> lines, CancellationToken ct)
    {
        var outcome = await _writer.PublishAsync(new PublishRequest(layerId, lines.Select(l => new PublishItem(l.Sku, l.Price, l.Shelf)).ToList())
        {
            Kind = PublishKinds.Campaign, CampaignKey = campaignKey.ToString(System.Globalization.CultureInfo.InvariantCulture),
            CampaignStartUtc = startUtc, CampaignEndUtc = endUtc,
        }, ct);
        return (outcome.BatchId, outcome.Status);
    }
}
