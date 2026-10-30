namespace PricingTool.Agent;

/// <summary>Who made an AI call. Stored as text in <c>ai.AiCallLog.Agent</c>.</summary>
public enum AgentRole
{
    /// <summary>Alpha, the pricing agent (floors, targets, campaign plans).</summary>
    Alpha,
    /// <summary>Nova, the campaign agent.</summary>
    Nova,
    /// <summary>The run checker: pass / hold / fix on flagged engine rows.</summary>
    RunChecker,
    /// <summary>The Agent tab chat.</summary>
    Chat,
}

/// <summary>
/// Settings for the agents' Claude calls (section "Agent"). The calls go through the team's Claude
/// subscription by running Claude Code headless (<c>claude -p</c>), so there is no API key here.
/// </summary>
public class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>The kill switch. Off: no AI call is made and every caller gets
    /// <see cref="ClaudeCode.AgentCallStatus.Disabled"/>. Off unless configured.</summary>
    public bool Enabled { get; set; }

    /// <summary>The Claude Code executable: a name on PATH or a full path.</summary>
    public string ClaudeExecutable { get; set; } = "claude";

    /// <summary>
    /// Working folder for the <c>claude</c> process. Must be outside the repo, so Claude Code finds no
    /// CLAUDE.md there or above it. Empty = %LOCALAPPDATA%\PricingToolAgent\claude-runtime.
    /// </summary>
    public string RuntimeDirectory { get; set; } = "";

    /// <summary>Wall-clock limit per attempt; the process tree is killed when it passes.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>Passed as <c>--max-budget-usd</c> on every attempt (API-equivalent dollars).</summary>
    public decimal MaxBudgetUsdPerCall { get; set; } = 2m;

    /// <summary>Model per role (<c>--model</c>): Alpha and Nova reason, the checker runs on many rows. Full ids,
    /// not aliases: on Claude Code 2.1.205 "opus" ran claude-opus-4-8 and "sonnet" claude-sonnet-5 (2026-10-30).</summary>
    public string AlphaModel { get; set; } = "claude-opus-5-5";
    public string NovaModel { get; set; } = "claude-opus-5-5";
    public string RunCheckerModel { get; set; } = "claude-sonnet-5-5";
    public string ChatModel { get; set; } = "claude-opus-5-5";

    public string ModelFor(AgentRole role) => role switch
    {
        AgentRole.Alpha => AlphaModel,
        AgentRole.Nova => NovaModel,
        AgentRole.RunChecker => RunCheckerModel,
        AgentRole.Chat => ChatModel,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    /// <summary>The PricingTool.AgentTools executable that serves the data tools over MCP. Relative paths are
    /// resolved against the running app's folder. Empty = PricingTool.AgentTools.exe next to the app.</summary>
    public string ToolsExecutable { get; set; } = "";

    public string ResolveToolsExecutable() =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(ToolsExecutable) ? "PricingTool.AgentTools.exe" : ToolsExecutable,
            AppContext.BaseDirectory);

    /// <summary>Where test runs (<c>--test</c>: demo, backtest, injected tests) keep their records. Empty = the tool's
    /// database name + "Test" (PricingToolHackathonTest). See <see cref="AgentServiceCollectionExtensions.TestConnectionString"/>.</summary>
    public string TestDatabase { get; set; } = "";

    /// <summary>The daily cycle on the laptop (prompt 05).</summary>
    public CycleOptions Cycle { get; set; } = new();

    /// <summary>At most this many flagged rows go to the run checker's one call per run (highest impact first).</summary>
    public int RunCheckerModelCap { get; set; } = 150;

    /// <summary>The scorecard Alpha judges (see <see cref="Alpha.AlphaInputBuilder"/>).</summary>
    public ScorecardOptions Scorecard { get; set; } = new();

    /// <summary>Guardrails of the agents' own SQL (see <see cref="Tools.QueryDataService"/>).</summary>
    public QueryDataOptions QueryData { get; set; } = new();

    /// <summary>The limits code enforces on every floor/target decision (see <see cref="Alpha.SafetyLimits"/>).</summary>
    public SafetyLimitOptions Limits { get; set; } = new();

    /// <summary>The owner's email for the organisers' time sheet (column B). Kept in user-secrets, never in a
    /// tracked file; empty leaves the column blank.</summary>
    public string TimeSheetOwnerEmail { get; set; } = "";

    /// <summary>Alpha's weekly pass: one call per layer, with tools, so it gets more time and budget.</summary>
    public int AlphaPassTimeoutSeconds { get; set; } = 900;
    public decimal AlphaPassMaxBudgetUsd { get; set; } = 5m;

    public string ResolveRuntimeDirectory() =>
        string.IsNullOrWhiteSpace(RuntimeDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PricingToolAgent", "claude-runtime")
            : RuntimeDirectory;
}

