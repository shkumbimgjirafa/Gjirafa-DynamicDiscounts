using PricingTool.Core.Domain;

namespace PricingTool.Core.Services;

/// <summary>
/// Conflict resolution between algorithm votes: weighted average where each vote's weight is
/// (per-band admin weight 0..100) × (vote confidence 0..1). Algorithms that returned null
/// simply do not participate. No votes → no price change.
/// </summary>
public class WeightedScoringService
{
    public record ScoringResult(decimal? RawPrice, IReadOnlyList<WeightedVote> Votes);

    public ScoringResult Combine(SkuContext ctx, IReadOnlyList<(IPricingAlgorithm Algorithm, AlgorithmVote Vote)> votes)
    {
        var weighted = new List<WeightedVote>();
        decimal totalWeight = 0, weightedSum = 0;

        foreach (var (algorithm, vote) in votes)
        {
            var setting = ctx.Band.GetAlgorithm(algorithm.Code);
            if (!setting.Enabled || setting.Weight <= 0) continue;

            var confidence = Math.Clamp(vote.Confidence, 0m, 1m);
            var effective = setting.Weight * confidence;

            weighted.Add(new WeightedVote(
                algorithm.Code, vote.SuggestedPrice, confidence,
                setting.Weight, effective, vote.ReasonCode, vote.ReasonText));

            if (effective <= 0) continue;
            totalWeight += effective;
            weightedSum += effective * vote.SuggestedPrice;
        }

        var ordered = weighted.OrderByDescending(v => v.EffectiveWeight).ToList();
        return totalWeight > 0
            ? new ScoringResult(weightedSum / totalWeight, ordered)
            : new ScoringResult(null, ordered);
    }
}
