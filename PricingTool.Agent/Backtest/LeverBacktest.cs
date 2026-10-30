namespace PricingTool.Agent.Backtest;

/// <summary>One booked sales line (money columns are line totals; Cogs booked at the time of sale).</summary>
public sealed record BacktestLine(DateOnly Day, string Sku, decimal Qty, decimal Net, decimal Gross, decimal Cogs, decimal Gp, decimal Discount,
    string StockClass, decimal? UnitCostInclVat, int? BandId, string Category, bool CampaignDay)
{
    public decimal UnitNet => Net / Qty;
    public decimal UnitCogs => Cogs / Qty;
    public decimal UnitGross => Gross / Qty;
    public decimal VatFactor => Net > 0 && Gross > 0 ? Gross / Net : 1m;
}

/// <summary>One lever's gain in one month at one elasticity, with the lines and units behind it.</summary>
public sealed record LeverGain(int Lever, string Month, decimal Elasticity, decimal Gain, int Lines, decimal Units);

/// <summary>Measured figures the levers rest on, per month.</summary>
public sealed record LeverContext(string Month, decimal CrossDockLoss, int CrossDockLossLines, decimal CaughtLoss, decimal CampaignDayCrossDockLoss);

public sealed record LeverBacktestResult(int LayerId, DateOnly From, DateOnly To, IReadOnlyList<LeverGain> Gains, IReadOnlyList<LeverContext> Context);

/// <summary>
/// Backtest levers 1–3 (plan section 5, "Backtester") on booked sales lines, walk-forward (each line is judged only on the lines
/// before it), each line counted by one lever at most, in the plan's order: loss block, per-category floor, cost reaction.
/// Campaign days are left to lever 7. Units at a changed price scale by (new / old)^elasticity, for each elasticity case.
/// <list type="bullet">
/// <item>1, loss-sale block: a cross-dock line sold under its booked cost, when the cost the SKU last sold at was already above
/// the price (the tool would have known it); the loss is the gain (units sold at a loss cost nothing to lose). Measured loss,
/// estimated catch (a cost that jumped at that very sale isn't counted).</item>
/// <item>2, per-category floors: where the agent's floor for the band × category is above the hand-set floor of that day, a line
/// under it is lifted to it; the gain is over the lift to the hand-set floor alone, so the band floor's own gain isn't claimed.
/// Never above a ceiling: the line's pre-discount price, or the SKU's highest price in the 60 days before (assumed).</item>
/// <item>3, cost reaction: a line whose unit cost is more than 5 % above the SKU's median of the 30 days before while its price
/// moved less than 1 %; priced to restore the prior margin (capped at the ceiling), from the day after the jump was first seen.</item>
/// </list>
/// </summary>
public static class LeverBacktest
{
    public static readonly decimal[] Elasticities = { -0.5m, -1.0m, -2.0m };
    public const decimal CostJumpPct = 5m, PriceStillPct = 1m;
    public const int ReferenceDays = 30, CeilingDays = 60, KnownCostDays = 60;

