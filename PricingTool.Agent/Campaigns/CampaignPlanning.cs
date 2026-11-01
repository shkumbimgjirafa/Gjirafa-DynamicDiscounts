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

    /// <summary>Restores what a campaign publish changed (its discounts end before they start): the number of rows restored.</summary>
    Task<int> RollbackAsync(long batchId, CancellationToken ct);
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
    private readonly IUsageMeter? _meter;

    /// <summary>The tables a plan's candidates come from: the latest run's prices, stock, booked sales and past campaigns.</summary>
    private static readonly string[] SourceTables =
        { "PricingTool.ProposedPrices", "PricingTool.DailySnapshots", "hk.ProductForProposal", "agent.SalesLines", "agent.CampaignHistory" };

    public CampaignPlanning(AgentDbContext db, PricingToolDbContext tool, ICampaignCandidateSource candidates, AgentDataTools tools,
        BandConfigProvider bands, ClaudeCodeClient client, IKillSwitch killSwitch, IOptions<AgentOptions> options, ILogger<CampaignPlanning> logger,
        TimeProvider? clock = null, ICampaignPublisher? publisher = null, IUsageMeter? meter = null)
    {
        _meter = meter;
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

    /// <summary>Test seam: the backtest (a replay of the campaign history needs SQL).</summary>
    internal Func<int, CancellationToken, Task<CampaignBacktest?>>? BacktestSource { get; set; }

    public async Task<PlanningResult> PlanAsync(long briefId, bool publish, CancellationToken ct = default)
    {
        var stop = await _killSwitch.GetAsync(ct);
        if (stop.Stopped) return new PlanningResult(briefId, "Skipped", Array.Empty<CampaignPlan>(), $"Kill switch: {stop.Reason}");
        var brief = await _db.CampaignBriefs.SingleAsync(b => b.Id == briefId, ct);
        if (brief.Status is not (BriefStatus.Proposed or BriefStatus.Planned))
            return new PlanningResult(briefId, "Refused", Array.Empty<CampaignPlan>(), $"Brief #{briefId} is {brief.Status}: only a proposed brief is planned.");
        // One run of the core task for the organisers' meter (a test brief records none): Alpha's and Nova's calls count toward it.
        using var usage = UsageRun.Start(brief.IsTest ? null : _meter, CoreTasks.CampaignPlanning, new { brief = briefId }, _clock);
        var layer = await _tool.Layers.AsNoTracking().SingleAsync(l => l.Id == brief.LayerId, ct);
        var calendar = brief.CalendarId is { } cid ? await _db.CampaignCalendar.AsNoTracking().SingleOrDefaultAsync(c => c.Id == cid, ct) : null;
        var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
        var depth = await (DepthSource ?? ((l, c) => _tools.GetDepthEffectAsync(l, c)))(layer.Id, ct);
        var type = CampaignMath.TypeOf(calendar?.Name ?? brief.Name);
        var eventLift = depth.EventLift(type);
        var liftWindows = depth.EventLiftWindows.TryGetValue(type, out var lw) ? lw : 0;
        var liftNote = eventLift is { } el ? $"event lift x{el:0.00} (median of {liftWindows} past {type} window{(liftWindows == 1 ? "" : "s")})" : $"no past {type} window: no event lift assumed (x1.00, conservative)";
        var liftCaveat = eventLift is not null && liftWindows == 1
            ? $" The {type} event lift rests on one past window, so the estimate's uncertainty is wider than any range shown." : "";
        // Calibrated like for like: a plan with an event lift for its type uses the replays that had one too.
        var backtest = await (BacktestSource ?? (async (l, c) => await _tools.ReplayCampaignsAsync(l, c)))(layer.Id, ct);
        var calibration = backtest?.For(eventLift is not null);
        var bands = (await _bands.GetBandsAsync(layer.Id, ct)).ToDictionary(b => b.BandId);
        // People's rules from the chat that cover the campaign's days: they only tighten (exclusions, a higher minimum margin).
        var rules = (await _db.ChatRules.AsNoTracking().Where(r => r.RemovedUtc == null && (r.LayerId == null || r.LayerId == layer.Id)).ToListAsync(ct))
            .Where(r => r.ActiveOn(brief.StartDate, brief.EndDate)).ToList();
        var ruleMinMargin = rules.Where(r => r.Kind == ChatRuleKinds.MinCampaignMargin)
            .Select(r => decimal.TryParse(r.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0m).DefaultIfEmpty(0m).Max();
        // A person's terms for this campaign (asked for in the chat): no depth cap, every SKU in stock, and a margin goal that is
        // the campaign's effective margin, spent down to, not a floor to clear.
        var depthCap = calendar?.DepthCapLiftedBy is null ? _options.Campaigns.MaxDepthPct : CampaignLimitOptions.LiftedMaxDepthPct;
        var everyInStock = calendar?.EverySkuInStock == true;
        var (candidates, leftOutBefore, followers) = await CandidatesAsync(brief, layer.Id, layer.Brand == "GjirafaMall", today, ct, rules, everyInStock);
        usage.Source = new UsageSource(CoreTasks.ShopSql, CoreTasks.ShopCopy, SourceTables, candidates.Count + followers.Count);
        // The model sets the brief's minimum margin; code never lets it under the floor. Stored on the brief, so the cost watcher
        // judges the live campaign on the same value.
        var marginFloor = _options.Campaigns.MinMarginFloorPct;
        if (brief.MinMarginPct < marginFloor)
        {
            await SayAsync(brief, null, 1, "code", $"Minimum margin {brief.MinMarginPct:0.#}% raised to {marginFloor:0.#}%: no campaign SKU goes under the code floor.", null, ct);
            brief.MinMarginPct = marginFloor;
            await _db.SaveChangesAsync(ct);
        }
        if (ruleMinMargin > brief.MinMarginPct)
            await SayAsync(brief, null, 1, "code", $"A person's rule raises the minimum margin from {brief.MinMarginPct:0.#}% to {ruleMinMargin:0.#}% for these days.", null, ct);
        // A goal a person set for the campaign (on the calendar, or by asking for it in the chat) is binding: Nova can't change
        // it, and a plan short of it waits for a person.
        var personGoal = calendar?.GoalSetBy is { } setBy && calendar.GoalType == brief.GoalType ? setBy : null;
        var toTarget = personGoal is not null && brief.GoalType == "margin";
        var strategies = toTarget ? new[] { "spend-to-margin" } : CampaignPlanner.Strategies;
        var maxSkus = everyInStock ? int.MaxValue : _options.Campaigns.MaxSkusPerCampaign;
        var days = brief.EndDate.DayNumber - brief.StartDate.DayNumber + 1;
        // SKUs the plan doesn't price still sell during the campaign: GjirafaMall SKUs that take Gjirafa50's price, and on an
        // all-shop campaign the holdout categories. Their expected gross profit at today's price with the event lift counts
        // toward a gross-profit goal, which (like the past windows Nova anchors on and the backtest replays) covers them.
        var followersGp = Math.Round(followers.Sum(c => c.UnitsPerDay * days * (eventLift ?? 1m) * (c.CurrentPrice - c.Cost)) / (1m + layer.VatRatePct / 100m), 0);

        var plans = new List<CampaignPlan>();
        for (var round = 1; round <= MaxRounds; round++)
        {
            var spec = new PlanSpec(brief.CalendarId ?? brief.Id, brief.EndDate.DayNumber - brief.StartDate.DayNumber + 1, brief.GoalType, brief.GoalValue,
                Math.Max(toTarget ? brief.GoalValue : brief.MinMarginPct, ruleMinMargin), Math.Min(brief.MaxDepthPct, depthCap), layer.VatRatePct, eventLift ?? 1m,
                ExcludedBrands(brief.Exclusions)) { GrossProfitOutsidePlan = followersGp, Calibration = calibration, InSampleCalibration = backtest?.InSampleCalibration,
                SkuMarginFloorPct = marginFloor, SpendToTarget = toTarget };
            var results = strategies.Select(s => CampaignPlanner.Plan(s, spec, candidates, depth, bands, maxSkus, leftOutBefore)).ToList();
            var basis = $"{candidates.Count:N0} SKUs in scope with stock and normal-day sales ({today.AddDays(-42):yyyy-MM-dd}..{today.AddDays(-15):yyyy-MM-dd}, campaign days left out); " +
                        $"{liftNote}; extra depth: {(depth.B is { } b ? $"b = {b} within-campaign fit" : "no fit, no extra units from depth")}; estimated." +
                        (followers.Count > 0 ? $" Not priced by the plan: {followers.Count:N0} SKUs that take Gjirafa50's price or sit in a holdout category, expected gross profit " +
                                               $"{followersGp:N0} at today's prices with the event lift; it counts toward the gross-profit goal (the whole shop, as the past windows), estimated." : "") +
                        (calibration is { } cal ? $" Backtest calibration x{cal.Factor:0.00}: {cal.Basis}."
                            : $" No leave-one-out calibration for this case (fewer than {CampaignReplay.MinCampaigns} past campaigns of a type " +
                              $"{(eventLift is not null ? "with an event lift from another window" : "without one")})." +
                              (backtest?.InSampleCalibration is { } ins ? $" In-sample only: x{ins.Factor:0.00} (not leave-one-out; for comparison)." : "")) +
                        liftCaveat +
                        (toTarget ? $" The goal is the campaign's effective margin, set by a person ({personGoal}): spent down to {spec.MinMarginPct:0.#}% (gross profit over net " +
                                    "revenue), the most units per euro first; one plan." : "") +
                        (calendar?.DepthCapLiftedBy is { } lifted ? $" Depth cap lifted by {lifted}: up to {depthCap:0}% where the margins allow; deeper than past campaigns " +
                                                                     $"went ({depth.MaxDepthPct:0} points over normal), the estimate counts no extra units (conservative)." : "") +
                        (everyInStock ? " Every SKU in stock, with no cap on how many; one with no sales in the six weeks is priced at the campaign's margin." : "");
            await SayAsync(brief, null, round, "code", $"Round {round}: {results.Count} plan{(results.Count == 1 ? "" : "s")} computed. {basis}\n{Describe(results, brief)}", null, ct);

            var choice = await AskAlphaAsync(brief, results, basis, round, ct);
            if (choice is null)
                return await FailAsync(brief, plans, "The pricing agent's answer was missing or invalid: nothing planned.", ct);
            var chosen = results.Single(r => r.Strategy == choice.Value.Strategy);
            var plan = await StoreAsync(brief, round, chosen, results, choice.Value.Explanation, basis, ct);
            plans.Add(plan);
            await SayAsync(brief, plan.Id, round, FloorTargetAgent.Source, choice.Value.Explanation, choice.Value.CallLogId, ct);

            var review = await AskNovaAsync(brief, plan, round, personGoal, ct);
            if (review is null)
                return await FailAsync(brief, plans, "The campaign agent's review was missing or invalid: the plan waits for a person.", ct);
            await SayAsync(brief, plan.Id, round, CampaignAgent.Source, review.Value.Message, review.Value.CallLogId, ct);
            if (review.Value.Accept && personGoal is not null && !plan.Feasible)
            {
                plan.Status = PlanStatus.Held;
                await SayAsync(brief, plan.Id, round, "code",
                    $"Accepted by the campaign agent, but it falls short of the goal a person set ({brief.GoalType} {GoalText(brief)}; {personGoal}). " +
                    $"{plan.Feasibility} The plan waits for a person: approve it as it is (approve-plan {plan.Id}) or set another goal (campaign-goal).", null, ct);
                break;
            }
            if (review.Value.Accept)
            {
                plan.Status = PlanStatus.Accepted;
                if (personGoal is not null && plan.GoalVerdict == GoalVerdicts.Stretch)
                {
                    // Met only on the median estimate: it goes ahead, and the person who set the goal sees that it is a stretch.
                    plan.WaitsForPerson = true;
                    await SayAsync(brief, plan.Id, round, "code",
                        $"The goal a person set ({personGoal}) is met only on the median estimate. {plan.Feasibility} " +
                        $"The plan goes ahead and waits in the inbox for that person to approve it as a stretch (approve-plan {plan.Id}) or set another goal (campaign-goal).", null, ct);
                }
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
            if (adjusted.MinMargin < marginFloor)
            {
                await SayAsync(brief, plan.Id, round, "code", $"Minimum margin {adjusted.MinMargin:0.#}% raised to {marginFloor:0.#}%: no campaign SKU goes under the code floor.", null, ct);
                adjusted = (adjusted.Goal, adjusted.MaxDepth, marginFloor);
            }
            if (adjusted.MaxDepth > depthCap)
                await SayAsync(brief, plan.Id, round, "code", $"Max depth {adjusted.MaxDepth:0.#}% capped at {depthCap:0.#}%.", null, ct);
            if (personGoal is not null && adjusted.Goal != brief.GoalValue)
            {
                await SayAsync(brief, plan.Id, round, "code",
                    $"The goal was set by a person ({personGoal}): it stays {brief.GoalType} {GoalText(brief)}. The campaign agent's {adjusted.Goal:N0} is kept as its view in this thread.", null, ct);
                adjusted = (brief.GoalValue, adjusted.MaxDepth, adjusted.MinMargin);
            }
            (brief.GoalValue, brief.MinMarginPct, brief.MaxDepthPct) = (adjusted.Goal, adjusted.MinMargin, Math.Min(adjusted.MaxDepth, depthCap));
            await _db.SaveChangesAsync(ct);
        }

        var final = plans[^1];
        brief.Status = BriefStatus.Planned;
        if (final.Status == PlanStatus.Accepted && publish && !brief.IsTest)
            await PublishPlanAsync(brief, final, ct);
        await _db.SaveChangesAsync(CancellationToken.None);
        _logger.LogInformation("Campaign brief {Brief}: {Rounds} round(s), plan #{Plan} {Status}", brief.Id, plans.Count, final.Id, final.Status);
        (usage.Units, usage.Ok) = (1, true);   // one unit = one campaign planned (accepted, or held for a person to decide)
        return new PlanningResult(brief.Id, final.Status, plans, null);
    }

    /// <summary>
    /// A person cancels a campaign before it starts (on the Campaigns tab, or by asking Nova): it stays on record as cancelled,
    /// its open briefs are closed, its plans stopped, and a publish still on the shelf is rolled back, so nothing of it goes
    /// live. A campaign that has started isn't cancelled here: its discounts end on their date. Returns what happened, in words.
    /// </summary>
    public async Task<(bool Cancelled, string Message)> CancelAsync(long calendarId, string by, CancellationToken ct = default)
    {
        var entry = await _db.CampaignCalendar.SingleOrDefaultAsync(c => c.Id == calendarId, ct);
        var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
        if (entry is null) return (false, $"Campaign #{calendarId} isn't on the calendar.");
        if (entry.Status == CampaignStatus.Cancelled) return (false, $"{entry.Name} (#{entry.Id}) is already cancelled.");
        if (entry.StartDate <= today)
            return (false, $"{entry.Name} (#{entry.Id}) started on {entry.StartDate:ddd dd MMM}: a campaign that has started isn't cancelled; its discounts end on its last day.");
        var briefs = await _db.CampaignBriefs.Where(b => b.CalendarId == calendarId && !b.IsTest).ToListAsync(ct);
        var briefIds = briefs.Select(b => b.Id).ToList();
        var plans = await _db.CampaignPlans.Where(p => briefIds.Contains(p.BriefId) && !p.IsTest).ToListAsync(ct);
        var rolledBack = new List<string>();
        foreach (var p in plans.Where(p => p.Status == PlanStatus.Published && p.PublishBatchId != null))
        {
            var batch = await _db.PublishBatches.AsNoTracking().SingleOrDefaultAsync(b => b.Id == p.PublishBatchId, ct);
            if (batch is null || batch.RolledBackUtc is not null) continue;
            if (_publisher is null) return (false, $"{entry.Name}: publish batch #{batch.Id} is on the shelf and no publisher is configured here to roll it back; nothing changed.");
            var restored = await _publisher.RollbackAsync(batch.Id, ct);
            rolledBack.Add($"batch #{batch.Id} rolled back ({restored:N0} rows restored)");
        }
        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var p in plans.Where(p => p.Status is PlanStatus.Proposed or PlanStatus.Accepted or PlanStatus.Held or PlanStatus.Published))
            (p.Status, p.WaitsForPerson) = (PlanStatus.Stopped, false);
        foreach (var b in briefs.Where(b => b.Status is BriefStatus.Proposed or BriefStatus.Held))
            (b.Status, b.Limit, b.LimitDetail) = (BriefStatus.Rejected, "cancelled", $"The campaign was cancelled by {by}.");
        entry.Status = CampaignStatus.Cancelled;
        var note = $"Cancelled by {by} on {_clock.GetLocalNow():yyyy-MM-dd}.";
        entry.Notes = string.IsNullOrWhiteSpace(entry.Notes) ? note : $"{entry.Notes} {note}";
        if (entry.Notes.Length > 2000) entry.Notes = entry.Notes[^2000..];
        var message = $"{entry.Name} (#{entry.Id}, {entry.StartDate:dd MMM}–{entry.EndDate:dd MMM}) cancelled by {by}: " +
                      (rolledBack.Count > 0 ? string.Join("; ", rolledBack) + "." : "nothing of it was published.");
        if (briefs.OrderByDescending(b => b.Id).FirstOrDefault() is { } latest)
            _db.CampaignMessages.Add(new CampaignMessage { BriefId = latest.Id, Round = 0, From = by.Length > 40 ? by[..40] : by, Text = message, CreatedUtc = now });
        await _db.SaveChangesAsync(CancellationToken.None);
        _logger.LogInformation("Campaign {Calendar} cancelled by {By}", calendarId, by);
        return (true, message);
    }

    /// <summary>
    /// A person approves a plan waiting for them: a held one (e.g. short of the goal they set) is accepted and, when asked,
    /// published; one that went ahead as a stretch is marked approved.
    /// </summary>
    public async Task<PlanningResult> ApproveAsync(long planId, string by, bool publish, CancellationToken ct = default)
    {
        var stop = await _killSwitch.GetAsync(ct);
        if (stop.Stopped) return new PlanningResult(0, "Skipped", Array.Empty<CampaignPlan>(), $"Kill switch: {stop.Reason}");
        var plan = await _db.CampaignPlans.SingleAsync(p => p.Id == planId, ct);
        var brief = await _db.CampaignBriefs.SingleAsync(b => b.Id == plan.BriefId, ct);
        if (plan.Status == PlanStatus.Accepted && plan.PublishBatchId is null && !plan.WaitsForPerson && plan.ReviewedBy is not null && publish && !brief.IsTest)
        {
            // Approved earlier where no publisher runs (the web app): publish it now.
            await PublishPlanAsync(brief, plan, ct);
            await _db.SaveChangesAsync(CancellationToken.None);
            return new PlanningResult(brief.Id, plan.Status, new[] { plan }, null);
        }
        if (plan.Status != PlanStatus.Held && !plan.WaitsForPerson)
            return new PlanningResult(brief.Id, "Refused", new[] { plan }, $"Plan #{planId} is {plan.Status} and waits for nobody: nothing to approve.");
        (plan.ReviewedBy, plan.ReviewedUtc, plan.WaitsForPerson) = (by, _clock.GetUtcNow().UtcDateTime, false);
        if (plan.Status != PlanStatus.Held)
        {
            await SayAsync(brief, plan.Id, plan.Round, by, $"Approved by a person as a stretch. {plan.Feasibility}", null, ct);
            await _db.SaveChangesAsync(CancellationToken.None);
            return new PlanningResult(brief.Id, plan.Status, new[] { plan }, null);
        }
        plan.Status = PlanStatus.Accepted;
        await SayAsync(brief, plan.Id, plan.Round, by, $"Approved by a person as it is. {plan.Feasibility}", null, ct);
        if (publish && !brief.IsTest) await PublishPlanAsync(brief, plan, ct);
        await _db.SaveChangesAsync(CancellationToken.None);
        return new PlanningResult(brief.Id, plan.Status, new[] { plan }, null);
    }

    private async Task PublishPlanAsync(CampaignBrief brief, CampaignPlan plan, CancellationToken ct)
    {
        if (_publisher is null)
        {
            await SayAsync(brief, plan.Id, plan.Round, "code", "Accepted; no publisher configured here, so nothing was published.", null, ct);
            return;
        }
        if (plan.DiscountedSkus == 0)
        {
            await SayAsync(brief, plan.Id, plan.Round, "code",
                "The plan discounts no SKU, so there is no campaign to publish: nothing goes out and the SKUs keep today's prices.", null, ct);
            return;
        }
        var lines = await _db.CampaignPlanLines.AsNoTracking().Where(l => l.PlanId == plan.Id).ToListAsync(ct);
        var (start, end) = Window(brief, brief.LayerId);
        // A re-plan (from the Campaigns tab) replaces the campaign's earlier publish: that one is rolled back first, so the new plan
        // starts from the shelf without the campaign and a Stop of the new one leaves no campaign at all.
        if (brief.CalendarId is { } calendarId)
        {
            var briefIds = _db.CampaignBriefs.Where(b => b.CalendarId == calendarId).Select(b => b.Id);
            var earlier = await _db.CampaignPlans.Where(p => briefIds.Contains(p.BriefId) && p.Id != plan.Id && p.Status == PlanStatus.Published && p.PublishBatchId != null)
                .ToListAsync(ct);
            foreach (var old in earlier)
            {
                var batch = await _db.PublishBatches.AsNoTracking().SingleOrDefaultAsync(b => b.Id == old.PublishBatchId, ct);
                if (batch is not null && batch.RolledBackUtc is null)
                {
                    var restored = await _publisher.RollbackAsync(batch.Id, ct);
                    await SayAsync(brief, plan.Id, plan.Round, "code", $"Plan #{old.Id}'s publish (batch #{batch.Id}) rolled back ({restored:N0} rows restored): plan #{plan.Id} replaces it.", null, ct);
                }
                old.Status = PlanStatus.Superseded;
            }
        }
        var (batchId, status) = await _publisher.PublishAsync(brief.LayerId, brief.CalendarId ?? brief.Id, start, end,
            lines.Select(l => (l.Sku, l.CampaignPrice, l.RegularPrice)).ToList(), ct);
        plan.PublishBatchId = batchId;
        if (status == "Published") plan.Status = PlanStatus.Published;
        await SayAsync(brief, plan.Id, plan.Round, "code",
            $"Publish batch #{batchId}: {status}. {lines.Count:N0} time-boxed ai-campaign discounts, live {start:yyyy-MM-dd HH:mm}..{end:yyyy-MM-dd HH:mm} UTC.", null, ct);
    }

    /// <summary>A goal as people read it: a margin with its decimal, euros and units in thousands.</summary>
    private static string GoalText(CampaignBrief b) => b.GoalType == "margin" ? $"{b.GoalValue:0.0}%" : b.GoalValue.ToString("N0");

    /// <summary>The campaign's local days as a UTC window (whole days, inclusive of the last).</summary>
    private static (DateTime Start, DateTime End) Window(CampaignBrief b, int layerId)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Central European Standard Time" : "Europe/Belgrade");
        return (TimeZoneInfo.ConvertTimeToUtc(b.StartDate.ToDateTime(TimeOnly.MinValue), tz),
                TimeZoneInfo.ConvertTimeToUtc(b.EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue), tz).AddSeconds(-1));
    }

    private async Task<(List<CampaignCandidate>, Dictionary<string, int>, List<CampaignCandidate> Followers)> CandidatesAsync(CampaignBrief brief, int layerId,
        bool isMall, DateOnly today, CancellationToken ct, IReadOnlyList<ChatRule>? rules = null, bool everyInStock = false)
    {
        var all = await _candidates.LoadAsync(layerId, today.AddDays(-42), today.AddDays(-14), ct, everyInStock);
        var holdouts = _options.Limits.Holdouts.TryGetValue(layerId.ToString(CultureInfo.InvariantCulture), out var h) ? h : new List<string>();
        var leftOut = new Dictionary<string, int>();
        void Out(string why) => leftOut[why] = leftOut.GetValueOrDefault(why) + 1;
        var keep = new List<CampaignCandidate>();
        var followers = new List<CampaignCandidate>();
        foreach (var c in all)
        {
            if (brief.ScopeKind == "category" && !string.Equals(c.Category, brief.ScopeValue, StringComparison.OrdinalIgnoreCase)) continue;
            if (brief.ScopeKind == "brand" && !string.Equals(c.Brand, brief.ScopeValue, StringComparison.OrdinalIgnoreCase)) continue;
            if (rules?.FirstOrDefault(r => r.Kind == ChatRuleKinds.ExcludeBrand && string.Equals(r.Value, c.Brand, StringComparison.OrdinalIgnoreCase)
                                           || r.Kind == ChatRuleKinds.ExcludeCategory && string.Equals(r.Value, c.Category, StringComparison.OrdinalIgnoreCase)) is { } kept)
            {
                Out($"kept out by a person's rule #{kept.Id} ({kept.Kind} {kept.Value})");
                continue;
            }
            if (c.DeadStock) { Out("dead stock or clearance lock (its own lane)"); continue; }
            if (isMall && c.FollowsGj50)
            {
                Out("GjirafaMall price follows Gjirafa50 (planned on Gjirafa50)");
                if (c.UnitsPerDay > 0) followers.Add(c);
                continue;
            }
            if (holdouts.Contains(c.Category, StringComparer.OrdinalIgnoreCase))
            {
                Out("holdout category");
                if (c.UnitsPerDay > 0 && brief.ScopeKind == "all") followers.Add(c);   // not discounted, but sells during the campaign
                continue;
            }
            if (c.LocalStock <= 0 && c.SupplierStock <= 0) { Out("no stock"); continue; }
            if (c.UnitsPerDay <= 0 && !everyInStock) { Out("no normal-day sales to plan on"); continue; }
            keep.Add(c);
        }
        return (keep, leftOut, followers);
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
            scope {brief.ScopeKind} {brief.ScopeValue}, goal {brief.GoalType} {GoalText(brief)}, minimum margin {brief.MinMarginPct:0.#}%, maximum depth {brief.MaxDepthPct:0.#}%.
            Its rationale: {brief.Rationale}

            Code computed {results.Count} plan{(results.Count == 1 ? "" : "s")} ({basis}):
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
        // Only a plan computed this round can be chosen: a person's margin goal computes one (spend-to-margin), not all four.
        return a is null || !results.Any(r => r.Strategy == a.Strategy) ? null
            : (a.Strategy, a.Explanation, call.CallLogIds.Count > 0 ? call.CallLogIds[^1] : null);
    }

    private async Task<(bool Accept, decimal? Goal, decimal? MaxDepth, decimal? MinMargin, string Message, long? CallLogId)?> AskNovaAsync(CampaignBrief brief,
        CampaignPlan plan, int round, string? personGoal, CancellationToken ct)
    {
        var prompt = $"""
            Review of the pricing agent's plan for your brief #{brief.Id} "{brief.Name}" (round {round} of at most {MaxRounds}).
            Your brief: {brief.ScopeKind} {brief.ScopeValue}, {brief.StartDate:yyyy-MM-dd}..{brief.EndDate:yyyy-MM-dd}, goal {brief.GoalType} {GoalText(brief)},
            minimum margin {brief.MinMarginPct:0.#}%, maximum depth {brief.MaxDepthPct:0.#}%.
            The plan ({plan.Strategy}): {plan.DiscountedSkus:N0} of {plan.SkusInScope:N0} SKUs discounted at {plan.AverageDepthPct:0.#}% on average,
            expected units {plan.ExpectedUnits:N0}, net revenue {plan.ExpectedNetRevenue:N0}, gross profit {plan.ExpectedGrossProfit:N0}
            (without any discount {plan.GrossProfitWithoutDiscounts:N0}), margin {plan.ExpectedMarginPct:0.0}%. {plan.Feasibility}
            {plan.HoldoutSkus:N0} SKUs are the campaign holdout. Basis: {plan.Basis}
            The pricing agent says: {plan.Explanation}

            Accept the plan, or adjust your brief (a new goal value, maximum depth or minimum margin; null keeps a value) and say why.
            {(personGoal is null ? "" : $"The goal was set by a person ({personGoal}): it is binding, so don't change it; adjust the depth or the minimum margin, or accept. A plan short of it waits for that person.")}
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
            AverageDepthPct = t.AverageDepthPct, Feasible = r.Feasible, Feasibility = r.Feasibility, GoalVerdict = r.GoalVerdict, LeftOut = JsonSerializer.Serialize(r.LeftOut, Json),
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
