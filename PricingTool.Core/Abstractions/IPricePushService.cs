namespace PricingTool.Core.Abstractions;

public record ApprovedPrice(
    long ProposedPriceId,
    string Sku,
    decimal OldPrice,
    decimal CurrentPrice,
    decimal NewPrice,
    long RunId,
    string ApprovedBy);

public record PushResult(bool Success, string Detail);

/// <summary>
/// Integration point for pushing approved prices into the live platform.
/// THE ENGINE NEVER CALLS THIS. Only the explicit, human-triggered push action in the admin UI
/// does, and only for proposals a Manager approved. The v1 implementation exports a CSV /
/// staging file for the platform team; they wire the real NopCommerce write-back later
/// (open decision #6).
/// </summary>
public interface IPricePushService
{
    Task<PushResult> PushAsync(IReadOnlyList<ApprovedPrice> prices, CancellationToken ct = default);
}