public class ScorecardOptions
{
    /// <summary>Length of the window, in days.</summary>
    public int WindowDays { get; set; } = 30;

    /// <summary>The window ends this many days before today: late cancellations still remove lines from
    /// the newest two weeks.</summary>
    public int LagDays { get; set; } = 14;

    /// <summary>A band × category with fewer normal-day units than this in the window is thin data (the
    /// agent must keep its floor).</summary>
    public int ThinDataUnits { get; set; } = 30;

    /// <summary>A line's margin counts as below the floor when it is more than this many points under it
    /// (rounding to a charm price lands a little either side).</summary>
    public decimal FloorTolerancePp { get; set; } = 0.1m;

    /// <summary>A line counts as at the floor when its margin is within the tolerance below and this many
    /// points above the floor.</summary>
    public decimal AtFloorBandPp { get; set; } = 1.0m;
}

public class CycleOptions
{
    private static string Local(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PricingToolAgent", name);

    /// <summary>The layers the cycle runs, in order (Gjirafa50 first: GjirafaMall adopts its prices).</summary>
    public List<int> Layers { get; set; } = new() { 4, 1 };

    /// <summary>The day of the week Alpha's floor/target pass runs.</summary>
    public DayOfWeek AlphaDay { get; set; } = DayOfWeek.Monday;

    /// <summary>Local time ("HH:mm") from which a running engine host starts the new day's cycle. A host that
    /// starts later in the day (at log-on) runs the cycle straight away.</summary>
    public string DailyStartLocal { get; set; } = "07:00";

    /// <summary>A failed step of today's cycle is tried again after this many minutes (three attempts a day at most).</summary>
    public int RetryFailedAfterMinutes { get; set; } = 60;

    /// <summary>Where the checked exports go for the team's push (outside the repo).</summary>
    public string ExportDirectory { get; set; } = "";
    public string BackupDirectory { get; set; } = "";
    public string TimeSheetDirectory { get; set; } = "";
    public int KeepBackups { get; set; } = 7;

    /// <summary>Local engine runs whose proposals are kept per layer (imported prod runs are never touched).</summary>
    public int KeepRunsPerLayer { get; set; } = 2;
    public decimal SizeWarningGb { get; set; } = 8m;

    /// <summary>The newest days of sales lines are re-pulled every day: late cancellations still remove lines.</summary>
    public int SalesLateWindowDays { get; set; } = 14;

    /// <summary>Products and the discount snapshot are reloaded when older than this (they change slowly; a full
    /// reload is the heaviest replica read).</summary>
    public int SnapshotRefreshDays { get; set; } = 7;

    /// <summary>Who pushes the exports and owns the measurement, for the Approval record.</summary>
    public string PushedBy { get; set; } = "pricing tool (team push)";

    public TimeOnly DailyStart() => TimeOnly.ParseExact(DailyStartLocal, "HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    public string ExportDir() => string.IsNullOrWhiteSpace(ExportDirectory) ? Local("exports") : ExportDirectory;
    public string BackupDir() => string.IsNullOrWhiteSpace(BackupDirectory) ? Local("backups") : BackupDirectory;
    public string TimeSheetDir() => string.IsNullOrWhiteSpace(TimeSheetDirectory) ? Local("timesheet") : TimeSheetDirectory;
}

public class SafetyLimitOptions
{
    /// <summary>How far a floor or a target may move from its value 7 days earlier, in percentage points.</summary>
    public decimal MaxStepPpPerWeek { get; set; } = 1.0m;

    /// <summary>The lowest floor allowed on any layer without its own entry in <see cref="HardMinFloorPct"/>.
    /// Above zero, so a floor always keeps the price above cost.</summary>
    public decimal DefaultHardMinFloorPct { get; set; } = 5m;

    /// <summary>Per-layer hard minimum floor, keyed by layer id ("1", "4").</summary>
    public Dictionary<string, decimal> HardMinFloorPct { get; set; } = new();

    /// <summary>Holdout categories per layer id: never changed by an agent; the control group.</summary>
    public Dictionary<string, List<string>> Holdouts { get; set; } = new();

    /// <summary>Anything outside these is malformed and rejected.</summary>
    public decimal MaxFloorPct { get; set; } = 60m;
    public decimal MaxTargetPct { get; set; } = 80m;

    public decimal HardMinFloorFor(int layerId) =>
        Math.Max(0.5m, HardMinFloorPct.TryGetValue(layerId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var v)
            ? v : DefaultHardMinFloorPct);

    public bool IsHoldout(int layerId, string category) =>
        Holdouts.TryGetValue(layerId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var list)
        && list.Any(c => string.Equals(c.Trim(), category.Trim(), StringComparison.OrdinalIgnoreCase));
}

public class QueryDataOptions
{
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxRows { get; set; } = 500;

    /// <summary>The database user every agent query runs as: no login, SELECT on schema agent only.</summary>
    public string ReaderUser { get; set; } = "agent_reader";
}
