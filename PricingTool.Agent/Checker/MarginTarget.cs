using Microsoft.Data.SqlClient;
using PricingTool.Agent.Tools;

namespace PricingTool.Agent.Checker;

/// <summary>A product of a run that sold on normal days, with what the margin target needs: the run's prices, its cost
/// (PPTCV, VAT included, as the price) and the units it sold.</summary>
public sealed record ShelfRow(long ProposalId, string Sku, decimal OldPrice, decimal CurrentPrice, decimal AnchorPrice, decimal Cost, int? BandId,
    bool DeadStock, bool SyncedToGj50, bool PersonSet, decimal Units);

public interface IShelfSource
{
    /// <summary>The run's products that sold on normal days in [<paramref name="from"/>, <paramref name="to"/>) (campaign days
    /// left out), with the units each sold.</summary>
    Task<IReadOnlyList<ShelfRow>> SoldAsync(long runId, int layerId, DateOnly from, DateOnly to, CancellationToken ct = default);
}

public sealed class SqlShelfSource : IShelfSource
{
    private readonly AgentDataSource _source;
    public SqlShelfSource(AgentDataSource source) => _source = source;

    public async Task<IReadOnlyList<ShelfRow>> SoldAsync(long runId, int layerId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_source.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            WITH s AS (
                SELECT sl.Sku, SUM(sl.Qty) AS Units
                FROM agent.SalesLines sl
                WHERE sl.LayerId = @L AND sl.SaleDate >= @From AND sl.SaleDate < @To
                  AND NOT EXISTS (SELECT 1 FROM agent.CampaignHistory c WHERE c.LayerId = @L AND sl.SaleDate BETWEEN c.StartDate AND c.EndDate)
                GROUP BY sl.Sku
                HAVING SUM(sl.Qty) > 0)
            SELECT pp.Id, pp.Sku, pp.OldPrice, pp.CurrentPrice, ISNULL(pp.AnchorPrice, 0), pp.Pptcv, pp.PriceBandId,
                   CAST(CASE WHEN pp.DeadStockStage <> 0 OR pp.ReasonCodes LIKE '%DEAD_STOCK%' OR pp.GuardrailFlags LIKE '%DEAD_STOCK%'
                                  OR pp.GuardrailFlags LIKE '%CLEARANCE_LOCK%' THEN 1 ELSE 0 END AS bit),
                   CAST(CASE WHEN pp.ReasonCodes LIKE '%SYNCED_TO_GJ50%' THEN 1 ELSE 0 END AS bit),
                   CAST(CASE WHEN pp.ManualOverrideBy IS NOT NULL THEN 1 ELSE 0 END AS bit),
                   s.Units
            FROM s
            JOIN PricingTool.ProposedPrices pp ON pp.PricingRunId = @Run AND pp.Sku = s.Sku
            WHERE pp.Pptcv > 0 AND pp.CurrentPrice > 0
            """, conn) { CommandTimeout = 300 };
        cmd.Parameters.AddWithValue("@L", layerId);
        cmd.Parameters.AddWithValue("@Run", runId);
        cmd.Parameters.Add("@From", System.Data.SqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
        cmd.Parameters.Add("@To", System.Data.SqlDbType.Date).Value = to.ToDateTime(TimeOnly.MinValue);
        var rows = new List<ShelfRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new ShelfRow(Convert.ToInt64(r.GetValue(0)), r.GetString(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5),
                r.IsDBNull(6) ? null : Convert.ToInt32(r.GetValue(6)), r.GetBoolean(7), r.GetBoolean(8), r.GetBoolean(9), Convert.ToDecimal(r.GetValue(10))));
        return rows;
    }
}

/// <summary>A product as the margin target sees it: the price it would have today, its cost, units and ceiling.</summary>
public sealed record TargetProduct(string Sku, decimal Price, decimal Cost, decimal Units, decimal Ceiling, bool Raisable);

/// <summary>What the margin target did: the shop's effective margin before and after, the per-product minimum it took, the
/// raised prices, and how many stopped at their ceiling.</summary>
public sealed record TargetOutcome(decimal TargetPct, decimal BeforePct, decimal AfterPct, decimal? MinimumPct, IReadOnlyDictionary<string, decimal> Raised,
    int AtCeiling)
{
    public bool Reached => AfterPct >= TargetPct - 0.05m;
}

/// <summary>
/// A shop's effective margin target for a day (a person's <c>margin-target</c> rule): the effective margin is the shop's gross
/// profit over its net revenue (both VAT excluded; on the VAT-inclusive price and cost it is the same ratio), weighted by
/// what each product sold. Code finds the lowest per-product margin m that brings the shop to the target and raises every
/// raisable product under m up to it, never above its ceiling (max(regular, today's price)), never lowering a price. Volume
/// is held as it was (estimated): a raise sells less, which the estimate doesn't count.
/// </summary>
public static class MarginTarget
{
    /// <param name="priceAt">A product's price at a per-product minimum margin (percent): charm-rounded, within [today's price, ceiling].</param>
    public static TargetOutcome Solve(IReadOnlyList<TargetProduct> products, decimal targetPct, Func<TargetProduct, decimal, decimal> priceAt)
    {
        static decimal MarginOf(decimal price, decimal cost) => price > 0 ? 100m * (price - cost) / price : 0m;
        decimal Blend(Func<TargetProduct, decimal> price)
        {
            decimal gp = 0, net = 0;
            foreach (var p in products)
            {
                var x = price(p);
                gp += p.Units * (x - p.Cost);
                net += p.Units * x;
            }
            return net > 0 ? 100m * gp / net : 0m;
        }
        decimal Raw(TargetProduct p, decimal m) => p.Raisable && m < 100m && MarginOf(p.Price, p.Cost) < m
            ? Math.Max(p.Price, Math.Min(p.Ceiling, p.Cost / (1m - m / 100m))) : p.Price;

        var before = Blend(p => p.Price);
        if (before >= targetPct || products.Count == 0)
            return new TargetOutcome(targetPct, Math.Round(before, 1), Math.Round(before, 1), null, new Dictionary<string, decimal>(), 0);
        decimal lo = 0m, hi = 95m;
        if (Blend(p => Raw(p, hi)) >= targetPct)
            for (var i = 0; i < 40; i++)
            {
                var mid = (lo + hi) / 2m;
                if (Blend(p => Raw(p, mid)) >= targetPct) hi = mid; else lo = mid;
            }
        var minimum = Math.Ceiling(hi * 10m) / 10m;
        var raised = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var atCeiling = 0;
        foreach (var p in products.Where(p => p.Raisable && MarginOf(p.Price, p.Cost) < minimum))
        {
            var price = priceAt(p, minimum);
            if (price <= p.Price + 0.005m) continue;
            raised[p.Sku] = price;
            if (price >= p.Ceiling - 0.005m && MarginOf(price, p.Cost) < minimum) atCeiling++;
        }
        var after = Blend(p => raised.TryGetValue(p.Sku, out var x) ? x : p.Price);
        return new TargetOutcome(targetPct, Math.Round(before, 1), Math.Round(after, 1), minimum, raised, atCeiling);
    }
}
