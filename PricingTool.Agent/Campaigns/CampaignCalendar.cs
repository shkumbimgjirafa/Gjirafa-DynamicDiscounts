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
                    CreatedBy = FromHistory, CreatedUtc = now,
                });
                added++;
            }

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

    /// <summary>Done after its last day, live on its days, otherwise as it was (planned).</summary>
    public static string StatusOn(DateOnly start, DateOnly end, DateOnly today, string current) =>
        today > end ? CampaignStatus.Done : today >= start ? CampaignStatus.Live : current;
}
