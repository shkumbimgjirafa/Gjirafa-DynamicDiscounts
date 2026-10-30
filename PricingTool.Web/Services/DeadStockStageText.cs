using PricingTool.Core.Domain;

namespace PricingTool.Web.Services;

/// <summary>
/// Owner-facing labels for the dead-stock stages. The month ranges describe how long a SKU has gone
/// WITHOUT SALES (the tool's snapshot-tracked observed-dead duration), not physical warehouse age. The
/// names are fixed while the underlying per-band thresholds are editable — UI surfaces that show a label
/// next to numbers should tooltip the band's actual thresholds so an edited band doesn't silently lie.
/// </summary>
public static class DeadStockStageText
{
    public static string Label(DeadStockStage stage) => stage switch
    {
        DeadStockStage.Stage1 => "3–6 months",
        DeadStockStage.Stage2 => "6–12 months",
        DeadStockStage.Stage3 => "12–36 months",
        DeadStockStage.Stage4 => "36 months+",
        _ => "",
    };
}
