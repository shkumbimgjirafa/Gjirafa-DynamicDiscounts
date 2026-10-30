using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.ClaudeCode;
using PricingTool.Agent.Storage;
using PricingTool.Agent.Tools;

namespace PricingTool.Agent.Campaigns;

public sealed record NovaPassResult(long? PassId, string Status, string? Error, IReadOnlyList<CampaignBrief> Briefs, int ResultsWritten)
{
    public int Count(string status) => Briefs.Count(b => b.Status == status);
}

/// <summary>
/// Nova, the campaign agent's weekly pass for one layer (plan section 5, "CampaignAgent"). One Claude Code call reads
/// the calendar, the categories' trends and the campaign history (all computed by code), may investigate with the data
/// tools, and returns briefs for upcoming calendar campaigns and the mini-campaigns it proposes, plus a lesson per
/// recently finished campaign. Code then judges every brief against <see cref="CampaignLimits"/>: within them it waits
/// for the pricing agent's plan (a proposed mini-campaign also goes on the calendar as proposed); a cap a person may
/// lift holds it; anything else is rejected with the reason. Each finished campaign gets a <see cref="CampaignResult"/>
/// with the numbers code measured. Nova never sets a price. A failed call changes nothing.
/// </summary>
public sealed class CampaignAgent
{
    public const string Source = "Nova";
    private const int ReviewDays = 45;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ClaudeCodeClient _client;
    private readonly AgentDbContext _db;
    private readonly PricingDataReader _reader;
    private readonly AgentDataTools _tools;
    private readonly CampaignCalendar _calendar;
    private readonly AgentModeStore _modes;
    private readonly IKillSwitch _killSwitch;
    private readonly AgentOptions _options;
    private readonly ILogger<CampaignAgent> _logger;
    private readonly TimeProvider _clock;

