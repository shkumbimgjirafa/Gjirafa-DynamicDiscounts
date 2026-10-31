using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Campaigns;
using PricingTool.Agent.ClaudeCode;
using PricingTool.Agent.Operations;
using PricingTool.Agent.Storage;

namespace PricingTool.Agent.Chat;

public sealed record ChatTurn(long ConversationId, IReadOnlyList<ChatMessage> Added, string? Error);

/// <summary>
/// The Agent tab's chat (plan section 5, "AgentChat"): a person talks with the agents, grounded in the same read-only data tools.
/// Nova answers by default; "@alpha" asks Alpha. Nova can hand a pricing question to Alpha ("askAlpha"): code asks Alpha, stores
/// its answer in the thread, and asks Nova again with it. Nova can turn a request into a campaign brief: code judges it against
/// the campaign limits and, when it passes, the normal planning loop plans it (Alpha chooses, Nova reviews) and publishes an
/// accepted plan time-boxed, where a person can still stop it before it starts. An instruction becomes a structured rule
/// (<see cref="ChatRule"/>) that only tightens, enforced in code by the campaign planning. The agents never change a price.
/// One call per agent turn; every call is logged like any other.
/// </summary>
public sealed class AgentChat
{
    public const int HistoryMessages = 12;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly AgentDbContext _db;
    private readonly ClaudeCodeClient _client;
    private readonly CampaignPlanning _planning;
    private readonly AgentOptions _options;
    private readonly ILogger<AgentChat> _logger;
    private readonly TimeProvider _clock;

