using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PricingTool.Agent.ClaudeCode;
using PricingTool.Agent.Storage;
using PricingTool.Data;
using PricingTool.Data.Entities;

namespace PricingTool.Agent.Alpha;

/// <summary>What one pass did.</summary>
public sealed record AlphaPassResult(long? PassId, string Status, string? Error, IReadOnlyList<AgentDecision> Decisions)
{
    public int Count(string status) => Decisions.Count(d => d.Status == status);
}

/// <summary>
/// The pricing agent's weekly floor and target pass for one layer (plan section 5, "FloorTargetAgent").
/// One Claude Code call reads the scorecard, may investigate with the data tools, and proposes one decision
/// per band × category. Code then decides what happens: <see cref="SafetyLimits"/> judges every row, thin
/// and holdout rows stay as they are, and only decisions within the limits are applied (Autonomous) or
/// queued (Approve-first). A failed call changes nothing.
///
/// Applied changes are written as per-category overrides (PricingTool.BandCategoryRules); the band's own
/// hand-set values stay untouched as the baseline. The engine reads the overrides from the per-category
/// floors step on.
/// </summary>
public sealed class FloorTargetAgent
{
    public const string Source = "Alpha";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IAlphaInputSource _input;
    private readonly ClaudeCodeClient _client;
    private readonly AgentDbContext _db;
    private readonly PricingToolDbContext _tool;
    private readonly AgentModeStore _modes;
    private readonly AgentOptions _options;
    private readonly ILogger<FloorTargetAgent> _logger;
    private readonly TimeProvider _clock;
    private readonly IKillSwitch _killSwitch;

    public FloorTargetAgent(IAlphaInputSource input, ClaudeCodeClient client, AgentDbContext db, PricingToolDbContext tool,
        AgentModeStore modes, IOptions<AgentOptions> options, ILogger<FloorTargetAgent> logger, TimeProvider? clock = null,
        IKillSwitch? killSwitch = null)
    {
        _input = input;
        _client = client;
        _db = db;
        _tool = tool;
        _modes = modes;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _killSwitch = killSwitch ?? new ConfigKillSwitch(_options);
    }

    /// <summary>Where the rules, the decision schema and the data dictionary ship.</summary>
    public static string PromptsDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Prompts");

