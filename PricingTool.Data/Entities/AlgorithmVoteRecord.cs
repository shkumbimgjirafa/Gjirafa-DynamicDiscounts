namespace PricingTool.Data.Entities;

/// <summary>Every algorithm's vote for one proposal — full per-SKU explainability per run.</summary>
public class AlgorithmVoteRecord
{
    public long Id { get; set; }
    public long ProposedPriceId { get; set; }
    public ProposedPrice ProposedPrice { get; set; } = null!;

    public string AlgorithmCode { get; set; } = "";
    public decimal SuggestedPrice { get; set; }
    public decimal Confidence { get; set; }
    public int BandWeight { get; set; }
    public decimal EffectiveWeight { get; set; }
    public string ReasonCode { get; set; } = "";
    public string ReasonText { get; set; } = "";
}