    public AgentChat(AgentDbContext db, ClaudeCodeClient client, CampaignPlanning planning, IOptions<AgentOptions> options, ILogger<AgentChat> logger,
        TimeProvider? clock = null)
    {
        _db = db;
        _client = client;
        _planning = planning;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public static string PromptsDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Prompts");

    /// <summary>Test seam: planning a brief the chat created (the real one runs the planning loop with its model calls).</summary>
    internal Func<long, CancellationToken, Task<PlanningResult>>? PlanSource { get; set; }

    public async Task<ChatTurn> AskAsync(long? conversationId, string text, string author, CancellationToken ct = default)
    {
        var conversation = conversationId ?? (await _db.ChatMessages.MaxAsync(m => (long?)m.ConversationId, ct) ?? 0) + 1;
        var added = new List<ChatMessage>();
        var user = await AddAsync(conversation, "user", text.Trim(), null, added, ct, author);
        var role = text.TrimStart().StartsWith("@alpha", StringComparison.OrdinalIgnoreCase) ? AgentRole.Alpha : AgentRole.Nova;

        var answer = await CallAsync(role, conversation, null, ct);
        if (answer is null) return new ChatTurn(conversation, added, await FailAsync(conversation, role, added, ct));
        if (role == AgentRole.Nova && !string.IsNullOrWhiteSpace(answer.Value.Answer.AskAlpha))
        {
            await AddAsync(conversation, "Nova", $"Asking Alpha: {answer.Value.Answer.AskAlpha}", answer.Value.CallLogId, added, ct);
            var alpha = await CallAsync(AgentRole.Alpha, conversation, answer.Value.Answer.AskAlpha, ct);
            if (alpha is null) return new ChatTurn(conversation, added, await FailAsync(conversation, AgentRole.Alpha, added, ct));
            await AddAsync(conversation, "Alpha", alpha.Value.Answer.Reply, alpha.Value.CallLogId, added, ct);
            answer = await CallAsync(AgentRole.Nova, conversation, null, ct, noMoreAlpha: true);
            if (answer is null) return new ChatTurn(conversation, added, await FailAsync(conversation, role, added, ct));
        }

        var (a, callId) = answer.Value;
        var reply = await AddAsync(conversation, role == AgentRole.Alpha ? "Alpha" : "Nova", a.Reply, callId, added, ct);
        if (a.Rule is { } rule) await RuleAsync(conversation, rule, text, author, reply, added, ct);
        if (role == AgentRole.Nova && a.Brief is { } brief) await BriefAsync(conversation, brief, reply, added, author, ct);
        if (role == AgentRole.Nova && a.Goal is { } goal) await GoalAsync(conversation, goal, text, author, reply, added, ct);
        return new ChatTurn(conversation, added, null);
    }

    /// <summary>The Monday report, written by code from the stored records, as the first message of a new conversation.</summary>
    public async Task<ChatTurn> WeeklyReportAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var since = now.AddDays(-7);
        var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
        var real = _db.AgentPasses.Where(p => !p.IsTest).Select(p => p.Id);
        var decisions = await _db.AgentDecisions.AsNoTracking().Where(d => d.CreatedUtc >= since && real.Contains(d.PassId) && d.Action != "keep").ToListAsync(ct);
        var batches = await _db.PublishBatches.AsNoTracking().Where(b => b.StartedUtc >= since).ToListAsync(ct);
        var calls = await _db.AiCallLog.AsNoTracking().Where(c => c.CalledAtUtc >= since).ToListAsync(ct);
        var coming = await _db.CampaignCalendar.AsNoTracking().Where(c => c.StartDate >= today && c.StartDate <= today.AddDays(21)).OrderBy(c => c.StartDate).ToListAsync(ct);
        var ledger = await _db.MoneyLedger.AsNoTracking().Where(m => m.Measured30Eur != null).ToListAsync(ct);
        var results = await _db.CampaignResults.AsNoTracking().Where(r => r.CreatedUtc >= since).ToListAsync(ct);
        var sb = new StringBuilder($"Weekly report, {today:ddd dd MMM yyyy} (written by code from the stored records; the last 7 days).\n");
        sb.AppendLine($"- Changes: {decisions.Count(d => d.Status == DecisionStatus.Applied)} applied within the limits, {decisions.Count(d => d.Status == DecisionStatus.Held)} held for a person " +
                      $"({string.Join(", ", decisions.GroupBy(d => d.Source).Select(g => $"{g.Key} {g.Count()}"))}).");
        sb.AppendLine($"- Publishes: {batches.Count(b => b.Status == "Published" && b.RolledBackUtc == null)} live, {batches.Count(b => b.RolledBackUtc != null)} rolled back " +
                      $"({batches.Where(b => b.RolledBackUtc == null).Sum(b => b.Created + b.Updated):N0} discounts and {batches.Where(b => b.RolledBackUtc == null).Sum(b => b.TierRaised + b.TierInserted):N0} " +
                      $"regular prices written to {(batches.FirstOrDefault()?.Target ?? "the sandbox")}).");
        var valves = await _db.DailyCycleSteps.AsNoTracking()
            .Where(s => s.Step == "size-check" && s.StartedUtc >= since && s.Detail != null && s.Detail.Contains("ran at once")).OrderBy(s => s.Id).ToListAsync(ct);
        if (valves.Count > 0)
            sb.AppendLine("- Size valve (LocalDB's 10 GB cap): " + string.Join("; ", valves.Select(v => $"{v.CycleDate:ddd dd MMM}: {v.Detail}")) + ".");
        // The daily publish to the sandbox, which follows the live shop: the shadow of the live shelf, the latest per layer.
        var shelf = batches.Where(b => b.Kind == "Regular" && b.Status == "Published" && b.RolledBackUtc == null)
            .GroupBy(b => b.LayerId).Select(g => g.OrderByDescending(b => b.Id).First()).OrderBy(b => b.LayerId).ToList();
        if (shelf.Count > 0)
            sb.AppendLine("- On the shelf at our price (the latest daily publish to the sandbox, which follows the live shop): " + string.Join("; ", shelf.Select(b =>
                $"layer {b.LayerId} {b.RoundTripChecked - b.RoundTripMismatches:N0} of {b.Requested:N0} checked prices (batch #{b.Id})")) + "; what held the rest is the next line.");
        var leftOut = await Operations.ExceptionsInbox.PublishSkusAsync(_db, since, now, 0, ct);
        if (leftOut.Count > 0)
            sb.AppendLine("- Left out by the publisher, once per SKU: " + string.Join("; ", leftOut.Select(g =>
                $"layer {g.LayerId} {g.Kind} {g.Skus:N0} SKUs ({g.NewInLastDay:N0} new in the last day, up to {g.MostDays} days)")) + ".");
        sb.AppendLine(ledger.Count > 0
            ? $"- Results vs the holdout: {ledger.Sum(m => m.Measured30Eur!.Value):N0} EUR gross profit over 30 days across {ledger.Count} measured changes (measured)."
            : "- Results vs the holdout: nothing measured yet (no pushed change has completed its window).");
        foreach (var r in results) sb.AppendLine($"- A finished campaign was reviewed (calendar #{r.CalendarId}): {r.Lesson}");
        sb.AppendLine(coming.Count > 0 ? $"- Coming up: {string.Join("; ", coming.Select(c => $"{c.Name} from {c.StartDate:ddd dd MMM} ({c.Status})"))}." : "- Coming up: no campaign in the next three weeks.");
        sb.Append($"- AI: {calls.Count} calls, {calls.Sum(c => c.CostUsd ?? 0m):0.00} USD (measured).");
        var conversation = (await _db.ChatMessages.MaxAsync(m => (long?)m.ConversationId, ct) ?? 0) + 1;
        var added = new List<ChatMessage>();
        await AddAsync(conversation, "Nova", sb.ToString(), null, added, ct);
        return new ChatTurn(conversation, added, null);
    }

