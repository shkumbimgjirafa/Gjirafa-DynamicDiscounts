namespace PricingTool.Agent.Operations;

/// <summary>
/// The cycle's size check acts, not only warns (LocalDB stops every write at 10 GB, the push export with them): at
/// <see cref="CycleOptions.SizeTrimGb"/> the retention deletes run at once with <see cref="CycleOptions.TrimDays"/>-day windows
/// and one local run per layer, before the engine runs; at <see cref="CycleOptions.SizeStopSandboxGb"/> after that, the sandbox
/// steps (mirror and publish) wait for the day. The engine runs, checks, the push export, the ledger and the backup still go.
/// </summary>
public static class SizeValve
{
    /// <summary>What the valve reads and does: the used size, and the trim (with its day window), which says what it deleted.</summary>
    public sealed record Probe(Func<CancellationToken, Task<decimal>> UsedGb, Func<int, CancellationToken, Task<string>> Trim);

    private const string PauseText = "the sandbox mirror and publish wait for the day (the engine runs, checks, the push export, the ledger and the backup still go)";

    /// <summary>The size-check step: the size, and the trim when it is due.</summary>
    public static async Task<(string Status, string Detail)> CheckAsync(CycleOptions c, Probe probe, CancellationToken ct = default)
    {
        var gb = await probe.UsedGb(ct);
        var head = $"{gb:0.00} GB used of LocalDB's 10 GB";
        if (gb < c.SizeTrimGb)
            return gb >= c.SizeWarningGb ? (StepStatus.Warning, $"{head}: over the {c.SizeWarningGb} GB warning line") : (StepStatus.Succeeded, head);
        var deleted = await probe.Trim(c.TrimDays, ct);
        var after = await probe.UsedGb(ct);
        var detail = $"{head}: at or over {c.SizeTrimGb} GB, so the retention ran at once with {c.TrimDays}-day windows and one local run per layer " +
                     $"(deleted {deleted}); freed {Math.Max(0m, gb - after):0.00} GB, now {after:0.00} GB";
        if (after >= c.SizeStopSandboxGb) detail += $"; still at or over {c.SizeStopSandboxGb} GB: {PauseText}";
        return (StepStatus.Warning, detail);
    }

    /// <summary>Why the sandbox steps wait today, or null.</summary>
    public static string? SandboxPause(CycleOptions c, decimal usedGb) =>
        usedGb >= c.SizeStopSandboxGb ? $"{usedGb:0.00} GB used, at or over the {c.SizeStopSandboxGb} GB line: {PauseText}" : null;
}
