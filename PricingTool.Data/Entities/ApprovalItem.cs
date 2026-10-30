namespace PricingTool.Data.Entities;

/// <summary>
/// One SKU in an <see cref="Approval"/>: the price move that was approved. The SKU set drives the approval's
/// before/after profit query; <see cref="OldPrice"/>/<see cref="NewPrice"/> record the move and classify it
/// as a rise or a cut.
/// </summary>
public class ApprovalItem
{
    public long Id { get; set; }

    public long ApprovalId { get; set; }
    public Approval Approval { get; set; } = null!;

    public string Sku { get; set; } = "";

    /// <summary>The live price the shopper was paying immediately before this approval (the proposal's CurrentPrice).</summary>
    public decimal OldPrice { get; set; }

    /// <summary>The approved price (the proposal's ProposedPriceValue).</summary>
    public decimal NewPrice { get; set; }
}