    /// <param name="isTest">Demo, backtest or injected test: stored and labelled, but it never applies anything
    /// (within-limit changes stay Pending) and writes no time-sheet row.</param>
    /// <param name="injectedTestNote">Test passes only: text appended to the prompt, labelled [INJECTED-TEST]
    /// (e.g. a looser limit than code enforces, to show code holds what the model asks beyond it).</param>
    public async Task<AlphaPassResult> RunPassAsync(int layerId, bool isTest = false, CancellationToken ct = default,
        string? injectedTestNote = null)
    {
        if (injectedTestNote is not null && !isTest)
            throw new ArgumentException("An injected test note is only allowed on a test pass.", nameof(injectedTestNote));
        var stop = await _killSwitch.GetAsync(ct);
        if (stop.Stopped)
            return new AlphaPassResult(null, "Skipped", $"Stopped by the kill switch ({stop.Reason}): no pass, nothing changed.", Array.Empty<AgentDecision>());
        var mode = await _modes.GetAsync(layerId, ct);
        if (mode == AutonomyMode.Off)
            return new AlphaPassResult(null, "Skipped", $"Layer {layerId} is Off: no pass.", Array.Empty<AgentDecision>());

        var sw = Stopwatch.StartNew();
        var now = _clock.GetUtcNow().UtcDateTime;
        var pass = new AgentPass { LayerId = layerId, Source = Source, Mode = mode.ToString(), StartedUtc = now, Status = "Running", IsTest = isTest };
        _db.AgentPasses.Add(pass);
        await _db.SaveChangesAsync(ct);

        var card = await _input.BuildAsync(layerId, ct);
        var overrides = (await _tool.BandCategoryRules.AsNoTracking().Where(r => r.LayerId == layerId).ToListAsync(ct))
            .ToDictionary(r => Key(r.BandId, r.Category));
        var bands = card.Bands.ToDictionary(b => b.BandId);
        var effective = card with { Rows = card.Rows.Select(r => WithOverride(r, overrides)).ToList() };
        var rows = effective.Rows.Where(r => r.BandId is { } id && bands.ContainsKey(id) && r.Units > 0).ToList();
        var recent = await AppliedChangesSinceAsync(layerId, now.AddDays(-7), ct);
        var paused = (await _db.CategoryPauses.AsNoTracking().Where(p => p.LayerId == layerId && p.ReleasedUtc == null).ToListAsync(ct))
            .Select(p => Key(p.BandId, p.Category)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var prompt = BuildPrompt(effective, rows, mode, recent);
        if (injectedTestNote is not null) prompt += $"\n[INJECTED-TEST] {injectedTestNote}\n";
        var call = await _client.CallAsync(new AgentCallRequest(AgentRole.Alpha,
            isTest ? $"[INJECTED-TEST] floor-target L{layerId}" : $"floor-target L{layerId}",
            prompt, Path.Combine(PromptsDirectory, "pricing-agent-rules.md"))
        {
            AppendSystemPromptFiles = new[] { Path.Combine(PromptsDirectory, "agent-data-dictionary.md") },
            JsonSchema = await File.ReadAllTextAsync(Path.Combine(PromptsDirectory, "floor-target-decision.schema.json"), ct),
            UseDataTools = true,
            TimeoutSeconds = _options.AlphaPassTimeoutSeconds,
            MaxBudgetUsd = _options.AlphaPassMaxBudgetUsd,
        }, ct);
        pass.CallTag = call.CallTag;
        pass.AiCallLogId = call.CallLogIds.Count > 0 ? call.CallLogIds[^1] : null;

        var answer = call.Succeeded ? call.Deserialize<AlphaAnswer>() : null;
        if (answer is null || answer.Layer != layerId)
        {
            pass.Status = "Failed";
            pass.Error = call.Succeeded ? $"The answer was for layer {answer?.Layer}, not {layerId}." : $"{call.Status}: {call.Error}";
            pass.FinishedUtc = _clock.GetUtcNow().UtcDateTime;
            await _db.SaveChangesAsync(CancellationToken.None);
            _logger.LogWarning("Alpha pass {Pass} on layer {Layer} failed, nothing changed: {Error}", pass.Id, layerId, pass.Error);
            return new AlphaPassResult(pass.Id, pass.Status, pass.Error, Array.Empty<AgentDecision>());
        }

        var proposals = new Dictionary<string, AlphaDecisionDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in answer.Decisions) proposals.TryAdd(Key(d.BandId, d.Category), d);
        var queryIds = await _db.AgentQueryLog.Where(q => q.CallTag == call.CallTag).Select(q => q.Id).ToListAsync(ct);

        var decisions = new List<AgentDecision>();
        foreach (var row in rows)
        {
            var key = Key(row.BandId!.Value, row.Category);
            var floor = row.FloorPct!.Value;
            var target = row.TargetPct ?? floor;
            recent.TryGetValue(key, out var moved);
            var decision = NewDecision(pass, row.BandId.Value, row.Category, floor, target, call, queryIds);
            decisions.Add(decision);

            if (!proposals.Remove(key, out var p))
            {
                decision.Action = "keep";
                decision.Status = DecisionStatus.Rejected;
                decision.Limit = "missing";
                decision.Reason = "No decision came back for this row; nothing changed.";
                continue;
            }

            var verdict = SafetyLimits.Evaluate(new LimitInput(layerId, row.Category, row.ThinData, _options.Limits.IsHoldout(layerId, row.Category),
                floor, target, floor - moved.Floor, target - moved.Target, p.Action, p.NewFloorPct, p.NewTargetPct, paused.Contains(key)), _options.Limits);
            Fill(decision, p, queryIds, verdict.Action, verdict.Floor, verdict.Target,
                verdict.Status == DecisionStatus.Applied && verdict.Action != "keep" && (mode == AutonomyMode.ApproveFirst || isTest)
                    ? DecisionStatus.Pending : verdict.Status,
                verdict.Limit, verdict.Detail);
        }
        foreach (var p in proposals.Values)   // decisions for rows nobody asked about
        {
            var known = bands.TryGetValue(p.BandId, out var band);
            var decision = NewDecision(pass, p.BandId, p.Category, known ? band!.MarginFloorPct : 0, known ? band!.TargetMarginPct : null, call, queryIds);
            Fill(decision, p, queryIds, p.Action, p.NewFloorPct, p.NewTargetPct, DecisionStatus.Rejected, "unknown-row",
                "Not a scorecard row with sales this week.");
            decisions.Add(decision);
        }

        _db.AgentDecisions.AddRange(decisions);
        await _db.SaveChangesAsync(CancellationToken.None);
        await ApplyAsync(layerId, decisions.Where(d => d.Status == DecisionStatus.Applied && d.Action != "keep").ToList(), overrides);

        pass.Applied = decisions.Count(d => d.Status == DecisionStatus.Applied);
        pass.Held = decisions.Count(d => d.Status == DecisionStatus.Held);
        pass.Rejected = decisions.Count(d => d.Status == DecisionStatus.Rejected);
        pass.Pending = decisions.Count(d => d.Status == DecisionStatus.Pending);
        pass.Summary = Truncate(isTest ? "[INJECTED-TEST] " + answer.Summary : answer.Summary, 4000);
        pass.Status = "Succeeded";
        pass.FinishedUtc = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(CancellationToken.None);

        if (!isTest) await WriteTimeSheetAsync(pass, card, decisions, sw.Elapsed);
        return new AlphaPassResult(pass.Id, pass.Status, null, decisions);
    }