    private async Task<(ChatAnswer Answer, long? CallLogId)?> CallAsync(AgentRole role, long conversation, string? handedQuestion, CancellationToken ct,
        bool noMoreAlpha = false)
    {
        var history = (await _db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == conversation).OrderByDescending(m => m.Id).Take(HistoryMessages).ToListAsync(ct))
            .OrderBy(m => m.Id).ToList();
        var rules = await _db.ChatRules.AsNoTracking().Where(r => r.RemovedUtc == null).OrderBy(r => r.Id).ToListAsync(ct);
        var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
        var sb = new StringBuilder();
        sb.AppendLine($"You are {(role == AgentRole.Alpha ? "Alpha, the pricing agent" : "Nova, the campaign agent")}. Today is {today:yyyy-MM-dd} ({today.DayOfWeek}).");
        sb.AppendLine($"Campaign limits code enforces: starts on or after {today.AddDays(_options.Campaigns.LeadDays):yyyy-MM-dd}; a mini-campaign runs {_options.Campaigns.MinDays}-{_options.Campaigns.MaxMiniDays} days; " +
                      $"depth at most {_options.Campaigns.MaxDepthPct:0.#}%.");
        sb.AppendLine("Active rules: " + (rules.Count == 0 ? "none." : string.Join("; ", rules.Select(r => $"#{r.Id} {r.Kind} {r.Value} {r.FromDate:yyyy-MM-dd}..{r.ToDate:yyyy-MM-dd}{(r.LayerId is { } l ? $" layer {l}" : "")}"))));
        sb.Append(await Goals.BlockAsync(_db, today, null, ct));
        sb.AppendLine();
        sb.AppendLine(await CampaignStateAsync(today, ct));
        sb.AppendLine("The conversation so far (oldest first):");
        foreach (var m in history) sb.AppendLine($"[{m.From}] {m.Text}");
        sb.AppendLine();
        if (handedQuestion is not null) sb.AppendLine($"Nova hands you this question: {handedQuestion}\nAnswer it in `reply`; leave askAlpha, brief, rule and goal null.");
        else if (role == AgentRole.Alpha) sb.AppendLine("Answer the person's last message in `reply`. You don't write briefs or record goals and don't ask Alpha: brief, goal and askAlpha stay null.");
        else sb.AppendLine(noMoreAlpha ? "Alpha has answered above. Answer the person's last message now; askAlpha stays null." : "Answer the person's last message.");

