using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PricingTool.Agent.Alpha;
using PricingTool.Agent.Chat;
using PricingTool.Agent.ClaudeCode;
using PricingTool.Agent.Storage;
using PricingTool.Data;

namespace PricingTool.Agent.Operations;

/// <summary>What a review of the cost watcher's holds settled: held SKUs whose shelf price is back at or above the floor, team
/// discounts ended so the floor price applies, and what is still open.</summary>
public sealed record HoldReview(int Resolved, int Fixed, int StillHeld, IReadOnlyList<string> Lines);

/// <summary>Settles the cost watcher's holds that code can settle (the publisher implements it): a hold whose price is back at or
/// above the floor is closed, and one a cheaper team discount keeps under the floor is fixed by republishing the current run's
/// price with that discount ended, unless a chat rule protects it (the user's decision, 31 Oct).</summary>
public interface IHoldResolver
{
    Task<HoldReview> ReviewAsync(CancellationToken ct = default);

    /// <summary>Alpha confirmed these held cost jumps are real: the current run's price is republished like any other cost rise.</summary>
    Task<HoldReview> ConfirmRealRiseAsync(IReadOnlyCollection<long> decisionIds, CancellationToken ct = default);
}

/// <summary>Alpha's answer on the open problems, as the schema asks for it.</summary>
public sealed record ProblemAnswer(string Post, IReadOnlyList<ProblemItemAnswer> Items);

public sealed record ProblemItemAnswer(string Key, string Action, string Owner, string Note);

/// <summary>What one problem pass did: what code settled, what Alpha reported, and its post in the chat.</summary>
public sealed record ProblemPassResult(string Status, HoldReview? Review, int Reported, int Confirmed, long? PostId, string? Error);

/// <summary>
/// The agents' problem pass (the user's decision, 31 Oct: only what the agents can't settle reaches a person, with who can fix it).
/// Code first settles what it can (<see cref="IHoldResolver"/>). What is still open goes to Alpha in one call: for each item it says
/// who can fix it (one of <see cref="AgentOptions.Owners"/>) and what it found; it may confirm that a held cost jump is real, which
/// code then republishes like any other rise. Nothing else it says changes a price. Its summary is posted in the chat, and each
/// reported hold keeps its owner and note for the Chat tab's "waiting for you".
/// </summary>
public sealed class ProblemDesk
{
    public const string ConfirmRealRise = "confirm-real-rise", Report = "report";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly AgentDbContext _db;
    private readonly PricingToolDbContext _tool;
    private readonly ExceptionsInbox _inbox;
    private readonly ClaudeCodeClient _client;
    private readonly AgentChat _chat;
    private readonly AgentOptions _options;
    private readonly ILogger<ProblemDesk> _logger;
    private readonly IHoldResolver? _resolver;
    private readonly TimeProvider _clock;

    public ProblemDesk(AgentDbContext db, PricingToolDbContext tool, ExceptionsInbox inbox, ClaudeCodeClient client, AgentChat chat,
        IOptions<AgentOptions> options, ILogger<ProblemDesk> logger, IHoldResolver? resolver = null, TimeProvider? clock = null)
    {
        _db = db;
        _tool = tool;
        _inbox = inbox;
        _client = client;
        _chat = chat;
        _options = options.Value;
        _logger = logger;
        _resolver = resolver;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<ProblemPassResult> RunAsync(CancellationToken ct = default)
    {
        var review = _resolver is null ? null : await _resolver.ReviewAsync(ct);
        var inbox = await _inbox.LoadAsync(ct);
        var now = _clock.GetUtcNow().UtcDateTime;

        // The holds still open, once per SKU; an item already reported in the last day isn't asked about again.
        var holds = inbox.CostHoldSkus.Where(h => h.Latest.ReportedUtc is null || h.Latest.ReportedUtc < now.AddDays(-1)).Take(80).ToList();
        var others = OtherItems(inbox);
        if (holds.Count == 0 && others.Count == 0)
        {
            if (review is { Fixed: > 0 } or { Resolved: > 0 })
            {
                var post = await _chat.PostAsync("Alpha", Settled(review) + " Nothing else needs a person.", null, ct);
                return new ProblemPassResult("Succeeded", review, 0, 0, post.Id, null);
            }
            return new ProblemPassResult("Skipped", review, 0, 0, null, "nothing open");
        }

        var layers = await _tool.Layers.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.DisplayName, ct);
        var input = new
        {
            settledByCodeJustNow = review is null ? null : new { review.Resolved, endedTeamDealsAndRepublished = review.Fixed, review.Lines },
            owners = _options.Owners,
            heldSkus = holds.Select(h => new
            {
                key = $"hold:{h.Latest.Id}",
                layer = layers.GetValueOrDefault(h.LayerId, $"layer {h.LayerId}"),
                sku = h.Sku,
                kind = h.Latest.Limit,
                timesHeld = h.Ids.Count,
                whatTheWatcherFound = h.Latest.Reason,
                facts = Parse(h.Latest.EvidenceRefs),
            }),
            otherItems = others,
        };
        var call = await _client.CallAsync(new AgentCallRequest(AgentRole.Alpha, "problems",
            "These are the problems still open after code settled what it could. Investigate them with your data tools where it helps, " +
            "then say for each who can fix it and what you found, and write the post for the chat.\n\n" + JsonSerializer.Serialize(input, Json),
            Path.Combine(FloorTargetAgent.PromptsDirectory, "problem-desk-rules.md"))
        {
            AppendSystemPromptFiles = new[] { Path.Combine(FloorTargetAgent.PromptsDirectory, "agent-data-dictionary.md") },
            JsonSchema = await File.ReadAllTextAsync(Path.Combine(FloorTargetAgent.PromptsDirectory, "problem-desk.schema.json"), ct),
            UseDataTools = true,
            TimeoutSeconds = 600,
            MaxBudgetUsd = 2m,
        }, ct);
        var callLogId = call.CallLogIds.Count > 0 ? call.CallLogIds[^1] : (long?)null;
        var answer = call.Succeeded ? call.Deserialize<ProblemAnswer>(Json) : null;
        if (answer is null || string.IsNullOrWhiteSpace(answer.Post))
        {
            var why = call.Succeeded ? "the answer had no post" : $"{call.Status}: {call.Error}";
            _logger.LogWarning("Problem pass: Alpha's answer not used ({Why}).", why);
            if (review is { Fixed: > 0 } or { Resolved: > 0 })
                await _chat.PostAsync("Alpha", Settled(review) + $" I couldn't look at the rest this time ({why}).", callLogId, ct);
            return new ProblemPassResult(call.Status == AgentCallStatus.Disabled ? "Skipped" : "Failed", review, 0, 0, null, why);
        }

        // Code enforces what the answer may do: an owner from the list, a note, and "confirm" only on a held data-error jump.
        var byKey = holds.ToDictionary(h => $"hold:{h.Latest.Id}");
        var confirm = new List<long>();
        var reported = 0;
        foreach (var item in answer.Items ?? Array.Empty<ProblemItemAnswer>())
        {
            if (!byKey.TryGetValue(item.Key ?? "", out var hold)) continue;
            if (item.Action == ConfirmRealRise && hold.Latest.Limit is "data-error-cost-jump" or "data-error")
            {
                confirm.AddRange(hold.Ids);
                continue;
            }
            var owner = _options.Owners.TryGetValue(item.Owner ?? "", out var role) ? role : _options.Owners.Values.FirstOrDefault(v => v == item.Owner) ?? "not named";
            var decisions = await _db.AgentDecisions.Where(d => hold.Ids.Contains(d.Id)).ToListAsync(ct);
            foreach (var d in decisions)
                (d.ReportedTo, d.ReportNote, d.ReportedUtc) = (Truncate(owner, 200), Truncate(item.Note, 1000), now);
            reported++;
        }
        await _db.SaveChangesAsync(CancellationToken.None);
        var confirmed = confirm.Count > 0 && _resolver is not null ? await _resolver.ConfirmRealRiseAsync(confirm, ct) : null;

        var text = (review is { Fixed: > 0 } or { Resolved: > 0 } ? Settled(review) + "\n\n" : "") + answer.Post.Trim()
                   + (confirmed is { Fixed: > 0 } ? $"\n\n(Code republished {confirmed.Fixed} SKU{(confirmed.Fixed == 1 ? "" : "s")} I confirmed as real cost rises.)" : "");
        var message = await _chat.PostAsync("Alpha", text, callLogId, ct);
        return new ProblemPassResult("Succeeded", review, reported, confirmed?.Fixed ?? 0, message.Id, null);
    }

