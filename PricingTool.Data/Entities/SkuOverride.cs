namespace PricingTool.Data.Entities;

/// <summary>Per-SKU overrides; v1 carries the psychological-rounding opt-out required by the spec.</summary>
public class SkuOverride
{
    public int Id { get; set; }

    /// <summary>The layer this override applies to — same SKU can differ across layers.</summary>
    public int LayerId { get; set; }

    public string Sku { get; set; } = "";
    public bool RoundingDisabled { get; set; }
    public string? Note { get; set; }
}