    public CampaignAgent(ClaudeCodeClient client, AgentDbContext db, PricingDataReader reader, AgentDataTools tools, CampaignCalendar calendar,
        AgentModeStore modes, IKillSwitch killSwitch, IOptions<AgentOptions> options, ILogger<CampaignAgent> logger, TimeProvider? clock = null)
    {
        _client = client;
        _db = db;
        _reader = reader;
        _tools = tools;
        _calendar = calendar;
        _modes = modes;
        _killSwitch = killSwitch;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public static string PromptsDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Prompts");

    /// <summary>Test seam: the prompt's data block (trends, history, results), so a test needs no SQL.</summary>
    internal Func<int, DateOnly, CancellationToken, Task<string>>? InputSource { get; set; }

    /// <param name="isTest">Demo or injected test: stored and labelled; never puts anything on the calendar and writes no result.</param>
    public async Task<NovaPassResult> RunPassAsync(int layerId, bool isTest = false, CancellationToken ct = default)
    {
        var stop = await _killSwitch.GetAsync(ct);
        if (stop.Stopped)
            return new NovaPassResult(null, "Skipped", $"Stopped by the kill switch ({stop.Reason}): no pass.", Array.Empty<CampaignBrief>(), 0);
        if (await _modes.GetAsync(layerId, ct) == AutonomyMode.Off)
            return new NovaPassResult(null, "Skipped", $"Layer {layerId} is Off: no pass.", Array.Empty<CampaignBrief>(), 0);

        var sw = Stopwatch.StartNew();
        var now = _clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
        if (!isTest && InputSource is null) await _calendar.SyncAsync(ct);
        var pass = new AgentPass { LayerId = layerId, Source = Source, Mode = "Briefs", StartedUtc = now, Status = "Running", IsTest = isTest };
        _db.AgentPasses.Add(pass);
        await _db.SaveChangesAsync(ct);

        var calendar = await _db.CampaignCalendar.AsNoTracking().Where(c => c.LayerId == layerId).OrderBy(c => c.StartDate).ToListAsync(ct);
        var toReview = calendar.Where(c => c.Status == CampaignStatus.Done && c.EndDate >= today.AddDays(-ReviewDays)
                                           && !_db.CampaignResults.Any(r => r.CalendarId == c.Id)).ToList();
        var measured = new Dictionary<long, string>();
        foreach (var c in toReview)
            measured[c.Id] = JsonSerializer.Serialize(await _tools.GetCampaignResultsAsync(new AgentToolContext(AgentRole.Nova, $"code:nova-pass-{pass.Id}"), c.Id, ct), Json);

        var prompt = BuildPrompt(layerId, today, calendar, measured, await (InputSource ?? DataBlockAsync)(layerId, today, ct));
        var call = await _client.CallAsync(new AgentCallRequest(AgentRole.Nova,
            isTest ? $"[INJECTED-TEST] campaign-briefs L{layerId}" : $"campaign-briefs L{layerId}",
            prompt, Path.Combine(PromptsDirectory, "campaign-agent-rules.md"))
        {
            AppendSystemPromptFiles = new[] { Path.Combine(PromptsDirectory, "agent-data-dictionary.md") },
            JsonSchema = await File.ReadAllTextAsync(Path.Combine(PromptsDirectory, "campaign-brief.schema.json"), ct),
            UseDataTools = true,
            TimeoutSeconds = _options.AlphaPassTimeoutSeconds,
            MaxBudgetUsd = _options.AlphaPassMaxBudgetUsd,
        }, ct);
        pass.CallTag = call.CallTag;
        pass.AiCallLogId = call.CallLogIds.Count > 0 ? call.CallLogIds[^1] : null;

        var answer = call.Succeeded ? call.Deserialize<NovaAnswer>() : null;
        if (answer is null || answer.Layer != layerId)
        {
            pass.Status = "Failed";
            pass.Error = call.Succeeded ? $"The answer was for layer {answer?.Layer}, not {layerId}." : $"{call.Status}: {call.Error}";
            pass.FinishedUtc = _clock.GetUtcNow().UtcDateTime;
            await _db.SaveChangesAsync(CancellationToken.None);
            _logger.LogWarning("Nova pass {Pass} on layer {Layer} failed, nothing changed: {Error}", pass.Id, layerId, pass.Error);
            return new NovaPassResult(pass.Id, pass.Status, pass.Error, Array.Empty<CampaignBrief>(), 0);
        }

        var holdouts = _options.Limits.Holdouts.TryGetValue(layerId.ToString(CultureInfo.InvariantCulture), out var h) ? h : new List<string>();
        var working = calendar.ToList();
        var briefs = new List<CampaignBrief>();
        foreach (var b in answer.Briefs)
        {
            var brief = new CampaignBrief
            {
                PassId = pass.Id, LayerId = layerId, CalendarId = b.Kind == "calendar" ? b.CalendarId : null, Name = Truncate(b.Name, 200),
                Kind = b.Kind, ScopeKind = b.ScopeKind, ScopeValue = b.ScopeKind == "all" ? null : b.ScopeValue?.Trim(),
                GoalType = b.GoalType, GoalValue = b.GoalValue, MinMarginPct = b.MinMarginPct, MaxDepthPct = b.MaxDepthPct,
                Exclusions = JsonSerializer.Serialize(b.Exclusions ?? new List<string>(), Json), Rationale = Truncate(b.Rationale, 2000),
                EvidenceRefs = JsonSerializer.Serialize(new { model = b.EvidenceRefs ?? new List<string>() }, Json),
                AiCallLogId = pass.AiCallLogId, IsTest = isTest, CreatedUtc = now,
            };
            briefs.Add(brief);
            if (!DateOnly.TryParseExact(b.StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                || !DateOnly.TryParseExact(b.EndDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            {
                (brief.Status, brief.Limit, brief.LimitDetail) = (BriefStatus.Rejected, "malformed", $"Dates '{b.StartDate}'..'{b.EndDate}' aren't yyyy-MM-dd.");
                continue;
            }
            (brief.StartDate, brief.EndDate) = (start, end);
            var verdict = CampaignLimits.Evaluate(
                new BriefInput(b.Kind, b.CalendarId, brief.ScopeKind, brief.ScopeValue, start, end, b.GoalType, b.GoalValue, b.MinMarginPct, b.MaxDepthPct),
                layerId, today, working, c => holdouts.Contains(c, StringComparer.OrdinalIgnoreCase), _options.Campaigns);
            (brief.Status, brief.MaxDepthPct, brief.Limit, brief.LimitDetail) = (verdict.Status, verdict.MaxDepthPct, verdict.Limit, verdict.Detail);

            // A proposed mini-campaign goes on the calendar as proposed, so the next brief's concurrency check sees it.
            if (brief.Status == BriefStatus.Proposed && brief.Kind == "mini" && !isTest)
            {
                var entry = new CampaignCalendarEntry
                {
                    LayerId = layerId, Name = brief.Name, StartDate = start, EndDate = end, Type = "mini", ScopeKind = brief.ScopeKind,
                    ScopeValue = brief.ScopeValue, GoalType = brief.GoalType, GoalValue = brief.GoalValue, Status = CampaignStatus.Proposed,
                    CreatedBy = Source, CreatedUtc = now, Notes = Truncate(brief.Rationale, 2000),
                };
                _db.CampaignCalendar.Add(entry);
                await _db.SaveChangesAsync(ct);
                brief.CalendarId = entry.Id;
                working.Add(entry);
            }
            else if (brief.Status == BriefStatus.Proposed && brief.Kind == "calendar" && !isTest)
            {
                var entry = await _db.CampaignCalendar.SingleAsync(c => c.Id == brief.CalendarId, ct);
                entry.GoalType ??= brief.GoalType;
                entry.GoalValue ??= brief.GoalValue;
            }
        }
        _db.CampaignBriefs.AddRange(briefs);

        var results = 0;
        if (!isTest)
            foreach (var c in toReview)
            {
                _db.CampaignResults.Add(new CampaignResult
                {
                    CalendarId = c.Id, LayerId = layerId, PassId = pass.Id, Measured = measured[c.Id], CreatedUtc = now,
                    Lesson = answer.ResultsReview.FirstOrDefault(r => r.CalendarId == c.Id)?.Lesson is { } lesson ? Truncate(lesson, 2000) : null,
                    Provisional = c.EndDate > today.AddDays(-14),
                });
                results++;
            }

        pass.Applied = briefs.Count(b => b.Status == BriefStatus.Proposed);
        pass.Held = briefs.Count(b => b.Status == BriefStatus.Held);
        pass.Rejected = briefs.Count(b => b.Status == BriefStatus.Rejected);
        pass.Summary = Truncate(isTest ? "[INJECTED-TEST] " + answer.Summary : answer.Summary, 4000);
        pass.Status = "Succeeded";
        pass.FinishedUtc = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(CancellationToken.None);
        _logger.LogInformation("Nova pass {Pass} on layer {Layer}: {Briefs} briefs ({Proposed} proposed, {Held} held, {Rejected} rejected), {Results} results in {Seconds:0} s",
            pass.Id, layerId, briefs.Count, pass.Applied, pass.Held, pass.Rejected, results, sw.Elapsed.TotalSeconds);
        return new NovaPassResult(pass.Id, pass.Status, null, briefs, results);
    }

    private string BuildPrompt(int layerId, DateOnly today, IReadOnlyList<CampaignCalendarEntry> calendar, IReadOnlyDictionary<long, string> measured,
        string dataBlock)
    {
        var o = _options.Campaigns;
        var holdouts = _options.Limits.Holdouts.TryGetValue(layerId.ToString(CultureInfo.InvariantCulture), out var h) && h.Count > 0 ? string.Join(", ", h) : "none";
        var sb = new StringBuilder();
        sb.AppendLine($"Weekly campaign review: layer {layerId}, today {today:yyyy-MM-dd} ({today.DayOfWeek}). Answer with layer = {layerId}.");
        sb.AppendLine();
        sb.AppendLine("Limits code enforces on every brief (outside them it is rejected, or held for a person):");
        sb.AppendLine($"- starts on or after {today.AddDays(o.LeadDays):yyyy-MM-dd} ({o.LeadDays} days to plan, publish and veto); a calendar brief stays inside its calendar window;");
        sb.AppendLine($"- a mini-campaign runs {o.MinDays}-{o.MaxMiniDays} days, is scoped to a category or a brand, never overlaps a main campaign, and at most {o.MaxConcurrentMiniPerLayer} run at once on this layer;");
        sb.AppendLine($"- maximum discount depth {o.MaxDepthPct:0.#}% of the regular price; at most {o.MaxSkusPerCampaign} SKUs per campaign;");
        sb.AppendLine($"- holdout categories, never in a campaign: {holdouts}.");
        sb.AppendLine();
        sb.AppendLine("Calendar (id | name | type | dates | status | scope | goal):");
        foreach (var c in calendar.Where(c => c.EndDate >= today.AddDays(-ReviewDays)))
            sb.AppendLine($"{c.Id} | {c.Name} | {c.Type} | {c.StartDate:yyyy-MM-dd}..{c.EndDate:yyyy-MM-dd} | {c.Status} | {c.ScopeKind}{(c.ScopeValue is null ? "" : " " + c.ScopeValue)} | " +
                          (c.GoalType is null ? "no goal" : $"{c.GoalType} {c.GoalValue:0.##}"));
        sb.AppendLine();
        sb.AppendLine(dataBlock);
        if (measured.Count > 0)
        {
            sb.AppendLine("Finished campaigns to review (one lesson each in resultsReview; measured by code):");
            foreach (var (id, json) in measured) sb.AppendLine($"- calendarId {id}: {json}");
        }
        return sb.ToString();
    }

    /// <summary>The categories' recent trends, stock and the campaign history summary, computed by code.</summary>
    private async Task<string> DataBlockAsync(int layerId, DateOnly today, CancellationToken ct)
    {
        var sc = _options.Scorecard;
        var window = DateWindow.Ending(today, 28, sc.LagDays);
        var sales = await _reader.GetSalesAsync(layerId, window, window.SameWeeksLastYear(), sc.FloorTolerancePp, sc.AtFloorBandPp, null, null, ct);
        var run = await _reader.GetLatestRunAsync(layerId, ct);
        var stock = run is null ? new List<StockAggregate>() : await _reader.GetStockAsync(layerId, run, null, null, ct);
        var sb = new StringBuilder();
        sb.AppendLine($"Category trends, normal days {window} (ends {sc.LagDays} days ago: late cancellations) vs the same weeks a year earlier; " +
                      "EUR VAT excl., booked COGS; stock = local warehouse at cost incl. VAT from the latest engine run" +
                      (run is null ? " (no run yet)" : $" #{run.RunId}") + ":");
        sb.AppendLine("category | units | vs LY | GP | vs LY | margin % | stock | dead stock %");
        foreach (var g in sales.Where(s => !s.CampaignDay).GroupBy(s => s.Category).OrderByDescending(g => g.Where(s => s.Period == 0).Sum(s => s.GrossProfit)))
        {
            decimal u0 = g.Where(s => s.Period == 0).Sum(s => s.Units), u1 = g.Where(s => s.Period == 1).Sum(s => s.Units);
            decimal p0 = g.Where(s => s.Period == 0).Sum(s => s.GrossProfit), p1 = g.Where(s => s.Period == 1).Sum(s => s.GrossProfit);
            decimal n0 = g.Where(s => s.Period == 0).Sum(s => s.NetRevenue);
            var st = stock.Where(s => s.Category == g.Key).ToList();
            decimal sv = st.Sum(s => s.StockValue), dv = st.Sum(s => s.DeadStockValue);
            sb.AppendLine($"{g.Key} | {u0:0} | {Change(u0, u1)} | {p0:0} | {Change(p0, p1)} | {(n0 > 0 ? 100m * p0 / n0 : 0):0.0} | {sv:0} | {(sv > 0 ? 100m * dv / sv : 0):0}");
        }
        sb.AppendLine();
        var depth = await _tools.GetDepthEffectAsync(layerId, ct);
        sb.AppendLine("Campaign history (code): event lift by campaign type, units vs normal days at normal depth: " +
                      (depth.EventLiftByType.Count == 0 ? "not measured" : string.Join(", ", depth.EventLiftByType.Select(e => $"{e.Key} x{e.Value:0.0}"))) + ".");
        sb.AppendLine(depth.B is { } b
            ? $"What extra depth buys on top of the event (within-campaign fit, {depth.Campaigns} campaigns): 10 pt deeper -> x{depth.DepthMultiplier(10):0.00} units, 20 pt -> x{depth.DepthMultiplier(20):0.00} (b = {b}); estimated."
            : $"What extra depth buys: {depth.Basis}.");
        return sb.ToString();

        static string Change(decimal now, decimal ly) => ly > 0 ? $"{100m * (now - ly) / ly:+0;-0}%" : "n/a";
    }

    private static string Truncate(string? s, int max) => s is null ? "" : s.Length <= max ? s : s[..max];

    private sealed record NovaAnswer(int Layer, List<NovaBriefDto> Briefs, List<NovaResultDto> ResultsReview, string Summary);

    private sealed record NovaBriefDto(string Kind, long? CalendarId, string Name, string ScopeKind, string? ScopeValue, string StartDate, string EndDate,
        string GoalType, decimal GoalValue, decimal MinMarginPct, decimal MaxDepthPct, List<string>? Exclusions, string Rationale, List<string>? EvidenceRefs);

    private sealed record NovaResultDto(long CalendarId, string Lesson);
}