    /// <summary>Who reviewed a held or pending decision, and what came of it.</summary>
    public sealed record DecisionReview(bool Ok, string Message);

    /// <summary>
    /// A person approves a held (or approve-first pending) decision from the exceptions inbox. It is written
    /// through the same path as an automatic one. A person decides what the limits hold for a person: a move past the
    /// weekly step size, and a change in a category paused after a rollback (approving it applies that change; the
    /// pause stays for the next pass). The hard minimum floor, target at or above the floor and the holdout still hold,
    /// test passes never apply, and a decision whose starting floor or target has changed since the pass is refused.
    /// </summary>
    public async Task<DecisionReview> ApproveAsync(long decisionId, string by, CancellationToken ct = default)
    {
        var d = await _db.AgentDecisions.SingleOrDefaultAsync(x => x.Id == decisionId, ct);
        if (d is null) return new(false, $"Decision #{decisionId} doesn't exist.");
        if (d.Status is not (DecisionStatus.Held or DecisionStatus.Pending))
            return new(false, $"Decision #{d.Id} is {d.Status}: there is nothing to approve.");
        if (d.Source != Source)
            return new(false, $"Decision #{d.Id} is the {d.Source}'s, not a floor/target decision: it is handled, not approved.");
        if (await _db.AgentPasses.AsNoTracking().AnyAsync(p => p.Id == d.PassId && p.IsTest, ct))
            return new(false, $"Decision #{d.Id} comes from a test pass; test passes never apply.");
        if (d.Action == "keep")
            return new(false, $"Decision #{d.Id} keeps the current values: there is nothing to apply.");

        var hardMin = _options.Limits.HardMinFloorFor(d.LayerId);
        if (d.ProposedFloorPct < hardMin)
            return new(false, $"Decision #{d.Id}: floor {d.ProposedFloorPct:0.##}% is under the hard minimum of {hardMin:0.##}% for this layer.");
        if (d.ProposedTargetPct is { } t && t < d.ProposedFloorPct)
            return new(false, $"Decision #{d.Id}: target {t:0.##}% is under its floor {d.ProposedFloorPct:0.##}%.");
        if (_options.Limits.IsHoldout(d.LayerId, d.Category))
            return new(false, $"Decision #{d.Id}: '{d.Category}' is a holdout category and stays on its hand-set values.");

        var band = await _tool.PriceBands.AsNoTracking().SingleOrDefaultAsync(b => b.Id == d.BandId && b.LayerId == d.LayerId, ct);
        if (band is null) return new(false, $"Decision #{d.Id}: band {d.BandId} no longer exists on layer {d.LayerId}.");
        var existing = (await _tool.BandCategoryRules.AsNoTracking().Where(r => r.LayerId == d.LayerId).ToListAsync(ct))
            .ToDictionary(r => Key(r.BandId, r.Category));
        existing.TryGetValue(Key(d.BandId, d.Category), out var rule);
        var floorNow = rule?.MarginFloorPct ?? band.MarginFloorPct;
        var targetNow = rule?.TargetMarginPct ?? band.TargetMarginPct ?? floorNow;
        if (Math.Abs(floorNow - d.CurrentFloorPct) > 0.005m || Math.Abs(targetNow - (d.CurrentTargetPct ?? floorNow)) > 0.005m)
            return new(false, $"Decision #{d.Id}: the values it started from changed since the pass (floor {d.CurrentFloorPct:0.##}% → now " +
                              $"{floorNow:0.##}%, target {d.CurrentTargetPct:0.##}% → now {targetNow:0.##}%). The next pass decides again.");

        var now = _clock.GetUtcNow().UtcDateTime;
        d.Status = DecisionStatus.Applied;
        d.ReviewedBy = by;
        d.ReviewedUtc = now;
        d.LimitDetail = Truncate($"{d.LimitDetail} Approved by {by}.".Trim(), 500);
        await ApplyAsync(d.LayerId, new[] { d }, existing, by, $"approved by {by} in the exceptions inbox");
        if (d.Status == DecisionStatus.Applied)
            return new(true, $"Decision #{d.Id} applied: {d.Category}, floor {d.CurrentFloorPct:0.##}% → {d.ProposedFloorPct:0.##}%, " +
                             $"target {d.CurrentTargetPct:0.##}% → {d.ProposedTargetPct:0.##}%.");
        d.ReviewedBy = null;
        d.ReviewedUtc = null;
        await _db.SaveChangesAsync(CancellationToken.None);
        return new(false, $"Decision #{d.Id} could not be written and stays held: {d.LimitDetail}");
    }

