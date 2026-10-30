namespace PricingTool.Agent.Tools;

/// <summary>A half-open date window [From, To).</summary>
public readonly record struct DateWindow(DateOnly From, DateOnly To)
{
    public int Days => To.DayNumber - From.DayNumber;
    public static DateWindow Empty => new(DateOnly.MinValue, DateOnly.MinValue);

    /// <summary>The <paramref name="days"/> days ending <paramref name="lagDays"/> before <paramref name="today"/>.</summary>
    public static DateWindow Ending(DateOnly today, int days, int lagDays)
    {
        var to = today.AddDays(-lagDays);
        return new DateWindow(to.AddDays(-days), to);
    }

    /// <summary>The same weeks one year earlier: 364 days back, so weekdays line up.</summary>
    public DateWindow SameWeeksLastYear() => new(From.AddDays(-364), To.AddDays(-364));

    public override string ToString() => $"{From:yyyy-MM-dd} to {To.AddDays(-1):yyyy-MM-dd}";
}

public sealed record LayerInfo(int LayerId, string LayerName, string Brand, string Currency, decimal VatRatePct)
{
    /// <summary>The category level decisions are made at: GjirafaMall's main groups, Gjirafa50's L1.</summary>
    public string CategoryLevel => Brand == "GjirafaMall" ? "MainGroup" : "CategoryL1";
    public bool IsMall => Brand == "GjirafaMall";
}

public sealed record BandConfigRow(int BandId, string BandName, decimal MinCost, decimal MaxCost,
    decimal MarginFloorPct, decimal? TargetMarginPct, int SortOrder);

/// <summary>Sales of one band × category in one period, split by whether the day was a campaign day.
/// Money is EUR, VAT excluded; gross profit is on booked cost.</summary>
public sealed record SalesAggregate(
    int? BandId, string Category, int Period, bool CampaignDay,
    decimal Units, decimal NetRevenue, decimal GrossProfit,
    decimal NetBelowFloor, decimal NetAtFloor, decimal NetCrossDock,
    int Skus, int SkusWithElasticity, decimal NetWithElasticity);

/// <summary>Local-warehouse stock of one band × category in the latest engine run, at PPTCV (cost incl. VAT).</summary>
public sealed record StockAggregate(
    int? BandId, string Category, int SkusInStock, decimal StockValue, decimal DeadStockValue, int? MaxDeadObservedDays);

public sealed record CostChangeAggregate(int? BandId, string Category, int SkusWithCostData, int SkusCostUp5, int SkusCostDown5);

public sealed record CostChangeRow(string Sku, int? BandId, string Category, decimal UnitCostPrior60, decimal UnitCostLast30, decimal ChangePct);

public sealed record ElasticityFit(string Sku, int? BandId, string Category, bool IsUsable, decimal Coefficient, int ObservationCount);

public sealed record CampaignWindow(int CampaignId, string Name, DateOnly StartDate, DateOnly EndDate, string CampaignType);

/// <summary>One day's sales of a band × category (the row) and of the layer's holdout categories, booked cost, EUR VAT excl.</summary>
public sealed record DaySales(DateOnly Day, decimal RowUnits, decimal RowGrossProfit, decimal HoldoutUnits, decimal HoldoutGrossProfit);

/// <summary>Sales of one category in one window around a past campaign: 0 = the campaign, 1 = the 28 days before,
/// 2 = the campaign's days a year earlier, 3 = the 28 days before those (each without other campaigns' days).</summary>
/// <c>Discount</c> and <c>PreDiscountGross</c> are VAT-inclusive, over lines with a plausible discount (at most 80 % off).
public sealed record CampaignSegmentRow(int CampaignId, int Segment, string Category, decimal Units, decimal NetRevenue, decimal GrossProfit,
    decimal Discount, decimal PreDiscountGross, int Lines, int DiscountedLines);

/// <summary>One week's sales (week 0 = the first), this year or the same week a year earlier (364 days back).</summary>
public sealed record WeekSales(int Week, bool LastYear, decimal Units, decimal NetRevenue, decimal GrossProfit, decimal Discount, decimal PreDiscountGross,
    int Lines, int DiscountedLines)
{
    /// <summary>Discount depth in percent, or null where the lines don't record discounts (see <see cref="Campaigns.SegmentSales.DepthRecorded"/>).</summary>
    public decimal? DepthPct => Lines > 0 && DiscountedLines >= Campaigns.SegmentSales.MinDiscountedLineShare * Lines && PreDiscountGross > 0
        ? 100m * Discount / PreDiscountGross : null;
}

/// <summary>A per-category floor/target override in force (PricingTool.BandCategoryRules).</summary>
public sealed record CategoryRuleRow(int BandId, string Category, decimal? MarginFloorPct, decimal? TargetMarginPct, string UpdatedBy, DateTime UpdatedUtc);

/// <summary>Which engine run the stock figures come from.</summary>
public sealed record EngineRunRef(long RunId, DateOnly SnapshotDate);
