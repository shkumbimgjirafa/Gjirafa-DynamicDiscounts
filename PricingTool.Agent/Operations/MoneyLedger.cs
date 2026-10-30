using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PricingTool.Agent.Storage;
using PricingTool.Agent.Tools;
using PricingTool.Data;

namespace PricingTool.Agent.Operations;

public sealed record LedgerSummary(decimal Measured30Eur, decimal Measured7Eur, int Entries, int Measured30, decimal? MonthlyRaiseEur)
{
    /// <summary>Condition 4 of the bar: at least three times the raise's monthly cost in the 30 days.</summary>
    public decimal? TargetEur => MonthlyRaiseEur * 3;
}

/// <summary>
/// The money ledger (condition 4 of the 10 % bar). Every pushed export (an Approval recorded through
/// <see cref="Checker.CheckedExport.MarkPushedAsync"/>) gets an entry; after 7 and 30 days its gross profit is
/// measured from the sales lines (booked COGS) as a difference-in-differences against the holdout categories:
/// <c>measured = GP_after(pushed SKUs) − GP_before(pushed SKUs) × GP_after(holdout) / GP_before(holdout)</c>,
/// over the same number of days before and after the push. The holdout's ratio carries the season.
/// Conservative by rule: a window not yet complete, or a holdout too thin to compare against, stays null and
/// counts as €0; the expected figure is shown for reference and never added to a total.
/// </summary>
public sealed class MoneyLedger
{
    /// <summary>Below this many holdout sales lines in either window the comparison is not attributable.</summary>
    public const int MinHoldoutLines = 30;

    /// <summary>A window is re-measured for this many days after it closes: late cancellations still post.</summary>
    public const int RemeasureDays = 14;

    public const string RaiseSettingKey = "Bar.MonthlyRaiseEur";

    private readonly AgentDbContext _db;
    private readonly PricingToolDbContext _tool;
    private readonly AgentDataSource _source;
    private readonly AgentOptions _options;
    private readonly TimeProvider _clock;

