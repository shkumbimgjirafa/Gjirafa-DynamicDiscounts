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
using PricingTool.Core.Domain;
using PricingTool.Data;
using PricingTool.Data.Services;

namespace PricingTool.Agent.Campaigns;

/// <summary>What publishing a campaign does (the NOP writer in the app, a fake in tests).</summary>
public interface ICampaignPublisher
{
    /// <summary>Time-boxed ai-campaign discounts for the plan's lines: (batch, status).</summary>
    Task<(long BatchId, string Status)> PublishAsync(int layerId, long campaignKey, DateTime startUtc, DateTime endUtc,
        IReadOnlyList<(string Sku, decimal Price, decimal Shelf)> lines, CancellationToken ct);
}

public sealed record PlanningResult(long BriefId, string Status, IReadOnlyList<CampaignPlan> Plans, string? Error)
{
    public CampaignPlan? Final => Plans.Count == 0 ? null : Plans[^1];
}

/// <summary>
/// Brief → plan → accept or adjust → publish (plan section 5, campaign agent step 2 and "PlanCampaign"). Code computes the
/// three strategies (<see cref="CampaignPlanner"/>) on the layer's latest engine run and booked sales; the pricing agent
/// (Alpha) chooses one and explains it; the campaign agent (Nova) accepts the plan or adjusts its brief (goal, depth,
/// margin), at most two rounds, after which a still-unhappy Nova leaves the plan held for a person. Every message is
/// stored as the brief's thread. An accepted plan is published as time-boxed ai-campaign discounts (the sandbox during the
/// hackathon); they start and end with the campaign by themselves. Code enforces the campaign limits on every round.
/// </summary>
public sealed class CampaignPlanning
{
    public const int MaxRounds = 2;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly AgentDbContext _db;
    private readonly PricingToolDbContext _tool;
    private readonly ICampaignCandidateSource _candidates;
    private readonly AgentDataTools _tools;
    private readonly BandConfigProvider _bands;
    private readonly ClaudeCodeClient _client;
    private readonly ICampaignPublisher? _publisher;
    private readonly IKillSwitch _killSwitch;
    private readonly AgentOptions _options;
    private readonly ILogger<CampaignPlanning> _logger;
    private readonly TimeProvider _clock;