    public static LeverBacktestResult Run(int layerId, IReadOnlyList<BacktestLine> lines, DateOnly from, DateOnly to,
        Func<int, DateOnly, decimal?> handFloorPct, Func<int, string, decimal?> agentFloorPct, ICollection<(int Lever, BacktestLine Line, decimal Gain)>? trace = null)
    {
        var gains = new Dictionary<(int, string, decimal), (decimal Gain, int Lines, decimal Units)>();
        var context = new Dictionary<string, (decimal Loss, int LossLines, decimal Caught, decimal CampaignLoss)>();
        void Add(int lever, BacktestLine l, decimal e, decimal gain)
        {
            if (e == -1.0m) trace?.Add((lever, l, gain));
            var key = (lever, Month(l.Day), e);
            var (g, n, u) = gains.GetValueOrDefault(key);
            gains[key] = (g + gain, n + 1, u + l.Qty);
        }

        foreach (var sku in lines.Where(l => l.Qty > 0).GroupBy(l => l.Sku))
        {
            var hist = sku.OrderBy(l => l.Day).ToList();
            DateOnly? episodeStart = null;
            for (var i = 0; i < hist.Count; i++)
            {
                var l = hist[i];
                var before = hist.Take(i).Where(p => p.Day < l.Day).ToList();
                var inWindow = l.Day >= from && l.Day < to;

                // Lever 3's episode state runs over the whole history, so a jump seen before the window is already known in it.
                var reference = before.Where(p => p.Day >= l.Day.AddDays(-ReferenceDays)).ToList();
                var refCost = reference.Count > 0 ? Median(reference.Select(p => p.UnitCogs)) : (decimal?)null;
                var refPrices = reference.Where(p => !p.CampaignDay).Select(p => p.UnitNet).ToList();
                var refPrice = refPrices.Count > 0 ? Median(refPrices) : (decimal?)null;
                var jumped = refCost is > 0 && refPrice is > 0 && l.UnitCogs > refCost * (1 + CostJumpPct / 100m) && l.UnitNet <= refPrice * (1 + PriceStillPct / 100m);
                var firstDayOfJump = false;
                if (jumped && !l.CampaignDay) { episodeStart ??= l.Day; firstDayOfJump = episodeStart == l.Day; }
                else if (!l.CampaignDay) episodeStart = null;

                if (!inWindow) continue;
                var month = Month(l.Day);
                var crossDockLoss = l.StockClass == "CrossDock" && l.Gp < 0;
                if (crossDockLoss && l.CampaignDay)
                {
                    var c = context.GetValueOrDefault(month);
                    context[month] = c with { CampaignLoss = c.CampaignLoss - l.Gp };
                }
                if (l.CampaignDay) continue;

                // 1. Loss-sale block.
                if (crossDockLoss)
                {
                    var known = before.LastOrDefault(p => p.Day >= l.Day.AddDays(-KnownCostDays));
                    var caught = known is not null && known.UnitCogs > l.UnitNet;
                    var c = context.GetValueOrDefault(month);
                    context[month] = c with { Loss = c.Loss - l.Gp, LossLines = c.LossLines + 1, Caught = c.Caught + (caught ? -l.Gp : 0) };
                    if (caught)
                    {
                        foreach (var e in Elasticities) Add(1, l, e, -l.Gp);
                        continue;
                    }
                }

                var ceiling = Math.Max(l.Discount > 0 ? (l.Gross + l.Discount) / l.Qty : 0m,
                    hist.Where(p => p.Day <= l.Day && p.Day >= l.Day.AddDays(-CeilingDays)).Max(p => p.UnitGross));

                // 2. Per-category floors (engine basis: margin on the VAT-inclusive price against cost incl. VAT).
                if (l.BandId is { } band && l.UnitCostInclVat is > 0 and var cost && handFloorPct(band, l.Day) is { } fH
                    && agentFloorPct(band, l.Category) is { } fA && fA > fH)
                {
                    var p = l.UnitGross;
                    var toHand = Math.Min(Math.Max(p, cost / (1 - fH / 100m)), Math.Max(p, ceiling));
                    var toAgent = Math.Min(Math.Max(p, cost / (1 - fA / 100m)), Math.Max(p, ceiling));
                    if (toAgent > toHand)
                    {
                        foreach (var e in Elasticities) Add(2, l, e, Gp(l, toAgent, e) - Gp(l, toHand, e));
                        continue;
                    }
                }

                // 3. Cost reaction, from the day after the jump was first seen.
                if (jumped && !firstDayOfJump)
                {
                    var priorMargin = (refPrice!.Value - refCost!.Value) / refPrice.Value;
                    if (priorMargin > 0)
                    {
                        var target = Math.Min(l.UnitCogs / (1 - priorMargin), ceiling / l.VatFactor);
                        if (target > l.UnitNet)
                            foreach (var e in Elasticities) Add(3, l, e, Gp(l, target * l.VatFactor, e) - l.Gp);
                    }
                }
            }
        }

        return new LeverBacktestResult(layerId, from, to,
            gains.Select(kv => new LeverGain(kv.Key.Item1, kv.Key.Item2, kv.Key.Item3, Math.Round(kv.Value.Gain, 0), kv.Value.Lines, kv.Value.Units))
                .OrderBy(g => g.Lever).ThenBy(g => g.Month).ThenByDescending(g => g.Elasticity).ToList(),
            context.Select(kv => new LeverContext(kv.Key, Math.Round(kv.Value.Loss, 0), kv.Value.LossLines, Math.Round(kv.Value.Caught, 0), Math.Round(kv.Value.CampaignLoss, 0)))
                .OrderBy(c => c.Month).ToList());
    }

    /// <summary>The line's gross profit at another VAT-inclusive unit price, units scaled by the elasticity.</summary>
    public static decimal Gp(BacktestLine l, decimal unitGross, decimal elasticity)
    {
        var units = l.Qty * (decimal)Math.Pow((double)(unitGross / l.UnitGross), (double)elasticity);
        return units * (unitGross / l.VatFactor - l.UnitCogs);
    }

    public static string Month(DateOnly d) => d.ToString("yyyy-MM");

    private static decimal Median(IEnumerable<decimal> values)
    {
        var s = values.OrderBy(v => v).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2m;
    }
}