    public MoneyLedger(AgentDbContext db, PricingToolDbContext tool, AgentDataSource source, IOptions<AgentOptions> options, TimeProvider? clock = null)
    {
        _db = db;
        _tool = tool;
        _source = source;
        _options = options.Value;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Adds entries for newly pushed exports and (re)measures every window that is due. Returns
    /// (entries added, windows measured).</summary>
    public async Task<(int Added, int Measured)> UpdateAsync(CancellationToken ct = default)
    {
        var known = await _db.MoneyLedger.Where(e => e.Source == "AppliedSet").Select(e => e.RefId).ToListAsync(ct);
        var pushed = await _db.ExportBatches.Where(b => b.ApprovalId != null && !known.Contains(b.ApprovalId!.Value)).ToListAsync(ct);
        foreach (var b in pushed)
            _db.MoneyLedger.Add(new MoneyLedgerEntry
            {
                Source = "AppliedSet", RefId = b.ApprovalId!.Value, LayerId = b.LayerId, AppliedUtc = b.PushedUtc!.Value, Skus = b.Rows,
                ExpectedEur = b.ExpectedGp30Eur,
                ExpectedBasis = "estimated at export: each price change on the SKU's last 30 days of units, VAT excluded, no elasticity",
            });
        await _db.SaveChangesAsync(ct);

        var now = _clock.GetUtcNow().UtcDateTime;
        var measured = 0;
        foreach (var e in await _db.MoneyLedger.Where(e => e.Source == "AppliedSet").ToListAsync(ct))
        {
            foreach (var days in new[] { 7, 30 })
            {
                var closes = e.AppliedUtc.AddDays(days);
                if (now < closes || now > closes.AddDays(RemeasureDays)) continue;
                var (value, evidence) = await MeasureAsync(e, days, ct);
                if (days == 7) e.Measured7Eur = value; else e.Measured30Eur = value;
                e.Evidence = evidence;
                e.MeasuredUtc = now;
                measured++;
            }
        }
        await _db.SaveChangesAsync(ct);
        return (pushed.Count, measured);
    }

    private async Task<(decimal? Value, string Evidence)> MeasureAsync(MoneyLedgerEntry e, int days, CancellationToken ct)
    {
        var holdouts = _options.Limits.Holdouts.TryGetValue(e.LayerId.ToString(CultureInfo.InvariantCulture), out var h) ? h : new List<string>();
        if (holdouts.Count == 0) return (null, $"{days}-day window: no holdout categories on layer {e.LayerId}, so not attributable (counts €0).");

        await using var conn = new SqlConnection(_source.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            DECLARE @Mall bit = (SELECT CASE WHEN Brand = 'GjirafaMall' THEN 1 ELSE 0 END FROM PricingTool.Layers WHERE Id = @L);
            DECLARE @From datetime = DATEADD(day, -@N, @D), @To datetime = DATEADD(day, @N, @D);
            SELECT DISTINCT Sku INTO #t FROM PricingTool.ApprovalItems WHERE ApprovalId = @A;
            SELECT DISTINCT p.Sku INTO #h FROM hk.Products p
            WHERE p.LayerId = @L AND (CASE WHEN @Mall = 1 THEN ISNULL(p.MainGroup, N'Others') ELSE ISNULL(p.CategoryL1, N'Unknown') END)
                  IN (SELECT value FROM STRING_SPLIT(@Cats, '|'))
              AND p.Sku NOT IN (SELECT Sku FROM #t);
            SELECT
                ISNULL(SUM(CASE WHEN t.Sku IS NOT NULL AND s.DateAdded <  @D THEN s.Margin END), 0),
                ISNULL(SUM(CASE WHEN t.Sku IS NOT NULL AND s.DateAdded >= @D THEN s.Margin END), 0),
                ISNULL(SUM(CASE WHEN h.Sku IS NOT NULL AND s.DateAdded <  @D THEN s.Margin END), 0),
                ISNULL(SUM(CASE WHEN h.Sku IS NOT NULL AND s.DateAdded >= @D THEN s.Margin END), 0),
                SUM(CASE WHEN h.Sku IS NOT NULL AND s.DateAdded <  @D THEN 1 ELSE 0 END),
                SUM(CASE WHEN h.Sku IS NOT NULL AND s.DateAdded >= @D THEN 1 ELSE 0 END)
            FROM hk.SalesLines s
            LEFT JOIN #t t ON t.Sku = s.Sku
            LEFT JOIN #h h ON h.Sku = s.Sku
            WHERE s.LayerId = @L AND s.DateAdded >= @From AND s.DateAdded < @To;
            """, conn) { CommandTimeout = 300 };
        cmd.Parameters.AddWithValue("@L", e.LayerId);
        cmd.Parameters.AddWithValue("@A", e.RefId);
        cmd.Parameters.AddWithValue("@N", days);
        cmd.Parameters.Add("@D", System.Data.SqlDbType.DateTime).Value = e.AppliedUtc;
        cmd.Parameters.AddWithValue("@Cats", string.Join("|", holdouts));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        decimal tb = r.GetDecimal(0), ta = r.GetDecimal(1), hb = r.GetDecimal(2), ha = r.GetDecimal(3);
        int hbLines = r.GetInt32(4), haLines = r.GetInt32(5);

        var basis = $"{days} days before vs after {e.AppliedUtc:yyyy-MM-dd}; pushed SKUs GP {tb:0} -> {ta:0}; holdout ({string.Join(", ", holdouts)}) GP {hb:0} -> {ha:0} " +
                    $"({hbLines} / {haLines} lines); booked COGS from hk.SalesLines";
        if (hbLines < MinHoldoutLines || haLines < MinHoldoutLines || hb <= 0)
            return (null, basis + $". Holdout too thin to compare (< {MinHoldoutLines} lines or no profit before): not attributable, counts €0.");
        var value = Math.Round(ta - tb * (ha / hb), 2);
        return (value, basis + $". Measured = {ta:0} - {tb:0} x {ha:0}/{hb:0} = {value:0} EUR (difference-in-differences against the holdout).");
    }

    public async Task<LedgerSummary> SummaryAsync(CancellationToken ct = default)
    {
        var entries = await _db.MoneyLedger.AsNoTracking().ToListAsync(ct);
        var raise = await _tool.ToolSettings.AsNoTracking().Where(s => s.Key == RaiseSettingKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return new LedgerSummary(
            entries.Sum(e => e.Measured30Eur ?? 0), entries.Sum(e => e.Measured7Eur ?? 0), entries.Count, entries.Count(e => e.Measured30Eur is not null),
            decimal.TryParse(raise, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null);
    }
}