        var call = await _client.CallAsync(new AgentCallRequest(role, $"chat {conversation}", sb.ToString(), Path.Combine(PromptsDirectory, "chat-rules.md"))
        {
            AppendSystemPromptFiles = new[] { Path.Combine(PromptsDirectory, "agent-data-dictionary.md") },
            JsonSchema = await File.ReadAllTextAsync(Path.Combine(PromptsDirectory, "chat-answer.schema.json"), ct),
            UseDataTools = true,
            TimeoutSeconds = _options.AlphaPassTimeoutSeconds,
            MaxBudgetUsd = _options.AlphaPassMaxBudgetUsd,
        }, ct);
        var answer = call.Succeeded ? call.Deserialize<ChatAnswer>(Json) : null;
        if (answer is null || string.IsNullOrWhiteSpace(answer.Reply)) return null;
        if (role == AgentRole.Alpha || handedQuestion is not null) answer = answer with { AskAlpha = null, Brief = null, Goal = null };
        if (noMoreAlpha) answer = answer with { AskAlpha = null };
        return (answer, call.CallLogIds.Count > 0 ? call.CallLogIds[^1] : null);
    }

    /// <summary>
    /// The agents' own campaign records, which the data tools don't show: each campaign from two weeks ago to two months ahead with its
    /// latest brief and plan (status, verdict, expected figures, the goal check), so "where does X stand?" is answered from them.
    /// </summary>
    private async Task<string> CampaignStateAsync(DateOnly today, CancellationToken ct)
    {
        var entries = await _db.CampaignCalendar.AsNoTracking().Where(c => c.EndDate >= today.AddDays(-14) && c.StartDate <= today.AddDays(60))
            .OrderBy(c => c.StartDate).ToListAsync(ct);
        var sb = new StringBuilder("Campaigns now (the stored briefs and plans; plan figures are code's estimates):" + Environment.NewLine);
        if (entries.Count == 0) sb.AppendLine("- none in the window");
        foreach (var e in entries)
        {
            var brief = await _db.CampaignBriefs.AsNoTracking().Where(b => b.CalendarId == e.Id && !b.IsTest && b.Status != BriefStatus.Superseded)
                .OrderByDescending(b => b.Id).FirstOrDefaultAsync(ct);
            var plan = brief is null ? null : await _db.CampaignPlans.AsNoTracking().Where(p => p.BriefId == brief.Id && !p.IsTest).OrderByDescending(p => p.Id).FirstOrDefaultAsync(ct);
            sb.Append($"- #{e.Id} {e.Name}, layer {e.LayerId}, {e.StartDate:yyyy-MM-dd}..{e.EndDate:yyyy-MM-dd}, {e.Status}");
            if (e.GoalType is not null) sb.Append($", goal {e.GoalType} {e.GoalValue:0.##}{(e.GoalSetBy is null ? "" : $" (set by a person: {e.GoalSetBy})")}");
            sb.Append(brief is null ? "; no brief yet" : $"; brief #{brief.Id} {brief.Status}");
            if (plan is not null)
                sb.Append($"; plan #{plan.Id} {plan.Strategy} {plan.Status}{(string.IsNullOrEmpty(plan.GoalVerdict) ? "" : $" ({plan.GoalVerdict})")}{(plan.WaitsForPerson ? ", waiting for a person" : "")}: " +
                          $"{plan.DiscountedSkus} SKUs at {plan.AverageDepthPct:0.0}%, expected GP {plan.ExpectedGrossProfit:N0}, margin {plan.ExpectedMarginPct:0.0}%. Goal check: {plan.Feasibility}");
            if (e.Context is not null) sb.Append($" Context: {e.Context}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>A goal the person stated: checked by code (a layer the agents work on, a month or week not over, a sensible value),
    /// stored for the whole month or Monday-to-Sunday week around the date Nova read, replacing the one before for the same layer,
    /// period and kind.</summary>
    private async Task GoalAsync(long conversation, ChatGoalDto g, string instruction, string author, ChatMessage reply, List<ChatMessage> added, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
        string? problem = null;
        DateOnly day = default;
        if (!DateOnly.TryParseExact(g.Start, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) problem = $"'{g.Start}' isn't a yyyy-MM-dd date.";
        problem ??= Goals.Problem(g.Layer, g.Period, g.Kind, g.Value, day, today);
        if (problem is not null)
        {
            await AddAsync(conversation, "code", $"No goal recorded: {problem}", null, added, ct);
            return;
        }
        var (start, end) = Goals.PeriodOf(g.Period, day);
        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var old in await _db.Goals.Where(x => x.LayerId == g.Layer && x.Period == g.Period && x.Kind == g.Kind && x.Start == start && x.RetiredUtc == null).ToListAsync(ct))
            (old.RetiredUtc, old.RetiredBy) = (now, author);
        var goal = new GoalRecord
        {
            LayerId = g.Layer, Period = g.Period, Start = start, End = end, Kind = g.Kind, Value = g.Kind == GoalKinds.Margin ? Math.Round(g.Value, 1) : Math.Round(g.Value, 0),
            SetBy = author, Instruction = instruction.Length > 1000 ? instruction[..1000] : instruction, MessageId = reply.Id, CreatedUtc = now,
        };
        _db.Goals.Add(goal);
        await _db.SaveChangesAsync(ct);
        await AddAsync(conversation, "code", $"Goal #{goal.Id} set: {Goals.Describe(goal)}. The Run tab measures it on the booked sales lines; Nova and Alpha read it with their weekly passes.",
            null, added, ct);
    }

    private async Task RuleAsync(long conversation, ChatRuleDto r, string instruction, string author, ChatMessage reply, List<ChatMessage> added, CancellationToken ct)
    {
        string? problem = null;
        if (!ChatRuleKinds.All.Contains(r.Kind)) problem = $"'{r.Kind}' isn't a kind of rule code can enforce.";
        else if (!DateOnly.TryParseExact(r.FromDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
                 || !DateOnly.TryParseExact(r.ToDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to) || to < from)
            problem = $"Dates '{r.FromDate}'..'{r.ToDate}' aren't a yyyy-MM-dd range.";
        else if (string.IsNullOrWhiteSpace(r.Value)) problem = "The rule has no brand, category or margin.";
        else if (r.Kind == ChatRuleKinds.MinCampaignMargin && (!decimal.TryParse(r.Value.TrimEnd('%', ' '), NumberStyles.Number, CultureInfo.InvariantCulture, out var pct) || pct <= 0 || pct >= 100))
            problem = $"'{r.Value}' isn't a margin in percent.";
        else if (r.Layer is { } l && l is not (1 or 4)) problem = $"Layer {l} isn't one the agents work on.";
        else
        {
            var rule = new ChatRule
            {
                LayerId = r.Layer, Kind = r.Kind, Value = r.Kind == ChatRuleKinds.MinCampaignMargin ? r.Value.TrimEnd('%', ' ') : r.Value.Trim(),
                FromDate = from, ToDate = to, Instruction = instruction.Length > 1000 ? instruction[..1000] : instruction,
                CreatedBy = author, CreatedUtc = _clock.GetUtcNow().UtcDateTime,
            };
            _db.ChatRules.Add(rule);
            await _db.SaveChangesAsync(ct);
            reply.RuleId = rule.Id;
            await AddAsync(conversation, "code", $"Rule #{rule.Id} in force: {rule.Kind} {rule.Value} from {from:yyyy-MM-dd} to {to:yyyy-MM-dd}" +
                                                 $"{(rule.LayerId is { } layer ? $" on layer {layer}" : " on both layers")}. " +
                                                 (rule.Kind == ChatRuleKinds.ProtectDiscount
                                                     ? "The publisher never ends a team discount whose name matches it, even under the floor"
                                                     : "Campaign planning enforces it") + "; it's listed on the Chat tab.",
                null, added, ct);
            return;
        }
        await AddAsync(conversation, "code", $"No rule recorded: {problem}", null, added, ct);
    }

    private async Task BriefAsync(long conversation, ChatBriefDto b, ChatMessage reply, List<ChatMessage> added, string author, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
        var pass = new AgentPass { LayerId = b.Layer, Source = CampaignAgent.Source, Mode = "Chat", StartedUtc = now, FinishedUtc = now, Status = "Succeeded",
            Summary = $"Brief from the chat (conversation {conversation})." };
        _db.AgentPasses.Add(pass);
        await _db.SaveChangesAsync(ct);
        var brief = new CampaignBrief
        {
            PassId = pass.Id, LayerId = b.Layer, Name = b.Name.Length > 200 ? b.Name[..200] : b.Name, Kind = "mini", ScopeKind = b.ScopeKind,
            ScopeValue = b.ScopeKind == "all" ? null : b.ScopeValue?.Trim(), GoalType = b.GoalType, GoalValue = b.GoalValue, MinMarginPct = b.MinMarginPct,
            MaxDepthPct = b.MaxDepthPct, Rationale = b.Rationale.Length > 2000 ? b.Rationale[..2000] : b.Rationale, Exclusions = "[]", CreatedUtc = now,
        };
        _db.CampaignBriefs.Add(brief);
        if (!DateOnly.TryParseExact(b.StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !DateOnly.TryParseExact(b.EndDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
        {
            (brief.Status, brief.Limit, brief.LimitDetail) = (BriefStatus.Rejected, "malformed", $"Dates '{b.StartDate}'..'{b.EndDate}' aren't yyyy-MM-dd.");
            await _db.SaveChangesAsync(ct);
            await AddAsync(conversation, "code", $"Brief #{brief.Id} rejected: {brief.LimitDetail}", null, added, ct);
            return;
        }
        (brief.StartDate, brief.EndDate) = (start, end);
        var calendar = await _db.CampaignCalendar.AsNoTracking().Where(c => c.LayerId == b.Layer).ToListAsync(ct);
        var holdouts = _options.Limits.Holdouts.TryGetValue(b.Layer.ToString(CultureInfo.InvariantCulture), out var h) ? h : new List<string>();
        var verdict = CampaignLimits.Evaluate(new BriefInput("mini", null, brief.ScopeKind, brief.ScopeValue, start, end, b.GoalType, b.GoalValue, b.MinMarginPct, b.MaxDepthPct),
            b.Layer, today, calendar, c => holdouts.Contains(c, StringComparer.OrdinalIgnoreCase), _options.Campaigns);
        (brief.Status, brief.MaxDepthPct, brief.Limit, brief.LimitDetail) = (verdict.Status, verdict.MaxDepthPct, verdict.Limit, verdict.Detail);
        await _db.SaveChangesAsync(ct);
        reply.BriefId = brief.Id;
        if (brief.Status != BriefStatus.Proposed)
        {
            await _db.SaveChangesAsync(ct);
            await AddAsync(conversation, "code", $"Brief #{brief.Id} {brief.Status}{(brief.Limit is null ? "" : $" ({brief.Limit})")}: {brief.LimitDetail}", null, added, ct);
            return;
        }
        var entry = new CampaignCalendarEntry
        {
            LayerId = b.Layer, Name = brief.Name, StartDate = start, EndDate = end, Type = "mini", ScopeKind = brief.ScopeKind, ScopeValue = brief.ScopeValue,
            GoalType = brief.GoalType, GoalValue = brief.GoalValue, Status = CampaignStatus.Proposed, CreatedBy = CampaignAgent.Source, CreatedUtc = now,
            Notes = $"From the chat, conversation {conversation}.",
            // A person asked for this campaign in the chat, so its goal is theirs: binding, like a goal set on the calendar
            // (the campaign agent can't lower it to make a plan pass; a plan short of it waits for that person).
            GoalSetBy = $"{author} in the chat (conversation {conversation})",
        };
        _db.CampaignCalendar.Add(entry);
        await _db.SaveChangesAsync(ct);
        brief.CalendarId = entry.Id;
        await _db.SaveChangesAsync(ct);
        await AddAsync(conversation, "code", $"Brief #{brief.Id} passes the campaign limits{(brief.LimitDetail is null ? "" : $" ({brief.LimitDetail})")}; the pricing agent is planning it.", null, added, ct);

        var plan = await (PlanSource ?? ((id, c) => _planning.PlanAsync(id, publish: true, c)))(brief.Id, ct);
        var final = plan.Final;
        await AddAsync(conversation, "code", final is null
            ? $"Planning stopped: {plan.Error}"
            : $"Plan #{final.Id} ({final.Strategy}): {final.DiscountedSkus:N0} SKUs at {final.AverageDepthPct:0.0}% on average, expected gross profit {final.ExpectedGrossProfit:N0}, " +
              $"margin {final.ExpectedMarginPct:0.0}% (estimated). {final.Feasibility} Status: {final.Status}" +
              (final.Status == PlanStatus.Published ? $"; publishes {start:ddd dd MMM}, and a person can stop it on the Campaigns tab until then." : "."),
            null, added, ct);
    }

    private async Task<string> FailAsync(long conversation, AgentRole role, List<ChatMessage> added, CancellationToken ct)
    {
        var why = $"{role} couldn't answer (no valid reply after a retry; see the AI call log). Nothing changed.";
        await AddAsync(conversation, "code", why, null, added, ct);
        _logger.LogWarning("Chat {Conversation}: {Role} gave no valid answer", conversation, role);
        return why;
    }

    /// <summary>An agent's own message in the thread (a report, what it fixed, what needs a person), in the newest conversation:
    /// the Chat tab shows one continuous thread.</summary>
    public async Task<ChatMessage> PostAsync(string from, string text, long? callLogId = null, CancellationToken ct = default)
    {
        var conversation = await _db.ChatMessages.MaxAsync(m => (long?)m.ConversationId, ct) ?? 1;
        return await AddAsync(conversation, from, text, callLogId, new List<ChatMessage>(), ct);
    }

    private async Task<ChatMessage> AddAsync(long conversation, string from, string text, long? callLogId, List<ChatMessage> added, CancellationToken ct, string? author = null)
    {
        var m = new ChatMessage
        {
            ConversationId = conversation, From = from, Author = author, Text = text.Length > 8000 ? text[..8000] : text,
            CreatedUtc = _clock.GetUtcNow().UtcDateTime, AiCallLogId = callLogId,
        };
        _db.ChatMessages.Add(m);
        await _db.SaveChangesAsync(ct);
        added.Add(m);
        return m;
    }

    private sealed record ChatAnswer(string Reply, string? AskAlpha, ChatBriefDto? Brief, ChatRuleDto? Rule, ChatGoalDto? Goal = null);

    private sealed record ChatGoalDto(int Layer, string Period, string Start, string Kind, decimal Value);

    private sealed record ChatBriefDto(int Layer, string Name, string ScopeKind, string? ScopeValue, string StartDate, string EndDate, string GoalType,
        decimal GoalValue, decimal MinMarginPct, decimal MaxDepthPct, string Rationale);

    private sealed record ChatRuleDto(int? Layer, string Kind, string Value, string FromDate, string ToDate);
}
