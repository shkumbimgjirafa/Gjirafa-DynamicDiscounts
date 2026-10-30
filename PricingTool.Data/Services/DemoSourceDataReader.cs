using PricingTool.Core.Abstractions;
using PricingTool.Core.Demo;
using PricingTool.Core.Domain;

namespace PricingTool.Data.Services;

/// <summary>Demo-mode replacement for the SQL reader (UseDemoData=true). No source DB required.</summary>
public class DemoSourceDataReader : ISourceDataReader
{
    private readonly DemoDataGenerator _generator = new();

    // Demo mode ignores the layer context — every layer gets the same synthetic catalog (v1).
    public Task<IReadOnlyList<SnapshotRow>> GetDailyDatasetAsync(LayerSourceContext layer, CancellationToken ct = default) =>
        Task.FromResult(_generator.Generate(DateTime.UtcNow));

    // Demo mode has no live orders to refresh against.
    public Task<IReadOnlyList<BatchSaleRow>> GetApprovedBatchSalesAsync(
        LayerSourceContext layer, DateTime sinceUtc, IReadOnlyDictionary<string, decimal> skuPushedPrices, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BatchSaleRow>>(Array.Empty<BatchSaleRow>());
}
