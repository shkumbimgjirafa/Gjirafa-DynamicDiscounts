using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PricingTool.Agent.Storage;
using PricingTool.Agent.Tools;

namespace PricingTool.Agent.Campaigns;

/// <summary>
/// Keeps <c>ai.CampaignCalendar</c> in step with the shops' own campaigns (<c>agent.CampaignHistory</c>, loaded from the
/// KS campaign history: 15 windows Oct 2024–Jun 2026, Për ty Oct 2026 and Black Friday 2026) and moves every entry's
/// status with the calendar: planned → live → done. Idempotent; entries the campaign agent proposes are left alone
/// except for their status.
/// </summary>
public sealed class CampaignCalendar
{
    public const string FromHistory = "campaign history";

    private readonly AgentDbContext _db;
    private readonly PricingDataReader _reader;
    private readonly AgentOptions _options;
    private readonly TimeProvider _clock;

    public CampaignCalendar(AgentDbContext db, PricingDataReader reader, IOptions<AgentOptions> options, TimeProvider? clock = null)
    {
        _db = db;
        _reader = reader;
        _options = options.Value;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Returns (entries added, statuses changed).</summary>
    public async Task<(int Added, int Moved)> SyncAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
        var now = _clock.GetUtcNow().UtcDateTime;
        var known = await _db.CampaignCalendar.Where(c => c.SourceCampaignId != null).Select(c => new { c.LayerId, c.SourceCampaignId }).ToListAsync(ct);
        var added = 0;
        foreach (var layer in _options.Cycle.Layers)
            foreach (var w in await _reader.GetCampaignWindowsAsync(layer, new DateOnly(2000, 1, 1), today.AddYears(2), ct))
            {
                if (known.Any(k => k.LayerId == layer && k.SourceCampaignId == w.CampaignId)) continue;
                _db.CampaignCalendar.Add(new CampaignCalendarEntry
                {
                    LayerId = layer, Name = w.Name, StartDate = w.StartDate, EndDate = w.EndDate, Type = w.CampaignType, ScopeKind = "all",
                    Status = StatusOn(w.StartDate, w.EndDate, today, CampaignStatus.Planned), SourceCampaignId = w.CampaignId,
                    CreatedBy = FromHistory, CreatedUtc = now, Context = string.IsNullOrWhiteSpace(w.Notes) ? null : $"History: {w.Notes}",
                });
                added++;
            }

        // The history's notes reach entries synced before notes were kept.
        foreach (var layer in _options.Cycle.Layers)
            foreach (var w in (await _reader.GetCampaignWindowsAsync(layer, new DateOnly(2000, 1, 1), today.AddYears(2), ct)).Where(w => !string.IsNullOrWhiteSpace(w.Notes)))
                if (await _db.CampaignCalendar.FirstOrDefaultAsync(c => c.LayerId == layer && c.SourceCampaignId == w.CampaignId && c.Context == null, ct) is { } entry)
                    entry.Context = $"History: {w.Notes}";

        var moved = 0;
        foreach (var c in await _db.CampaignCalendar.Where(c => c.Status != CampaignStatus.Done && c.Status != CampaignStatus.Proposed).ToListAsync(ct))
        {
            var status = StatusOn(c.StartDate, c.EndDate, today, c.Status);
            if (status == c.Status) continue;
            c.Status = status;
            moved++;
        }
        await _db.SaveChangesAsync(ct);
        return (added, moved);
    }

    /// <summary>
    /// A person's context for a campaign (what the numbers don't show), appended to the entry with who and when; with
    /// <paramref name="notRepresentative"/> the window is left out of the event lifts and the depth fit from now on.
    /// </summary>
    public async Task<CampaignCalendarEntry> AddContextAsync(long calendarId, string text, string by, bool notRepresentative, CancellationToken ct = default)
    {
        var entry = await _db.CampaignCalendar.SingleAsync(c => c.Id == calendarId, ct);
        var line = $"{text.Trim()} ({by}, {_clock.GetLocalNow():yyyy-MM-dd})";
        entry.Context = string.IsNullOrWhiteSpace(entry.Context) ? line : $"{entry.Context} | {line}";
        if (notRepresentative) entry.Representative = false;
        await _db.SaveChangesAsync(ct);
        return entry;
    }

    /// <summary>Done after its last day, live on its days, otherwise as it was (planned).</summary>
    public static string StatusOn(DateOnly start, DateOnly end, DateOnly today, string current) =>
        today > end ? CampaignStatus.Done : today >= start ? CampaignStatus.Live : current;
}