    /// <summary>A person rejects a held or pending decision: nothing changes, the limit that held it is kept,
    /// and who and when are recorded and audited.</summary>
    public async Task<DecisionReview> RejectAsync(long decisionId, string by, CancellationToken ct = default)
    {
        var d = await _db.AgentDecisions.SingleOrDefaultAsync(x => x.Id == decisionId, ct);
        if (d is null) return new(false, $"Decision #{decisionId} doesn't exist.");
        if (d.Status is not (DecisionStatus.Held or DecisionStatus.Pending))
            return new(false, $"Decision #{d.Id} is {d.Status}: there is nothing to reject.");

        var now = _clock.GetUtcNow().UtcDateTime;
        d.Status = DecisionStatus.Rejected;
        d.ReviewedBy = by;
        d.ReviewedUtc = now;
        d.LimitDetail = Truncate($"{d.LimitDetail} Rejected by {by}.".Trim(), 500);
        _tool.AuditLog.Add(new AuditLogEntry
        {
            LayerId = d.LayerId, TimestampUtc = now, UserName = by, Category = AgentModeStore.AuditCategory,
            Action = $"Decision #{d.Id} rejected in the exceptions inbox", EntityType = nameof(AgentDecision), EntityId = d.Id.ToString(CultureInfo.InvariantCulture),
            OldValue = $"floor {d.CurrentFloorPct:0.##}%, target {d.CurrentTargetPct:0.##}%",
            NewValue = $"unchanged (proposed floor {d.ProposedFloorPct:0.##}%, target {d.ProposedTargetPct:0.##}%)",
        });
        await _tool.SaveChangesAsync(ct);
        await _db.SaveChangesAsync(ct);
        return new(true, $"Decision #{d.Id} rejected: {d.Category} keeps floor {d.CurrentFloorPct:0.##}%, target {d.CurrentTargetPct:0.##}%.");
    }

