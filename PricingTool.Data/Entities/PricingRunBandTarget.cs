namespace PricingTool.Data.Entities;

/// <summary>
/// Immutable snapshot of one price band's TARGET and FLOOR margins as they stood when a specific pricing
/// run executed — i.e. the band config that actually produced that run's proposals. Captured once per run
/// (one row per band) so historical views and the Target-impact analysis can read the targets a run aimed
/// for, instead of today's live <see cref="PriceBand"/> rows, which are admin-editable and drift over time
/// (e.g. a campaign raises a band's target, then reverts it).
///
/// Deliberately decoupled from <see cref="PricingRun"/> — <see cref="PricingRunId"/> is a plain indexed
/// column with no foreign key — so these rows survive the run-retention purge (runs/proposals are deleted
/// after a few days, but Approvals persist, so the band targets behind an approval must persist too). Join
/// back to a run/approval by <see cref="PricingRunId"/> / <c>Approval.SourceRunId</c>.
/// </summary>
public class PricingRunBandTarget
{
    public long Id { get; set; }

    /// <summary>The run whose band config this captures. Plain column (no FK) so it outlives the run.</summary>
    public long PricingRunId { get; set; }

    public int LayerId { get; set; }

    /// <summary>The source <see cref="PriceBand"/> Id at capture time (the band may later be edited or removed).</summary>
    public int BandId { get; set; }

    /// <summary>Band name as it stood at run time (self-describing so the snapshot needs no join to read).</summary>
    public string Name { get; set; } = "";

    public decimal MinPrice { get; set; }
    public decimal MaxPrice { get; set; }

    /// <summary>The band's blended margin GOAL at run time, in percent; null = the margin-target guardrail was off for this band.</summary>
    public decimal? TargetMarginPct { get; set; }

    /// <summary>The band's hard minimum margin at run time, in percent of the VAT-exclusive price.</summary>
    public decimal MarginFloorPct { get; set; }

    public DateTime CapturedUtc { get; set; }
}
