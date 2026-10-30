namespace PricingTool.Core.Domain;

/// <summary>
/// One ISO-week bucket of realized profit for a whole layer, read from SR_ProductsData:
/// <c>Profit = SUM(Margin) = SUM(NetoPrice − Cogs)</c> — the actual VAT-net gross profit earned that week
/// using the real landed cost recorded on each order line (not today's PPTCV). <see cref="WeekStart"/> is
/// the Monday the bucket starts on. Powers the dashboard's general weekly-profit bar chart.
/// </summary>
public record WeeklyProfitPoint(DateTime WeekStart, decimal Profit, long Units, decimal NetRevenue);

/// <summary>
/// Realized profit (SR <c>SUM(Margin)</c>) for one approval's SKU set in the 7/30/90 days BEFORE vs AFTER
/// the approval date. "After" figures reflect sales recorded so far and are only final once the window has
/// fully elapsed past the approval date. Correlational (market drift not separated out).
/// </summary>
public record ApprovalProfitWindows(
    decimal Profit7Before, decimal Profit7After,
    decimal Profit30Before, decimal Profit30After,
    decimal Profit90Before, decimal Profit90After);