    /// <summary>Writes the applied changes as category overrides, with an audit row each, in one save. If the
    /// write fails, those decisions are held instead: nothing is half-applied.</summary>
    private async Task ApplyAsync(int layerId, IReadOnlyList<AgentDecision> toApply, IReadOnlyDictionary<string, BandCategoryRule> existing,
        string? by = null, string? how = null)
    {
        if (toApply.Count == 0) return;
        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var d in toApply)
        {
            var rule = existing.TryGetValue(Key(d.BandId, d.Category), out var known)
                ? await _tool.BandCategoryRules.SingleAsync(r => r.Id == known.Id)
                : _tool.BandCategoryRules.Add(new BandCategoryRule { LayerId = layerId, BandId = d.BandId, Category = d.Category }).Entity;
            rule.MarginFloorPct = d.ProposedFloorPct;
            rule.TargetMarginPct = d.ProposedTargetPct;
            rule.UpdatedUtc = now;
            rule.UpdatedBy = by ?? Source;
            rule.AgentDecisionId = d.Id;
            _tool.AuditLog.Add(new AuditLogEntry
            {
                LayerId = layerId, TimestampUtc = now, UserName = by ?? Source, Category = AgentModeStore.AuditCategory,
                Action = $"Floor/target {d.Action} (decision #{d.Id}{(how is null ? "" : ", " + how)})", EntityType = nameof(BandCategoryRule),
                EntityId = $"L{layerId}/B{d.BandId}/{d.Category}",
                OldValue = $"floor {d.CurrentFloorPct:0.##}%, target {d.CurrentTargetPct:0.##}%",
                NewValue = $"floor {d.ProposedFloorPct:0.##}%, target {d.ProposedTargetPct:0.##}%",
            });
        }
        try
        {
            await _tool.SaveChangesAsync(CancellationToken.None);
            foreach (var d in toApply) d.AppliedUtc = now;
        }
        catch (DbUpdateException ex)
        {
            _tool.ChangeTracker.Clear();
            foreach (var d in toApply)
            {
                d.Status = DecisionStatus.Held;
                d.Limit = "apply-failed";
                d.LimitDetail = Truncate(ex.GetBaseException().Message, 500);
            }
        }
        await _db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task WriteTimeSheetAsync(AgentPass pass, AlphaScorecard card, IReadOnlyList<AgentDecision> decisions, TimeSpan elapsed)
    {
        var type = await _db.TimeSheetTaskTypes.SingleAsync(t => t.Id == AgentDbContext.TaskTypes.WeeklyFloorTargetReview);
        var changed = decisions.Count(d => d.Status == DecisionStatus.Applied && d.Action != "keep");
        _db.TimeSheetEntries.Add(new TimeSheetEntry
        {
            Date = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime),
            UserEmail = _options.TimeSheetOwnerEmail,
            TaskTypeId = type.Id,
            What = $"{card.Layer.LayerName}: {decisions.Count} band x category rows reviewed; {changed} changed, " +
                   $"{pass.Held} held, {pass.Rejected} rejected, {pass.Pending} pending",
            MinutesBefore = type.MinutesBefore,
            MinutesAfter = Math.Round((decimal)elapsed.TotalMinutes, 2),
            Proof = decisions.Count == 0 ? $"ai.AgentPass #{pass.Id}" : $"ai.AgentPass #{pass.Id}; ai.AgentDecision #{decisions.Min(d => d.Id)}-#{decisions.Max(d => d.Id)}",
            Comment = "Written by the pricing agent's pass; minutes after = the pass's wall clock (held items' review not yet added).",
        });
        await _db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Net applied change per band × category since <paramref name="since"/>: the step limit counts
    /// from the value a week ago, so two small moves in one week can't add up past it.</summary>
    private async Task<Dictionary<string, (decimal Floor, decimal Target)>> AppliedChangesSinceAsync(int layerId, DateTime since, CancellationToken ct)
    {
        var applied = await _db.AgentDecisions.AsNoTracking()
            .Where(d => d.LayerId == layerId && d.Status == DecisionStatus.Applied && d.Action != "keep" && d.AppliedUtc >= since)
            .ToListAsync(ct);
        return applied.GroupBy(d => Key(d.BandId, d.Category), StringComparer.OrdinalIgnoreCase).ToDictionary(
            g => g.Key,
            g => (g.Sum(d => d.ProposedFloorPct - d.CurrentFloorPct), g.Sum(d => (d.ProposedTargetPct ?? 0) - (d.CurrentTargetPct ?? 0))),
            StringComparer.OrdinalIgnoreCase);
    }

