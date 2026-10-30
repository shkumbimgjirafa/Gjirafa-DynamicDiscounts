namespace PricingTool.Core.Domain;

/// <summary>
/// A single algorithm's opinion for one SKU.
/// SuggestedPrice is the VAT-inclusive shelf price the algorithm wants.
/// Confidence is 0..1 and multiplies the band's admin weight during scoring.
/// </summary>
public record AlgorithmVote(decimal SuggestedPrice, decimal Confidence, string ReasonCode, string ReasonText);
