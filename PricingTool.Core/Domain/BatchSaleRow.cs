namespace PricingTool.Core.Domain;

/// <summary>
/// One approved-batch SKU's live-source refresh row: units and net (excl. VAT) sold since the batch's
/// push date across BOTH platforms of the country, plus the SKU's CURRENT local-warehouse stock. Feeds
/// the dead-stock "is it clearing?" dashboard (sold-since-push + the sold-out list). CurrentStock is 0
/// when the SKU has no local stock left (sold out / delisted). <paramref name="CompletedSoldUnits"/> is
/// the subset of <paramref name="SoldUnits"/> in COMPLETED orders only (OrderStatusId = 30); the wider
/// <paramref name="SoldUnits"/> also counts still-processing (status 20) orders.
/// <paramref name="ActiveDiscountIds"/> is the comma-joined live customer-facing discount id(s) on this
/// platform PRICED AT THE PUSHED PRICE (the promo(s) we created to carry the clearance price, to delete once
/// cleared); null when no live discount currently matches that price.
/// </summary>
public readonly record struct BatchSaleRow(
    string Sku, int SoldUnits, decimal SoldNet, int CompletedSoldUnits, int CurrentStock, string? ActiveDiscountIds);