    internal string BuildPrompt(AlphaScorecard card, IReadOnlyList<ScorecardRow> rows, AutonomyMode mode,
        IReadOnlyDictionary<string, (decimal Floor, decimal Target)> recent)
    {
        var l = _options.Limits;
        var layer = card.Layer.LayerId;
        var holdouts = l.Holdouts.TryGetValue(layer.ToString(CultureInfo.InvariantCulture), out var h) && h.Count > 0 ? string.Join(", ", h) : "none";
        var sb = new StringBuilder();
        sb.AppendLine($"Weekly floor and target review: layer {layer} ({card.Layer.LayerName}), {mode} mode.");
        sb.AppendLine();
        sb.AppendLine($"Return exactly one decision for each of the {rows.Count} scorecard rows with units > 0, identified by its band_id and its category exactly as written. Rows without sales need no decision. Answer with layer = {layer}.");
        sb.AppendLine("The floor and target columns are today's values for each row (category overrides included).");
        sb.AppendLine("Limits code enforces this week, per row (anything outside them is held for a person or rejected, never applied):");
        sb.AppendLine($"- floor and target move at most {l.MaxStepPpPerWeek:0.##} pt from their value 7 days ago;");
        sb.AppendLine($"- the floor is at least {l.HardMinFloorFor(layer):0.##}% on this layer; the target is never below the floor;");
        sb.AppendLine($"- rows marked thin, and the holdout categories ({holdouts}), stay as they are: answer keep.");
        if (recent.Count > 0)
            sb.AppendLine("- already moved in the last 7 days (counts against the step): " +
                string.Join("; ", recent.Select(r => $"{r.Key}: floor {r.Value.Floor:+0.##;-0.##} pt, target {r.Value.Target:+0.##;-0.##} pt")));
        sb.AppendLine();
        sb.Append(card.ToPromptTable());
        return sb.ToString();
    }

    private static ScorecardRow WithOverride(ScorecardRow row, IReadOnlyDictionary<string, BandCategoryRule> overrides) =>
        row.BandId is { } id && overrides.TryGetValue(Key(id, row.Category), out var o)
            ? row with { FloorPct = o.MarginFloorPct ?? row.FloorPct, TargetPct = o.TargetMarginPct ?? row.TargetPct }
            : row;

    private static AgentDecision NewDecision(AgentPass pass, int bandId, string category, decimal floor, decimal? target,
        AgentCallResult call, IReadOnlyList<long> queryIds) => new()
    {
        PassId = pass.Id, LayerId = pass.LayerId, BandId = bandId, Category = category.Trim(), Source = Source,
        CurrentFloorPct = floor, ProposedFloorPct = floor, CurrentTargetPct = target, ProposedTargetPct = target,
        AiCallLogId = call.CallLogIds.Count > 0 ? call.CallLogIds[^1] : null, CreatedUtc = pass.StartedUtc,
        EvidenceRefs = Evidence(bandId, category, Array.Empty<string>(), queryIds),
    };

    private static void Fill(AgentDecision d, AlphaDecisionDto p, IReadOnlyList<long> queryIds, string action, decimal floor,
        decimal target, string status, string? limit, string? detail)
    {
        d.Action = action;
        d.ProposedFloorPct = floor;
        d.ProposedTargetPct = target;
        d.Confidence = p.Confidence;
        d.Reason = Truncate(p.Reason, 2000) ?? "";
        d.Status = status;
        d.Limit = limit;
        d.LimitDetail = Truncate(detail, 500);
        d.EvidenceRefs = Evidence(d.BandId, d.Category, p.EvidenceRefs ?? new List<string>(), queryIds);
    }

    private static string Evidence(int bandId, string category, IReadOnlyList<string> modelRefs, IReadOnlyList<long> queryIds) =>
        JsonSerializer.Serialize(new { row = $"{bandId}/{category.Trim()}", model = modelRefs, queries = queryIds }, Json);

    private static string Key(int bandId, string category) => $"{bandId}/{category.Trim()}";
    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    private sealed record AlphaAnswer(int Layer, List<AlphaDecisionDto> Decisions, string Summary);

    private sealed record AlphaDecisionDto(int BandId, string Category, string Action, decimal NewFloorPct, decimal NewTargetPct,
        decimal Confidence, string Reason, List<string> EvidenceRefs);
}
