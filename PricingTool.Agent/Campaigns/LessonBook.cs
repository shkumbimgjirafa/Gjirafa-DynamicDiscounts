using System.Text;
using Microsoft.EntityFrameworkCore;
using PricingTool.Agent.Storage;

namespace PricingTool.Agent.Campaigns;

/// <summary>
/// What the agents have been taught: people's lessons and corrections (ai.AgentLesson), the lessons Nova drew from finished
/// campaigns (ai.CampaignResult) and how recent plans came out against their goals (ai.CampaignPlan). Each pass's prompt
/// carries them, so a correction made once holds every week after.
/// </summary>
public sealed class LessonBook
{
    public const int MaxLessons = 20, MaxResults = 8, MaxPlans = 6;
    private readonly AgentDbContext _db;
    private readonly TimeProvider _clock;

    public LessonBook(AgentDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<AgentLesson> AddAsync(string agent, int? layerId, string text, string by, string source = LessonSource.Person,
        CancellationToken ct = default)
    {
        if (agent is not (CampaignAgent.Source or "Alpha")) throw new ArgumentException($"Agent '{agent}': Nova or Alpha.");
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("A lesson needs its text.");
        var lesson = new AgentLesson
        {
            Agent = agent, LayerId = layerId, Text = text.Trim().Length > 2000 ? text.Trim()[..2000] : text.Trim(), Source = source,
            CreatedBy = by.Length > 200 ? by[..200] : by, CreatedUtc = _clock.GetUtcNow().UtcDateTime,
        };
        _db.AgentLessons.Add(lesson);
        await _db.SaveChangesAsync(ct);
        return lesson;
    }

    public async Task<bool> RetireAsync(long id, string by, CancellationToken ct = default)
    {
        var lesson = await _db.AgentLessons.SingleOrDefaultAsync(l => l.Id == id, ct);
        if (lesson is null || lesson.RetiredUtc is not null) return false;
        (lesson.RetiredUtc, lesson.RetiredBy) = (_clock.GetUtcNow().UtcDateTime, by);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public Task<List<AgentLesson>> ActiveAsync(string agent, int layerId, CancellationToken ct = default) =>
        _db.AgentLessons.AsNoTracking()
            .Where(l => l.Agent == agent && l.RetiredUtc == null && (l.LayerId == null || l.LayerId == layerId))
            .OrderByDescending(l => l.CreatedUtc).Take(MaxLessons).ToListAsync(ct);

    /// <summary>The prompt block for a Nova pass on one layer.</summary>
    public async Task<string> NovaBlockAsync(int layerId, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        var lessons = await ActiveAsync(CampaignAgent.Source, layerId, ct);
        sb.AppendLine("Lessons from people (corrections of earlier answers; they hold until a person retires them):");
        if (lessons.Count == 0) sb.AppendLine("- none yet");
        foreach (var l in lessons)
            sb.AppendLine($"- #{l.Id} ({l.CreatedUtc:yyyy-MM-dd}, {l.CreatedBy}{(l.Source == LessonSource.Correction ? ", a correction" : "")}): {l.Text}");

        var results = await (from r in _db.CampaignResults.AsNoTracking()
                             join c in _db.CampaignCalendar.AsNoTracking() on r.CalendarId equals c.Id
                             where r.LayerId == layerId && r.Lesson != null
                             orderby r.CreatedUtc descending
                             select new { c.Name, c.StartDate, c.EndDate, r.Lesson, r.Provisional }).Take(MaxResults).ToListAsync(ct);
        sb.AppendLine("Your lessons from finished campaigns (newest first):");
        if (results.Count == 0) sb.AppendLine("- none yet");
        foreach (var r in results)
            sb.AppendLine($"- {r.Name} {r.StartDate:yyyy-MM-dd}..{r.EndDate:yyyy-MM-dd}{(r.Provisional ? " (provisional)" : "")}: {r.Lesson}");

        var plans = await (from p in _db.CampaignPlans.AsNoTracking()
                           join b in _db.CampaignBriefs.AsNoTracking() on p.BriefId equals b.Id
                           join x in _db.PublishBatches.AsNoTracking() on p.PublishBatchId equals x.Id into batches
                           from x in batches.DefaultIfEmpty()
                           where p.LayerId == layerId && !p.IsTest && (p.Status == PlanStatus.Accepted || p.Status == PlanStatus.Published)
                           orderby p.Id descending
                           select new { b.Name, b.GoalType, b.GoalValue, p.Strategy, p.ExpectedGrossProfit, p.GrossProfitWithoutDiscounts, p.ExpectedMarginPct,
                                        p.ExpectedUnits, p.Feasible, p.Status, RolledBack = x != null && x.RolledBackUtc != null })
            .Take(MaxPlans).ToListAsync(ct);
        sb.AppendLine("Recent plans against their briefs' goals (code's estimate for the SKUs the plan covers):");
        if (plans.Count == 0) sb.AppendLine("- none yet");
        foreach (var p in plans)
        {
            // Against the brief's goal as it stands now (a person may have raised it after the plan was made).
            var reaches = p.GoalType switch
            {
                "gross-profit" => p.ExpectedGrossProfit >= p.GoalValue,
                "margin" => p.ExpectedMarginPct >= p.GoalValue,
                "units" => p.ExpectedUnits >= p.GoalValue,
                _ => p.Feasible,
            };
            sb.AppendLine($"- {p.Name}: goal {p.GoalType} {p.GoalValue:N0}; plan {p.Strategy}, expected GP {p.ExpectedGrossProfit:N0} " +
                          $"(without discounts {p.GrossProfitWithoutDiscounts:N0}), margin {p.ExpectedMarginPct:0.0}%; {(reaches ? "reaches the goal" : "falls short of the goal")}; " +
                          $"{(p.RolledBack ? "published, then rolled back" : p.Status)}.");
        }
        return sb.ToString();
    }
}
