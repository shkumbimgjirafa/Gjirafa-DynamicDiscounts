namespace PricingTool.Data.Entities;

/// <summary>Per-band, per-algorithm enable flag and weight (0–100).</summary>
public class BandAlgorithmSetting
{
    public int Id { get; set; }
    public int PriceBandId { get; set; }
    public PriceBand PriceBand { get; set; } = null!;

    public string AlgorithmCode { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int Weight { get; set; }
}
