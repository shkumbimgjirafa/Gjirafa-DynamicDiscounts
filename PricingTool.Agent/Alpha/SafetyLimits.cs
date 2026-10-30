namespace PricingTool.Agent.Alpha;

/// <summary>Decision statuses, as stored in ai.AgentDecision.</summary>
public static class DecisionStatus
{
    /// <summary>Within the limits and written (a keep is applied by changing nothing).</summary>
    public const string Applied = "Applied";
    /// <summary>Outside a limit a person may lift (the step size): waits in the exceptions inbox.</summary>
    public const string Held = "Held";
    /// <summary>Breaks a rule nobody can lift (range, floor/target order, hard minimum) or is malformed.</summary>
    public const string Rejected = "Rejected";
    /// <summary>Approve-first mode: within the limits, waiting for one click.</summary>
    public const string Pending = "Pending";
}

/// <summary>One proposed floor/target change with everything the limits need to judge it.</summary>
public sealed record LimitInput(
    int LayerId, string Category, bool ThinData, bool IsHoldout,
    decimal CurrentFloor, decimal CurrentTarget, decimal FloorWeekAgo, decimal TargetWeekAgo,
    string Action, decimal ProposedFloor, decimal ProposedTarget);

/// <summary>What code lets through: the status, the values to store and, when a limit acted, which one.</summary>
public sealed record LimitVerdict(string Status, string Action, decimal Floor, decimal Target, string? Limit, string? Detail);

/// <summary>
/// The limits on floor and target decisions. Code, not prompt: the model's answer is only a proposal, and
/// nothing it says can change these rules. Order matters: a holdout or thin row is kept whatever was
/// proposed; then malformed and impossible values are rejected; then the step size holds what is too big.
/// </summary>
public static class SafetyLimits
{
    private const decimal Epsilon = 0.0001m;

    public static LimitVerdict Evaluate(LimitInput d, SafetyLimitOptions o)
    {
        var asked = $"proposed {d.Action}: floor {d.ProposedFloor:0.##}%, target {d.ProposedTarget:0.##}%";
        var keep = new LimitVerdict(DecisionStatus.Applied, "keep", d.CurrentFloor, d.CurrentTarget, null, null);

        if (d.IsHoldout)
            return keep with { Limit = "holdout", Detail = $"Holdout category: never changed by an agent ({asked})." };
        if (d.ThinData)
            return keep with { Limit = "thin-data", Detail = $"Too few units to judge: kept by code ({asked})." };

        var action = d.Action.Trim().ToLowerInvariant();
        if (action is not ("raise" or "lower" or "keep"))
            return Reject("malformed", $"Unknown action '{d.Action}'.");
        if (action == "keep")
            return keep;

        if (d.ProposedFloor <= 0 || d.ProposedFloor > o.MaxFloorPct || d.ProposedTarget <= 0 || d.ProposedTarget > o.MaxTargetPct)
            return Reject("out-of-range", $"Values outside 0–{o.MaxFloorPct}% (floor) / 0–{o.MaxTargetPct}% (target): {asked}.");

        var dFloor = d.ProposedFloor - d.CurrentFloor;
        var dTarget = d.ProposedTarget - d.CurrentTarget;
        if (Math.Abs(dFloor) < Epsilon && Math.Abs(dTarget) < Epsilon)
            return keep;
        if ((action == "raise" && (dFloor < -Epsilon || dTarget < -Epsilon)) || (action == "lower" && (dFloor > Epsilon || dTarget > Epsilon)))
            return Reject("malformed", $"The action and the values disagree: {asked} from floor {d.CurrentFloor:0.##}%, target {d.CurrentTarget:0.##}%.");

        if (d.ProposedTarget < d.ProposedFloor - Epsilon)
            return Reject("target-below-floor", $"The target may never be below the floor: {asked}.");

        var hardMin = o.HardMinFloorFor(d.LayerId);
        if (d.ProposedFloor < hardMin - Epsilon)
            return Reject("hard-minimum", $"The floor may never go below {hardMin:0.##}% on this layer: {asked}.");

        var floorStep = Math.Abs(d.ProposedFloor - d.FloorWeekAgo);
        var targetStep = Math.Abs(d.ProposedTarget - d.TargetWeekAgo);
        if (floorStep > o.MaxStepPpPerWeek + Epsilon || targetStep > o.MaxStepPpPerWeek + Epsilon)
            return new LimitVerdict(DecisionStatus.Held, action, d.ProposedFloor, d.ProposedTarget, "max-step",
                $"Moves {Math.Max(floorStep, targetStep):0.##} pt against a 7-day limit of {o.MaxStepPpPerWeek:0.##} pt " +
                $"(floor {d.FloorWeekAgo:0.##}% and target {d.TargetWeekAgo:0.##}% a week ago): {asked}.");

        return new LimitVerdict(DecisionStatus.Applied, action, d.ProposedFloor, d.ProposedTarget, null, null);

        LimitVerdict Reject(string limit, string detail) =>
            new(DecisionStatus.Rejected, action, d.ProposedFloor, d.ProposedTarget, limit, detail);
    }
}