    /// <summary>The line code writes for what it settled itself.</summary>
    private static string Settled(HoldReview r) =>
        (r.Fixed > 0 ? $"I ended the team discounts that kept {r.Fixed} SKU{(r.Fixed == 1 ? "" : "s")} under the floor after a cost rise, and their floor prices are on the shelf. " : "") +
        (r.Resolved > 0 ? $"{r.Resolved} held SKU{(r.Resolved == 1 ? " is" : "s are")} back at or above the floor on their own (the deal ended or the price moved), so I closed {(r.Resolved == 1 ? "it" : "them")}." : "");

    /// <summary>The inbox's other open items, summarised for Alpha (it reports them; code doesn't act on its answer for them).</summary>
    private static List<object> OtherItems(InboxSnapshot inbox)
    {
        var items = new List<object>();
        foreach (var d in inbox.Decisions)
            items.Add(new { key = $"decision:{d.Decision.Id}", kind = "floor or target change outside the limits", layer = inbox.LayerName(d.Decision.LayerId),
                d.BandName, d.Decision.Category, d.Decision.Limit, d.Decision.LimitDetail, d.Decision.Reason });
        foreach (var p in inbox.CampaignPlans)
            items.Add(new { key = $"plan:{p.Plan.Id}", kind = "campaign plan waiting for a person", layer = inbox.LayerName(p.Plan.LayerId), p.Campaign, p.GoalType, p.GoalValue,
                p.Plan.Status, p.Plan.GoalVerdict, p.Plan.Feasibility });
        foreach (var b in inbox.HeldBriefs)
            items.Add(new { key = $"brief:{b.Id}", kind = "campaign brief over a cap", layer = inbox.LayerName(b.LayerId), b.Name, b.LimitDetail });
        foreach (var c in inbox.BlockedRuns)
            items.Add(new { key = $"run:{c.RunId}", kind = "prices the run checker blocked (held; the rest of the run went out)", layer = inbox.LayerName(c.LayerId), c.Notes, c.Error });
        foreach (var p in inbox.Publishes.Where(p => p.NeedsAPerson))
            items.Add(new { key = $"batch:{p.Batch.Id}", kind = "publish stopped, failed or rolled back", layer = inbox.LayerName(p.Batch.LayerId), p.Batch.Status, p.Batch.Note,
                exceptions = p.Exceptions.Select(e => $"{e.Kind} x{e.Count}") });
        foreach (var s in inbox.FailedSteps)
            items.Add(new { key = $"step:{s.Id}", kind = "daily-cycle step failed", s.Step, s.Error });
        foreach (var p in inbox.Pauses)
            items.Add(new { key = $"pause:{p.Id}", kind = "band x category paused after a rollback", layer = inbox.LayerName(p.LayerId), p.BandId, p.Category, p.Reason });
        return items;
    }

    private static JsonElement? Parse(string? json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? null : JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
