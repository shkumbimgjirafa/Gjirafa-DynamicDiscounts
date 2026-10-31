using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PricingTool.Agent.Storage;
using PricingTool.Agent.Tools;

namespace PricingTool.Agent.Operations;

/// <summary>Where a goal stands: what the period has made so far (booked gross profit, VAT excluded, and its margin), how much of the
/// period the sales lines cover, and, for gross profit, the period at the same pace.</summary>
public sealed record GoalStatus(GoalRecord Goal, decimal GrossProfit, decimal? MarginPct, decimal ElapsedShare, decimal? Projected, DateTime? SalesThrough)
{
    /// <summary>Gross profit: the share of the goal made so far; margin: the margin so far against the goal.</summary>
    public decimal? ShareOfGoal => Goal.Kind == GoalKinds.GrossProfit && Goal.Value > 0 ? GrossProfit / Goal.Value : null;

    /// <summary>On track: gross profit at the same pace reaches the goal; margin so far at or above it.</summary>
    public bool? OnTrack => Goal.Kind == GoalKinds.GrossProfit
        ? Projected is { } p ? p >= Goal.Value : null
        : MarginPct is { } m ? m >= Goal.Value : null;
}

/// <summary>The goals the pricing manager sets in the chat: checked by code, one per layer, period and kind, and measured on the
/// booked sales lines.</summary>
public static class Goals
{
    /// <summary>The period a goal covers: a calendar month (from its first day) or a Monday-to-Sunday week, around <paramref name="day"/>.</summary>
    public static (DateOnly Start, DateOnly End) PeriodOf(string period, DateOnly day) => period switch
    {
        GoalKinds.Month => (new DateOnly(day.Year, day.Month, 1), new DateOnly(day.Year, day.Month, 1).AddMonths(1).AddDays(-1)),
        GoalKinds.Week => (day.AddDays(-(((int)day.DayOfWeek + 6) % 7)), day.AddDays(-(((int)day.DayOfWeek + 6) % 7) + 6)),
        _ => throw new ArgumentException($"'{period}' isn't a period: month or week."),
    };

    /// <summary>Why a goal can't be recorded, or null: a layer the agents work on, a month or week that hasn't ended, gross profit over
    /// zero, a margin between 0 and 100.</summary>
    public static string? Problem(int layer, string period, string kind, decimal value, DateOnly start, DateOnly today)
    {
        if (layer is not (1 or 4)) return $"Layer {layer} isn't one the agents work on.";
        if (period is not (GoalKinds.Month or GoalKinds.Week)) return $"'{period}' isn't a period: month or week.";
        if (kind == GoalKinds.GrossProfit && (value <= 0 || value > 50_000_000m)) return $"{value:N0} isn't a gross profit in euros.";
        if (kind == GoalKinds.Margin && (value <= 0 || value >= 100)) return $"{value:0.##} isn't a margin in percent.";
        if (kind is not (GoalKinds.GrossProfit or GoalKinds.Margin)) return $"'{kind}' isn't a kind of goal: gross-profit or margin.";
        var (_, end) = PeriodOf(period, start);
        return end < today ? $"That {period} ended on {end:yyyy-MM-dd}." : null;
    }

    /// <summary>The goals in force on <paramref name="today"/>, newest per layer, period and kind.</summary>
    public static async Task<List<GoalRecord>> ActiveAsync(AgentDbContext db, DateOnly today, int? layerId = null, CancellationToken ct = default) =>
        (await db.Goals.AsNoTracking().Where(g => g.RetiredUtc == null && g.Start <= today && g.End >= today && (layerId == null || g.LayerId == layerId))
            .OrderByDescending(g => g.Id).ToListAsync(ct))
        .GroupBy(g => (g.LayerId, g.Period, g.Kind)).Select(g => g.First())
        .OrderBy(g => g.LayerId).ThenBy(g => g.Period).ThenBy(g => g.Kind).ToList();

    /// <summary>A goal measured: <paramref name="grossProfit"/> and <paramref name="netRevenue"/> are the period's booked sales lines up to
    /// <paramref name="through"/> (the newest line loaded).</summary>
    public static GoalStatus Evaluate(GoalRecord goal, decimal grossProfit, decimal netRevenue, DateTime? through)
    {
        var days = goal.End.DayNumber - goal.Start.DayNumber + 1;
        var covered = through is { } t ? (decimal)(t - goal.Start.ToDateTime(TimeOnly.MinValue)).TotalDays : 0m;
        var share = Math.Clamp(covered / days, 0m, 1m);
        return new GoalStatus(goal, grossProfit, netRevenue > 0 ? Math.Round(100m * grossProfit / netRevenue, 1) : null, share,
            goal.Kind == GoalKinds.GrossProfit && share > 0.05m ? Math.Round(grossProfit / share, 0) : null, through);
    }

    /// <summary>Each goal in force measured on the booked sales lines (hk.SalesLines, refreshed daily from the replica).</summary>
    public static async Task<IReadOnlyList<GoalStatus>> StatusAsync(AgentDbContext db, AgentDataSource source, DateOnly today, int? layerId = null, CancellationToken ct = default)
    {
        var goals = await ActiveAsync(db, today, layerId, ct);
        if (goals.Count == 0) return Array.Empty<GoalStatus>();
        await using var conn = new SqlConnection(source.ConnectionString);
        await conn.OpenAsync(ct);
        var result = new List<GoalStatus>();
        foreach (var g in goals)
        {
            await using var cmd = new SqlCommand("""
                SELECT ISNULL(SUM(Margin), 0), ISNULL(SUM(NetoPrice), 0), MAX(DateAdded)
                FROM hk.SalesLines WHERE LayerId = @L AND DateAdded >= @From AND DateAdded < @To
                """, conn) { CommandTimeout = 60 };
            cmd.Parameters.AddWithValue("@L", g.LayerId);
            cmd.Parameters.AddWithValue("@From", g.Start.ToDateTime(TimeOnly.MinValue));
            cmd.Parameters.AddWithValue("@To", g.End.AddDays(1).ToDateTime(TimeOnly.MinValue));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            result.Add(Evaluate(g, r.GetDecimal(0), r.GetDecimal(1), r.IsDBNull(2) ? null : r.GetDateTime(2)));
        }
        return result;
    }

    /// <summary>The goals in force, as the agents read them with their weekly inputs and in the chat.</summary>
    public static async Task<string> BlockAsync(AgentDbContext db, DateOnly today, int? layerId = null, CancellationToken ct = default)
    {
        var goals = await ActiveAsync(db, today, layerId, ct);
        var sb = new StringBuilder("Goals the pricing manager set (booked gross profit, VAT excluded; margin = gross profit / net revenue): ");
        if (goals.Count == 0) return sb.Append("none.").AppendLine().ToString();
        sb.AppendLine();
        foreach (var g in goals) sb.AppendLine("- " + Describe(g));
        return sb.ToString();
    }

    public static string Describe(GoalRecord g) =>
        $"layer {g.LayerId}, {(g.Period == GoalKinds.Month ? g.Start.ToString("MMMM yyyy", CultureInfo.InvariantCulture) : $"week {g.Start:yyyy-MM-dd}..{g.End:yyyy-MM-dd}")}: " +
        (g.Kind == GoalKinds.GrossProfit ? $"gross profit €{g.Value:N0}" : $"margin {g.Value:0.#}%") + $" (set by {g.SetBy}, goal #{g.Id})";
}
