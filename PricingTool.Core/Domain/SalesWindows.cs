namespace PricingTool.Core.Domain;

/// <summary>
/// The source ships trailing sales only for fixed windows of 7, 14, 30, 60 and 90 days. Anything that
/// asks "did it sell in the last N days?" snaps N up to the smallest window that covers it.
/// </summary>
public static class SalesWindows
{
    /// <summary>Units sold within the smallest source window covering <paramref name="days"/> — floored at
    /// the 7-day window, capped at the 90-day one.</summary>
    public static int UnitsCovering(int days, int qty7, int qty14, int qty30, int qty60, int qty90) => days switch
    {
        <= 7 => qty7,
        <= 14 => qty14,
        <= 30 => qty30,
        <= 60 => qty60,
        _ => qty90,
    };
}
