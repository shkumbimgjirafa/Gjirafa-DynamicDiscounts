using System.Globalization;
using System.Text;
using PricingTool.Agent.Tools;

namespace PricingTool.Agent.Alpha;

/// <summary>One band × category of the scorecard. Percentages are 0–100. Sales are normal days only
/// (campaign days are excluded in both years and shown separately), EUR, VAT excluded, booked COGS.</summary>
public sealed record ScorecardRow
{
    public int? BandId { get; init; }
    public string BandName { get; init; } = "";
    public string Category { get; init; } = "";
    public decimal? FloorPct { get; init; }
    public decimal? TargetPct { get; init; }

    public decimal Units { get; init; }
    public decimal NetRevenue { get; init; }
    public decimal GrossProfit { get; init; }
    public decimal? MarginPct { get; init; }

    public decimal LyUnits { get; init; }
    public decimal LyNetRevenue { get; init; }
    public decimal LyGrossProfit { get; init; }
    public decimal? LyMarginPct { get; init; }

    /// <summary>Share of normal-day revenue sold below today's floor / at it / cross-dock.</summary>
    public decimal? BelowFloorPct { get; init; }
    public decimal? AtFloorPct { get; init; }
    public decimal? CrossDockPct { get; init; }

    /// <summary>Revenue on campaign days, left out of the figures above.</summary>
    public decimal CampaignNetRevenue { get; init; }
    public decimal LyCampaignNetRevenue { get; init; }

    public int SkusSold { get; init; }
    public int SkusWithElasticity { get; init; }
    /// <summary>Share of normal-day revenue from SKUs with a usable elasticity fit.</summary>
    public decimal? ElasticityCoveragePct { get; init; }

    public int SkusCostUp5 { get; init; }
    public int SkusCostDown5 { get; init; }

    public int SkusInStock { get; init; }
    public decimal StockValue { get; init; }
    public decimal? DeadStockPct { get; init; }

    /// <summary>Fewer normal-day units than <see cref="ScorecardOptions.ThinDataUnits"/>: keep the floor.</summary>
    public bool ThinData { get; init; }
}

/// <summary>What Alpha judges for one layer: one row per band × category, computed in code.</summary>
public sealed record AlphaScorecard
{
    public required LayerInfo Layer { get; init; }
    public required DateWindow Window { get; init; }
    public required DateWindow LastYear { get; init; }
    public required int ThinDataUnits { get; init; }
    public required IReadOnlyList<CampaignWindow> CampaignsExcluded { get; init; }
    public EngineRunRef? StockRun { get; init; }
    public required IReadOnlyList<ScorecardRow> Rows { get; init; }
    public required ScorecardRow Total { get; init; }
    public required IReadOnlyList<string> Definitions { get; init; }

    /// <summary>
    /// Merges the aggregates into rows. Pure: everything the agent reads as a number is computed here.
    /// </summary>
    public static AlphaScorecard Build(
        LayerInfo layer, DateWindow window, DateWindow lastYear, ScorecardOptions options,
        IReadOnlyList<BandConfigRow> bands, IReadOnlyList<SalesAggregate> sales, IReadOnlyList<StockAggregate> stock,
        IReadOnlyList<CostChangeRow> costChanges, IReadOnlyList<CampaignWindow> campaigns, EngineRunRef? stockRun)
    {
        var acc = new Dictionary<(int? Band, string Category), Accumulator>();
        Accumulator At(int? band, string category) =>
            acc.TryGetValue((band, category), out var a) ? a : acc[(band, category)] = new Accumulator(band, category);

        foreach (var s in sales) At(s.BandId, s.Category).Add(s);
        foreach (var s in stock) At(s.BandId, s.Category).Add(s);
        foreach (var c in costChanges) At(c.BandId, c.Category).Add(c);

        var bandById = bands.ToDictionary(b => b.BandId);
        var rows = acc.Values
            .Where(a => a.HasAnything)
            .Select(a => a.ToRow(bandById.GetValueOrDefault(a.BandId ?? -1), options.ThinDataUnits))
            .OrderBy(r => r.BandId is { } id && bandById.TryGetValue(id, out var b) ? b.SortOrder : int.MaxValue)
            .ThenByDescending(r => r.NetRevenue)
            .ThenBy(r => r.Category, StringComparer.Ordinal)
            .ToList();

        var total = new Accumulator(null, "all");
        foreach (var a in acc.Values) total.Merge(a);

        return new AlphaScorecard
        {
            Layer = layer,
            Window = window,
            LastYear = lastYear,
            ThinDataUnits = options.ThinDataUnits,
            CampaignsExcluded = campaigns,
            StockRun = stockRun,
            Rows = rows,
            Total = total.ToRow(null, options.ThinDataUnits, isTotal: true) with { BandName = "all bands" },
            Definitions = DefinitionsFor(layer, options),
        };
    }

