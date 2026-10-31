namespace PricingTool.Agent.Storage;

/// <summary>
/// One SKU's landed cost (PPTCV, incl. VAT, layer currency) seen by the cost watcher's feed (plan section 5,
/// "CostWatcher"): from the replica's ProductPricing when it refreshes, or an injected, labelled test row. The watcher
/// processes each once and records what it did.
/// </summary>
public class CostObservation
{
    public long Id { get; set; }
    public int LayerId { get; set; }
    public string Sku { get; set; } = "";
    public decimal Pptcv { get; set; }
    public DateTime ObservedUtc { get; set; }

    /// <summary>replica (ProductPricing refreshed at <see cref="ObservedUtc"/>) or "[INJECTED-TEST] ..." .</summary>
    public string Source { get; set; } = "";

    public DateTime? ProcessedUtc { get; set; }

    /// <summary>raised / held-data-error / ended-our-discount / held-cost-above-shelf / held-team-discount / held-own-push (the calculator's
    /// earlier push, imported by the team) / held-not-published (changed since the run, or didn't read back) / above-floor / not-a-rise /
    /// unknown-sku.</summary>
    public string? Outcome { get; set; }
    public long? DecisionId { get; set; }
    public long? PublishBatchId { get; set; }
}
