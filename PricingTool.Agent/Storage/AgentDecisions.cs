namespace PricingTool.Agent.Storage;

/// <summary>One weekly pass of an agent over a layer (<c>ai.AgentPass</c>). A failed pass changes nothing.</summary>
public class AgentPass
{
    public long Id { get; set; }
    public int LayerId { get; set; }

    /// <summary>Alpha / CostWatcher / Nova.</summary>
    public string Source { get; set; } = "";

    /// <summary>The layer's autonomy mode when the pass ran (Off / ApproveFirst / Autonomous).</summary>
    public string Mode { get; set; } = "";

    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }

    /// <summary>Running / Succeeded / Failed / Skipped.</summary>
    public string Status { get; set; } = "";

    public string? Error { get; set; }

    /// <summary>Ties the pass to its AI call and the queries it made (AiCallLog / AgentQueryLog).</summary>
    public string? CallTag { get; set; }
    public long? AiCallLogId { get; set; }

    public int Applied { get; set; }
    public int Held { get; set; }
    public int Rejected { get; set; }
    public int Pending { get; set; }

    /// <summary>The model's one-paragraph summary of the pass.</summary>
    public string? Summary { get; set; }

    /// <summary>Demo and backtest passes don't count as real work (no time-sheet row).</summary>
    public bool IsTest { get; set; }
}

/// <summary>
/// One decision on one band × category (<c>ai.AgentDecision</c>): what the agent proposed, what code let
/// through, and why. Everything shown on the Agent tab's feed and in the exceptions inbox comes from here.
/// </summary>
public class AgentDecision
{
    public long Id { get; set; }
    public long PassId { get; set; }
    public int LayerId { get; set; }
    public int BandId { get; set; }
    public string Category { get; set; } = "";

    /// <summary>Alpha / CostWatcher / Nova.</summary>
    public string Source { get; set; } = "";

    /// <summary>raise / lower / keep, as finally stored (a forced keep reads keep).</summary>
    public string Action { get; set; } = "";

    public decimal CurrentFloorPct { get; set; }
    public decimal ProposedFloorPct { get; set; }
    public decimal? CurrentTargetPct { get; set; }
    public decimal? ProposedTargetPct { get; set; }
    public decimal? Confidence { get; set; }
    public string Reason { get; set; } = "";

    /// <summary>JSON: the scorecard row, the model's own references and the pass's AgentQueryLog ids.</summary>
    public string EvidenceRefs { get; set; } = "";

    /// <summary>Applied / Held / Rejected / Pending.</summary>
    public string Status { get; set; } = "";

    /// <summary>The limit that held, rejected or overrode it (e.g. "max-step", "holdout", "thin-data").</summary>
    public string? Limit { get; set; }
    public string? LimitDetail { get; set; }

    public long? AiCallLogId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? AppliedUtc { get; set; }

    /// <summary>The person who approved or rejected a held or pending decision in the exceptions inbox, and when.</summary>
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedUtc { get; set; }
}

/// <summary>A kind of task the solution does for people, with its measured time before (<c>ai.TimeSheetTaskType</c>).
/// <see cref="MinutesBefore"/> stays NULL until the user has measured it; such rows aren't exported.</summary>
public class TimeSheetTaskType
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? MinutesBefore { get; set; }
}

/// <summary>One real task the solution completed, written by the solution itself (<c>ai.TimeSheetEntry</c>):
/// the organisers' hours-saved sheet, columns A–H.</summary>
public class TimeSheetEntry
{
    public long Id { get; set; }
    public DateOnly Date { get; set; }

    /// <summary>The owner from config (user-secrets); empty when not configured.</summary>
    public string UserEmail { get; set; } = "";

    public int TaskTypeId { get; set; }
    public string What { get; set; } = "";
    public int? MinutesBefore { get; set; }

    /// <summary>Measured: the task's wall clock plus any person's review of its held items.</summary>
    public decimal MinutesAfter { get; set; }

    public string Proof { get; set; } = "";
    public string? Comment { get; set; }
    public string Source { get; set; } = "app";
}
