using PricingTool.Core.Domain;

namespace PricingTool.Core.Abstractions;

/// <summary>
/// Pulls the daily pricing dataset for one layer. The real implementation runs the dataset query
/// over the READ-ONLY source connection (scoped by <paramref name="layer"/>); the demo
/// implementation fabricates data and ignores the layer context.
/// </summary>
public interface ISourceDataReader
{
    Task<IReadOnlyList<SnapshotRow>> GetDailyDatasetAsync(LayerSourceContext layer, CancellationToken ct = default);

    /// <summary>
    /// For an approved clearance batch: units + net (excl. VAT) sold since <paramref name="sinceUtc"/>
    /// across BOTH platforms of the country, the CURRENT local-warehouse stock, and the live discount id(s)
    /// priced at the pushed clearance price, for the given SKUs only. Powers the hourly "sold since push"
    /// refresh and the sold-out list. <paramref name="skuPushedPrices"/> maps each SKU to the price we pushed
    /// (the proposed clearance price); the returned <see cref="BatchSaleRow.ActiveDiscountIds"/> lists only
    /// live customer-facing discounts whose price equals that SKU's pushed price — the promo(s) to delete once
    /// it clears. Every requested SKU is returned (zeros / NULL discount ids when it hasn't sold, has no local
    /// stock, or has no matching live discount).
    /// </summary>
    Task<IReadOnlyList<BatchSaleRow>> GetApprovedBatchSalesAsync(
        LayerSourceContext layer, DateTime sinceUtc, IReadOnlyDictionary<string, decimal> skuPushedPrices, CancellationToken ct = default);
}
