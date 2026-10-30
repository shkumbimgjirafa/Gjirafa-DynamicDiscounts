namespace PricingTool.Data.Entities;

/// <summary>
/// One SKU that moved or cleared during a sealed dead-stock window (child of <see cref="DeadStockWindowLog"/>).
/// Captured for every batch SKU with <c>SoldUnits &gt; 0</c> OR fully cleared (<c>StockAtPush &gt; 0 AND
/// CurrentStock == 0</c>). The user's "SKUs cleared in the window + their value" = the rows with
/// <see cref="IsClearedOut"/>, valued by <see cref="SoldNet"/>.
/// </summary>
public class DeadStockWindowClearedItem
{
    public long Id { get; set; }

    public long DeadStockWindowLogId { get; set; }
    public DeadStockWindowLog DeadStockWindowLog { get; set; } = null!;

    public string Sku { get; set; } = "";
    public string? ProductName { get; set; }
    public string? Brand { get; set; }

    /// <summary>Price band label at push (display only), e.g. "250–500".</summary>
    public string? Band { get; set; }

    /// <summary>The price that was pushed live for this SKU.</summary>
    public decimal PushedPrice { get; set; }

    /// <summary>PPTCV (all-in cost) at push. Null when unknown.</summary>
    public decimal? Pptcv { get; set; }

    /// <summary>Local-warehouse stock at push — the units we were trying to clear for this SKU.</summary>
    public int StockAtPush { get; set; }

    /// <summary>Units sold since push (valid orders, both platforms), as of the seal.</summary>
    public int SoldUnits { get; set; }

    /// <summary>Net value sold since push (VAT-exclusive), as of the seal.</summary>
    public decimal SoldNet { get; set; }

    /// <summary>True when the SKU fully cleared — had local stock at push and now has none.</summary>
    public bool IsClearedOut { get; set; }
}
