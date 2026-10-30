namespace PricingTool.Data.Entities;

/// <summary>
/// One SKU in an <see cref="ApprovedBatch"/>: the price that was pushed, its context at push time,
/// and the sales it has recorded since (refreshed from the live source). Sold units/net are the
/// "is the clearance working?" signal on the dashboard.
/// </summary>
public class ApprovedBatchItem
{
    public long Id { get; set; }

    public long ApprovedBatchId { get; set; }
    public ApprovedBatch ApprovedBatch { get; set; } = null!;

    public string Sku { get; set; } = "";
    public string? ProductName { get; set; }
    public string? Brand { get; set; }

    /// <summary>Price band label at push (display only), e.g. "250–500".</summary>
    public string? Band { get; set; }

    /// <summary>Shelf / list ("was") price at push time.</summary>
    public decimal OldPrice { get; set; }

    /// <summary>The price the shopper saw immediately BEFORE this push (the batch's starting point).</summary>
    public decimal PrePushPrice { get; set; }

    /// <summary>The approved price that was pushed live.</summary>
    public decimal PushedPrice { get; set; }

    /// <summary>PPTCV (all-in cost) at push. Null when unknown.</summary>
    public decimal? Pptcv { get; set; }

    /// <summary>Local-warehouse stock at push — the units we're trying to clear.</summary>
    public int StockAtPush { get; set; }

    // ---- Refreshed from the live source: sales since the batch's PushedUtc (both platforms of the
    // country, valid orders). Zero until the SKU starts moving. ----
    public int SoldUnits { get; set; }
    public decimal SoldNet { get; set; }

    /// <summary>Units sold since push in COMPLETED orders only (OrderStatusId = 30) — a subset of
    /// <see cref="SoldUnits"/>, which also counts still-processing (status 20) orders. The
    /// "confirmed cleared" figure shown as "Completed sold" on the sold-out export.</summary>
    public int CompletedSoldUnits { get; set; }

    /// <summary>Comma-joined LIVE customer-facing discount id(s) on the SKU (this platform) whose price equals
    /// <see cref="PushedPrice"/>, refreshed alongside the sales figures — the promo(s) we created to carry the
    /// pushed clearance price. Surfaced on the sold-out export so the DB clean-up can delete them once the SKU
    /// has cleared; a sold-out SKU with none is dropped from that export (nothing of ours left to delete).
    /// Null when no live discount currently matches the pushed price.</summary>
    public string? ActiveDiscountIds { get; set; }

    /// <summary>
    /// The SKU's CURRENT local-warehouse stock, refreshed alongside <see cref="SoldUnits"/>. Null until
    /// the first refresh. A SKU with <see cref="StockAtPush"/> &gt; 0 and CurrentStock == 0 has cleared —
    /// it's in the "sold out" list on the dead-stock dashboard.
    /// </summary>
    public int? CurrentStock { get; set; }
}
