namespace PricingTool.Data.Entities;

/// <summary>
/// The dead-stock pile of one COUNTRY on one day, deduplicated across the country's brand layers: they
/// share one warehouse, so a SKU dead on both Gjirafa50 and GjirafaMall is one SKU and one set of units.
/// Rebuilt from <see cref="DeadStockEpisode"/> at the end of each run of the country's layers (the last
/// run of the day leaves the final figures). Raw components only, like <see cref="DailyLayerKpi"/>;
/// never retention-trimmed.
///
/// Flows are measured against <see cref="PrevDate"/>, the country's previous row, so they reconcile:
/// previous DeadSkus + EnteredSkus − LeftSkus = DeadSkus.
/// </summary>
public class DeadStockDailyKpi
{
    public long Id { get; set; }

    public string CountryCode { get; set; } = "";

    public DateTime Date { get; set; }

    /// <summary>The country's previous KPI date — what <see cref="EnteredSkus"/>/<see cref="LeftSkus"/> compare
    /// against. Null on the country's first row.</summary>
    public DateTime? PrevDate { get; set; }

    /// <summary>Distinct SKUs dead on at least one of the country's layers.</summary>
    public int DeadSkus { get; set; }

    /// <summary>Σ local warehouse units of those SKUs (each SKU counted once).</summary>
    public long DeadUnits { get; set; }

    /// <summary>Σ PPTCV × local units — the all-in cost tied up in the pile.</summary>
    public decimal DeadCost { get; set; }

    /// <summary>The pile by age tier (a SKU's deepest tier across the country's layers).</summary>
    public int Stage1Skus { get; set; }
    public int Stage2Skus { get; set; }
    public int Stage3Skus { get; set; }
    public int Stage4Skus { get; set; }

    /// <summary>Dead today, not dead at <see cref="PrevDate"/>.</summary>
    public int EnteredSkus { get; set; }

    /// <summary>Dead at <see cref="PrevDate"/>, not dead today — split by the logged exit reason below.</summary>
    public int LeftSkus { get; set; }

    public int LeftCleared { get; set; }
    public int LeftRevived { get; set; }
    public int LeftRestocked { get; set; }
    public int LeftDelisted { get; set; }
    public int LeftOther { get; set; }
}