    private static IReadOnlyList<string> DefinitionsFor(LayerInfo layer, ScorecardOptions o) => new[]
    {
        $"Category = {layer.CategoryLevel}. Money in {layer.Currency}, VAT excluded; gross profit = revenue minus booked cost (measured, sales lines).",
        "Sales figures are normal days only: campaign days are left out in both years; their revenue is camp_net / ly_camp_net.",
        "margin = gross profit / revenue of the band x category (sum first, then divide).",
        $"below_floor / at_floor: share of revenue whose line margin is more than {o.FloorTolerancePp} pt under today's floor / within that and {o.AtFloorBandPp} pt above it. Lines are banded by booked unit cost incl. VAT with today's band limits.",
        "xdock: share of revenue from units bought to order (arrived after the order).",
        "elast_cov: share of revenue from SKUs with a usable elasticity fit; elast_skus = how many such SKUs sold.",
        "cost_up5 / cost_down5: SKUs whose booked unit cost moved more than 5% (last 30 days vs the 60 before; only SKUs that sold in both).",
        "stock: local-warehouse units x PPTCV (cost incl. VAT) from the latest engine run, banded by the engine; dead = share of that value the engine stages as dead stock. GjirafaMall and Gjirafa50 KS share one warehouse: never add their stock.",
        $"thin = fewer than {o.ThinDataUnits} normal-day units in the window: keep the floor.",
    };