    public CampaignPlanning(AgentDbContext db, PricingToolDbContext tool, ICampaignCandidateSource candidates, AgentDataTools tools,
        BandConfigProvider bands, ClaudeCodeClient client, IKillSwitch killSwitch, IOptions<AgentOptions> options, ILogger<CampaignPlanning> logger,
        TimeProvider? clock = null, ICampaignPublisher? publisher = null)
    {
        _db = db;
        _tool = tool;
        _candidates = candidates;
        _tools = tools;
        _bands = bands;
        _client = client;
        _publisher = publisher;
        _killSwitch = killSwitch;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public static string PromptsDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Prompts");

    /// <summary>Test seam: the depth effect (a fit over the campaign history needs SQL).</summary>
    internal Func<int, CancellationToken, Task<DepthEffect>>? DepthSource { get; set; }

    public async Task<PlanningResult> PlanAsync(long briefId, bool publish, CancellationToken ct = default)
    {
        var stop = await _killSwitch.GetAsync(ct);
        if (stop.Stopped) return new PlanningResult(briefId, "Skipped", Array.Empty<CampaignPlan>(), $"Kill switch: {stop.Reason}");
        var brief = await _db.CampaignBriefs.SingleAsync(b => b.Id == briefId, ct);
        if (brief.Status is not (BriefStatus.Proposed or BriefStatus.Planned))
            return new PlanningResult(briefId, "Refused", Array.Empty<CampaignPlan>(), $"Brief #{briefId} is {brief.Status}: only a proposed brief is planned.");
        var layer = await _tool.Layers.AsNoTracking().SingleAsync(l => l.Id == brief.LayerId, ct);
        var calendar = brief.CalendarId is { } cid ? await _db.CampaignCalendar.AsNoTracking().SingleOrDefaultAsync(c => c.Id == cid, ct) : null;
        var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
        var depth = await (DepthSource ?? ((l, c) => _tools.GetDepthEffectAsync(l, c)))(layer.Id, ct);
        var type = CampaignMath.TypeOf(calendar?.Name ?? brief.Name);
        var eventLift = depth.EventLift(type);
        var liftNote = eventLift is { } el ? $"event lift x{el:0.00} (median of past {type} windows)" : $"no past {type} window: no event lift assumed (x1.00, conservative)";
        var bands = (await _bands.GetBandsAsync(layer.Id, ct)).ToDictionary(b => b.BandId);
        var (candidates, leftOutBefore) = await CandidatesAsync(brief, layer.Id, layer.Brand == "GjirafaMall", today, ct);

        var plans = new List<CampaignPlan>();
        for (var round = 1; round <= MaxRounds; round++)
        {
            var spec = new PlanSpec(brief.CalendarId ?? brief.Id, brief.EndDate.DayNumber - brief.StartDate.DayNumber + 1, brief.GoalType, brief.GoalValue,
                brief.MinMarginPct, Math.Min(brief.MaxDepthPct, _options.Campaigns.MaxDepthPct), layer.VatRatePct, eventLift ?? 1m,
                ExcludedBrands(brief.Exclusions));
            var results = CampaignPlanner.Strategies.Select(s => CampaignPlanner.Plan(s, spec, candidates, depth, bands, _options.Campaigns.MaxSkusPerCampaign, leftOutBefore)).ToList();
            var basis = $"{candidates.Count:N0} SKUs in scope with stock and normal-day sales ({today.AddDays(-42):yyyy-MM-dd}..{today.AddDays(-15):yyyy-MM-dd}, campaign days left out); " +
                        $"{liftNote}; extra depth: {(depth.B is { } b ? $"b = {b} within-campaign fit" : "no fit, no extra units from depth")}; estimated.";
            await SayAsync(brief, null, round, "code", $"Round {round}: three plans computed. {basis}\n{Describe(results, brief)}", null, ct);

            var choice = await AskAlphaAsync(brief, results, basis, round, ct);
            if (choice is null)
                return await FailAsync(brief, plans, "The pricing agent's answer was missing or invalid: nothing planned.", ct);
            var chosen = results.Single(r => r.Strategy == choice.Value.Strategy);
            var plan = await StoreAsync(brief, round, chosen, results, choice.Value.Explanation, basis, ct);
            plans.Add(plan);
            await SayAsync(brief, plan.Id, round, FloorTargetAgent.Source, choice.Value.Explanation, choice.Value.CallLogId, ct);

            var review = await AskNovaAsync(brief, plan, round, ct);
            if (review is null)
                return await FailAsync(brief, plans, "The campaign agent's review was missing or invalid: the plan waits for a person.", ct);
            await SayAsync(brief, plan.Id, round, CampaignAgent.Source, review.Value.Message, review.Value.CallLogId, ct);
            if (review.Value.Accept)
            {
                plan.Status = PlanStatus.Accepted;
                break;
            }
            if (round == MaxRounds)
            {
                plan.Status = PlanStatus.Held;
                await SayAsync(brief, plan.Id, round, "code", $"Still not accepted after {MaxRounds} rounds: the plan waits for a person.", null, ct);
                break;
            }
            plan.Status = PlanStatus.Superseded;
            // Nova adjusts its own brief, within the campaign limits code enforces.
            // Nova adjusts its own brief's numbers (dates and scope stay as code accepted them); the depth cap still holds.
            var adjusted = Adjusted(brief, review.Value);
            if (adjusted.MinMargin >= 100 || brief.GoalType == "margin" && adjusted.Goal >= 100)
            {
                plan.Status = PlanStatus.Held;
                await SayAsync(brief, plan.Id, round, "code", "The adjustment is out of range: the plan waits for a person.", null, ct);
                break;
            }
            var depthCap = _options.Campaigns.MaxDepthPct;
            if (adjusted.MaxDepth > depthCap)
                await SayAsync(brief, plan.Id, round, "code", $"Max depth {adjusted.MaxDepth:0.#}% capped at {depthCap:0.#}%.", null, ct);
            (brief.GoalValue, brief.MinMarginPct, brief.MaxDepthPct) = (adjusted.Goal, adjusted.MinMargin, Math.Min(adjusted.MaxDepth, depthCap));
            await _db.SaveChangesAsync(ct);
        }

        var final = plans[^1];
        brief.Status = BriefStatus.Planned;
        if (final.Status == PlanStatus.Accepted && publish && !brief.IsTest)
        {
            if (_publisher is null)
                await SayAsync(brief, final.Id, final.Round, "code", "Accepted; no publisher configured here, so nothing was published.", null, ct);
            else
            {
                var lines = await _db.CampaignPlanLines.AsNoTracking().Where(l => l.PlanId == final.Id).ToListAsync(ct);
                var (start, end) = Window(brief, layer.Id);
                var (batchId, status) = await _publisher.PublishAsync(layer.Id, brief.CalendarId ?? brief.Id, start, end,
                    lines.Select(l => (l.Sku, l.CampaignPrice, l.RegularPrice)).ToList(), ct);
                final.PublishBatchId = batchId;
                if (status == "Published") final.Status = PlanStatus.Published;
                await SayAsync(brief, final.Id, final.Round, "code",
                    $"Publish batch #{batchId}: {status}. {lines.Count:N0} time-boxed ai-campaign discounts, live {start:yyyy-MM-dd HH:mm}..{end:yyyy-MM-dd HH:mm} UTC.", null, ct);
            }
        }
        await _db.SaveChangesAsync(CancellationToken.None);
        _logger.LogInformation("Campaign brief {Brief}: {Rounds} round(s), plan #{Plan} {Status}", brief.Id, plans.Count, final.Id, final.Status);
        return new PlanningResult(brief.Id, final.Status, plans, null);
    }

    /// <summary>The campaign's local days as a UTC window (whole days, inclusive of the last).</summary>
    private static (DateTime Start, DateTime End) Window(CampaignBrief b, int layerId)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Central European Standard Time" : "Europe/Belgrade");
        return (TimeZoneInfo.ConvertTimeToUtc(b.StartDate.ToDateTime(TimeOnly.MinValue), tz),
                TimeZoneInfo.ConvertTimeToUtc(b.EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue), tz).AddSeconds(-1));
    }

    private async Task<(List<CampaignCandidate>, Dictionary<string, int>)> CandidatesAsync(CampaignBrief brief, int layerId, bool isMall, DateOnly today,
        CancellationToken ct)
    {
        var all = await _candidates.LoadAsync(layerId, today.AddDays(-42), today.AddDays(-14), ct);
        var holdouts = _options.Limits.Holdouts.TryGetValue(layerId.ToString(CultureInfo.InvariantCulture), out var h) ? h : new List<string>();
        var leftOut = new Dictionary<string, int>();
        void Out(string why) => leftOut[why] = leftOut.GetValueOrDefault(why) + 1;
        var keep = new List<CampaignCandidate>();
        foreach (var c in all)
        {
            if (brief.ScopeKind == "category" && !string.Equals(c.Category, brief.ScopeValue, StringComparison.OrdinalIgnoreCase)) continue;
            if (brief.ScopeKind == "brand" && !string.Equals(c.Brand, brief.ScopeValue, StringComparison.OrdinalIgnoreCase)) continue;
            if (c.DeadStock) { Out("dead stock or clearance lock (its own lane)"); continue; }
            if (isMall && c.FollowsGj50) { Out("GjirafaMall price follows Gjirafa50 (planned on Gjirafa50)"); continue; }
            if (holdouts.Contains(c.Category, StringComparer.OrdinalIgnoreCase)) { Out("holdout category"); continue; }
            if (c.LocalStock <= 0 && c.SupplierStock <= 0) { Out("no stock"); continue; }
            if (c.UnitsPerDay <= 0) { Out("no normal-day sales to plan on"); continue; }
            keep.Add(c);
        }
        return (keep, leftOut);
    }

    private static HashSet<string> ExcludedBrands(string exclusionsJson)
    {
        try { return JsonSerializer.Deserialize<List<string>>(exclusionsJson)?.Select(e => e.Trim()).Where(e => e.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new(); }
        catch (JsonException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private static string Describe(IEnumerable<PlanResult> results, CampaignBrief brief)
    {
        var sb = new StringBuilder();
        foreach (var r in results)
        {
            var t = r.Totals;
            sb.AppendLine($"- {r.Strategy}: {t.Discounted:N0} of {t.InScope:N0} SKUs discounted (average depth {t.AverageDepthPct:0.#}%), expected {t.Units:N0} units, " +
                          $"net revenue {t.NetRevenue:N0}, gross profit {t.GrossProfit:N0} (without any discount {t.GrossProfitWithoutDiscounts:N0}), margin {t.MarginPct:0.0}%. " +
                          $"{r.Feasibility}");
            if (r.TopContributors.Count > 0)
                sb.AppendLine("  top: " + string.Join("; ", r.TopContributors.Take(5).Select(l => $"{l.Sku} ({l.Category}) {l.RegularPrice:0.00} -> {l.CampaignPrice:0.00}, GP {l.ExpectedGrossProfit:0}")));
            if (r.LeftOut.Count > 0)
                sb.AppendLine("  left out: " + string.Join("; ", r.LeftOut.OrderByDescending(x => x.Value).Select(x => $"{x.Value:N0} {x.Key}")));
        }
        return sb.ToString();
    }

    private async Task<(string Strategy, string Explanation, long? CallLogId)?> AskAlphaAsync(CampaignBrief brief, IReadOnlyList<PlanResult> results, string basis, int round,
        CancellationToken ct)
    {
        var prompt = $"""
            Campaign brief #{brief.Id} from the campaign agent (round {round}): "{brief.Name}", layer {brief.LayerId}, {brief.StartDate:yyyy-MM-dd}..{brief.EndDate:yyyy-MM-dd},
            scope {brief.ScopeKind} {brief.ScopeValue}, goal {brief.GoalType} {brief.GoalValue:N0}, minimum margin {brief.MinMarginPct:0.#}%, maximum depth {brief.MaxDepthPct:0.#}%.
            Its rationale: {brief.Rationale}

            Code computed three plans ({basis}):
            {Describe(results, brief)}
            Choose one strategy and explain the choice to the campaign agent in three or four sentences: which goal it serves, whether the
            goal is reachable and if not what is, and what the plan gives up. Quote the figures above; don't compute new ones.
            """;
        var call = await _client.CallAsync(new AgentCallRequest(AgentRole.Alpha, $"{(brief.IsTest ? "[INJECTED-TEST] " : "")}campaign-plan brief {brief.Id} r{round}",
            prompt, Path.Combine(PromptsDirectory, "campaign-planner-rules.md"))
        {
            JsonSchema = await File.ReadAllTextAsync(Path.Combine(PromptsDirectory, "campaign-plan-choice.schema.json"), ct),
            MaxBudgetUsd = _options.MaxBudgetUsdPerCall,
        }, ct);
        var a = call.Succeeded ? call.Deserialize<ChoiceDto>() : null;
        return a is null || !CampaignPlanner.Strategies.Contains(a.Strategy) ? null
            : (a.Strategy, a.Explanation, call.CallLogIds.Count > 0 ? call.CallLogIds[^1] : null);
    }

    private async Task<(bool Accept, decimal? Goal, decimal? MaxDepth, decimal? MinMargin, string Message, long? CallLogId)?> AskNovaAsync(CampaignBrief brief,
        CampaignPlan plan, int round, CancellationToken ct)
    {
        var prompt = $"""
            Review of the pricing agent's plan for your brief #{brief.Id} "{brief.Name}" (round {round} of at most {MaxRounds}).
            Your brief: {brief.ScopeKind} {brief.ScopeValue}, {brief.StartDate:yyyy-MM-dd}..{brief.EndDate:yyyy-MM-dd}, goal {brief.GoalType} {brief.GoalValue:N0},
            minimum margin {brief.MinMarginPct:0.#}%, maximum depth {brief.MaxDepthPct:0.#}%.
            The plan ({plan.Strategy}): {plan.DiscountedSkus:N0} of {plan.SkusInScope:N0} SKUs discounted at {plan.AverageDepthPct:0.#}% on average,
            expected units {plan.ExpectedUnits:N0}, net revenue {plan.ExpectedNetRevenue:N0}, gross profit {plan.ExpectedGrossProfit:N0}
            (without any discount {plan.GrossProfitWithoutDiscounts:N0}), margin {plan.ExpectedMarginPct:0.0}%. {plan.Feasibility}
            {plan.HoldoutSkus:N0} SKUs are the campaign holdout. Basis: {plan.Basis}
            The pricing agent says: {plan.Explanation}

            Accept the plan, or adjust your brief (a new goal value, maximum depth or minimum margin; null keeps a value) and say why.
            {(round == MaxRounds ? "This is the last round: if you don't accept, a person decides." : "")}
            """;
        var call = await _client.CallAsync(new AgentCallRequest(AgentRole.Nova, $"{(brief.IsTest ? "[INJECTED-TEST] " : "")}campaign-review brief {brief.Id} r{round}",
            prompt, Path.Combine(PromptsDirectory, "campaign-agent-rules.md"))
        {
            JsonSchema = await File.ReadAllTextAsync(Path.Combine(PromptsDirectory, "campaign-plan-review.schema.json"), ct),
            MaxBudgetUsd = _options.MaxBudgetUsdPerCall,
        }, ct);
        var r = call.Succeeded ? call.Deserialize<ReviewDto>() : null;
        return r is null || r.Decision is not ("accept" or "adjust") ? null
            : (r.Decision == "accept", r.GoalValue, r.MaxDepthPct, r.MinMarginPct, r.Message, call.CallLogIds.Count > 0 ? call.CallLogIds[^1] : null);
    }

    private static (decimal Goal, decimal MaxDepth, decimal MinMargin) Adjusted(CampaignBrief b, (bool Accept, decimal? Goal, decimal? MaxDepth, decimal? MinMargin, string Message, long? CallLogId) r) =>
        (r.Goal is > 0 ? r.Goal.Value : b.GoalValue, r.MaxDepth is > 0 ? r.MaxDepth.Value : b.MaxDepthPct, r.MinMargin is >= 0 ? r.MinMargin.Value : b.MinMarginPct);

    private async Task<CampaignPlan> StoreAsync(CampaignBrief brief, int round, PlanResult r, IReadOnlyList<PlanResult> all, string explanation, string basis, CancellationToken ct)
    {
        var t = r.Totals;
        var plan = new CampaignPlan
        {
            BriefId = brief.Id, CalendarId = brief.CalendarId, LayerId = brief.LayerId, Round = round, Strategy = r.Strategy, DiscountedSkus = t.Discounted,
            SkusInScope = t.InScope, HoldoutSkus = r.Lines.Count(l => l.CampaignHoldout), ExpectedUnits = t.Units, ExpectedNetRevenue = t.NetRevenue,
            ExpectedGrossProfit = t.GrossProfit, ExpectedMarginPct = t.MarginPct, GrossProfitWithoutDiscounts = t.GrossProfitWithoutDiscounts,
            AverageDepthPct = t.AverageDepthPct, Feasible = r.Feasible, Feasibility = r.Feasibility, LeftOut = JsonSerializer.Serialize(r.LeftOut, Json),
            Alternatives = JsonSerializer.Serialize(all.Select(a => new { a.Strategy, a.Totals, a.Feasible, a.Feasibility }), Json),
            Explanation = Truncate(explanation, 2000), Basis = Truncate(basis, 1000), IsTest = brief.IsTest, CreatedUtc = _clock.GetUtcNow().UtcDateTime,
        };
        _db.CampaignPlans.Add(plan);
        await _db.SaveChangesAsync(ct);
        _db.CampaignPlanLines.AddRange(r.Lines.Where(l => l.CampaignPrice is not null).Select(l => new CampaignPlanLine
        {
            PlanId = plan.Id, Sku = l.Sku, Category = l.Category, RegularPrice = l.RegularPrice, CurrentPrice = l.CurrentPrice, Cost = l.Cost,
            CampaignPrice = l.CampaignPrice!.Value, DepthPct = l.DepthPct, ExpectedUnits = Math.Round(l.ExpectedUnits, 2), ExpectedGrossProfit = Math.Round(l.ExpectedGrossProfit, 2),
        }));
        await _db.SaveChangesAsync(ct);
        return plan;
    }

    private async Task SayAsync(CampaignBrief brief, long? planId, int round, string from, string text, long? callLogId, CancellationToken ct)
    {
        _db.CampaignMessages.Add(new CampaignMessage
        {
            BriefId = brief.Id, PlanId = planId, Round = round, From = from, Text = Truncate(text, 8000), AiCallLogId = callLogId,
            CreatedUtc = _clock.GetUtcNow().UtcDateTime,
        });
        await _db.SaveChangesAsync(ct);
    }

    private async Task<PlanningResult> FailAsync(CampaignBrief brief, List<CampaignPlan> plans, string why, CancellationToken ct)
    {
        foreach (var p in plans.Where(p => p.Status == PlanStatus.Proposed)) p.Status = PlanStatus.Held;
        await SayAsync(brief, plans.LastOrDefault()?.Id, plans.Count, "code", why, null, ct);
        await _db.SaveChangesAsync(CancellationToken.None);
        return new PlanningResult(brief.Id, "Failed", plans, why);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private sealed record ChoiceDto(string Strategy, string Explanation);
    private sealed record ReviewDto(string Decision, decimal? GoalValue, decimal? MaxDepthPct, decimal? MinMarginPct, string Message);
}