    /// <summary>A compact, pipe-separated table for the prompt: a header block, the column legend, one line
    /// per row and the layer total.</summary>
    public string ToPromptTable()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Scorecard: {Layer.LayerName} (layer {Layer.LayerId}), category level {Layer.CategoryLevel}, {Layer.Currency}");
        sb.AppendLine($"Window: {Window} ({Window.Days} days); same weeks last year: {LastYear}");
        sb.AppendLine(CampaignsExcluded.Count == 0
            ? "Campaign days excluded: none in either window"
            : "Campaign days excluded: " + string.Join("; ", CampaignsExcluded.Select(c => $"{c.Name} ({c.StartDate:yyyy-MM-dd} to {c.EndDate:yyyy-MM-dd})")));
        sb.AppendLine(StockRun is null
            ? "Stock: no engine run with a snapshot yet"
            : $"Stock: engine run #{StockRun.RunId}, snapshot {StockRun.SnapshotDate:yyyy-MM-dd}");
        foreach (var d in Definitions) sb.AppendLine("- " + d);
        sb.AppendLine();
        sb.AppendLine("band_id|band|category|floor|target|units|net|gp|margin|ly_units|ly_net|ly_margin|below_floor|at_floor|xdock|camp_net|ly_camp_net|skus|elast_skus|elast_cov|cost_up5|cost_down5|stock|dead|thin");
        foreach (var r in Rows) sb.AppendLine(Line(r));
        sb.AppendLine(Line(Total));
        return sb.ToString();
    }

    private static string Line(ScorecardRow r) => string.Join('|',
        r.BandId?.ToString(CultureInfo.InvariantCulture) ?? "-", r.BandName, r.Category,
        Pct(r.FloorPct), Pct(r.TargetPct),
        Num(r.Units), Num(r.NetRevenue), Num(r.GrossProfit), Pct(r.MarginPct),
        Num(r.LyUnits), Num(r.LyNetRevenue), Pct(r.LyMarginPct),
        Pct(r.BelowFloorPct), Pct(r.AtFloorPct), Pct(r.CrossDockPct),
        Num(r.CampaignNetRevenue), Num(r.LyCampaignNetRevenue),
        r.SkusSold.ToString(CultureInfo.InvariantCulture), r.SkusWithElasticity.ToString(CultureInfo.InvariantCulture), Pct(r.ElasticityCoveragePct),
        r.SkusCostUp5.ToString(CultureInfo.InvariantCulture), r.SkusCostDown5.ToString(CultureInfo.InvariantCulture),
        Num(r.StockValue), Pct(r.DeadStockPct), r.ThinData ? "thin" : "");

    private static string Num(decimal v) => Math.Round(v, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
    private static string Pct(decimal? v) => v is null ? "-" : Math.Round(v.Value, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Raw sums for one band × category; ratios are taken only at the end.</summary>
    private sealed class Accumulator
    {
        public Accumulator(int? bandId, string category) { BandId = bandId; Category = category; }
        public int? BandId { get; }
        public string Category { get; }

        decimal _units, _net, _gp, _lyUnits, _lyNet, _lyGp, _below, _at, _xdock, _campNet, _lyCampNet, _netElastic, _stock, _dead;
        int _skus, _skusElastic, _skusInStock, _costUp, _costDown;

        public bool HasAnything => _units != 0 || _lyUnits != 0 || _campNet != 0 || _lyCampNet != 0 || _stock != 0 || _costUp + _costDown > 0;

        public void Add(SalesAggregate s)
        {
            if (s.CampaignDay)
            {
                if (s.Period == 0) _campNet += s.NetRevenue; else _lyCampNet += s.NetRevenue;
                return;
            }
            if (s.Period != 0)
            {
                _lyUnits += s.Units; _lyNet += s.NetRevenue; _lyGp += s.GrossProfit;
                return;
            }
            _units += s.Units; _net += s.NetRevenue; _gp += s.GrossProfit;
            _below += s.NetBelowFloor; _at += s.NetAtFloor; _xdock += s.NetCrossDock;
            _skus += s.Skus; _skusElastic += s.SkusWithElasticity; _netElastic += s.NetWithElasticity;
        }

        public void Add(StockAggregate s) { _skusInStock += s.SkusInStock; _stock += s.StockValue; _dead += s.DeadStockValue; }

        public void Add(CostChangeRow c)
        {
            if (c.ChangePct > 0.05m) _costUp++;
            else if (c.ChangePct < -0.05m) _costDown++;
        }

        public void Merge(Accumulator o)
        {
            _units += o._units; _net += o._net; _gp += o._gp; _lyUnits += o._lyUnits; _lyNet += o._lyNet; _lyGp += o._lyGp;
            _below += o._below; _at += o._at; _xdock += o._xdock; _campNet += o._campNet; _lyCampNet += o._lyCampNet;
            _netElastic += o._netElastic; _stock += o._stock; _dead += o._dead;
            _skus += o._skus; _skusElastic += o._skusElastic; _skusInStock += o._skusInStock; _costUp += o._costUp; _costDown += o._costDown;
        }

        /// <summary>Below / at floor need a floor: a row without a band has none, the layer total sums
        /// each line against its own band's floor.</summary>
        public ScorecardRow ToRow(BandConfigRow? band, int thinDataUnits, bool isTotal = false) => new()
        {
            BandId = BandId,
            BandName = band?.BandName ?? (BandId is null ? "no band" : $"band {BandId}"),
            Category = Category,
            FloorPct = band?.MarginFloorPct,
            TargetPct = band?.TargetMarginPct,
            Units = _units, NetRevenue = _net, GrossProfit = _gp, MarginPct = Ratio(_gp, _net),
            LyUnits = _lyUnits, LyNetRevenue = _lyNet, LyGrossProfit = _lyGp, LyMarginPct = Ratio(_lyGp, _lyNet),
            BelowFloorPct = band is null && !isTotal ? null : Ratio(_below, _net),
            AtFloorPct = band is null && !isTotal ? null : Ratio(_at, _net),
            CrossDockPct = Ratio(_xdock, _net),
            CampaignNetRevenue = _campNet, LyCampaignNetRevenue = _lyCampNet,
            SkusSold = _skus, SkusWithElasticity = _skusElastic, ElasticityCoveragePct = Ratio(_netElastic, _net),
            SkusCostUp5 = _costUp, SkusCostDown5 = _costDown,
            SkusInStock = _skusInStock, StockValue = _stock, DeadStockPct = Ratio(_dead, _stock),
            ThinData = _units < thinDataUnits,
        };

        private static decimal? Ratio(decimal part, decimal whole) => whole > 0 ? 100m * part / whole : null;
    }
}
