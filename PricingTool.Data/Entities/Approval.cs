namespace PricingTool.Data.Entities;

/// <summary>
/// One approval EVENT — the set of proposals a Manager approved together (Approve / Approve-all). Because
/// the team applies approved prices to the shop out-of-band, the approval is what "went live", and this is
/// the tool's record of it (until a direct NOP write-back exists). Anchors the dashboard's per-approval
/// "did the bet pay off?" view: realized profit (SR <c>SUM(Margin)</c>) for its SKUs in the 7/30/90 days
/// BEFORE vs AFTER <see cref="ApprovedUtc"/>, plus how many prices went up vs down.
/// </summary>
public class Approval
{
    public long Id { get; set; }

    /// <summary>The layer (Brand + Country) this approval belongs to.</summary>
    public int LayerId { get; set; }

    /// <summary>When the batch was approved — D0, the go-live anchor for the before/after windows.</summary>
    public DateTime ApprovedUtc { get; set; }

    /// <summary>The run whose proposals were approved.</summary>
    public long SourceRunId { get; set; }

    public string ApprovedBy { get; set; } = "";

    public int SkuCount { get; set; }

    /// <summary>SKUs whose approved price is HIGHER than the price before it (a price rise).</summary>
    public int IncreaseCount { get; set; }

    /// <summary>SKUs whose approved price is LOWER than the price before it (a price cut).</summary>
    public int DecreaseCount { get; set; }

    // ---- Realized profit (SR SUM(Margin)) for this approval's SKUs, per window, before vs after
    // ApprovedUtc. Null until the profit refresh first runs; the "after" figures grow as calendar time
    // passes D0 and are final once the window has fully elapsed.
    public decimal? Profit7Before { get; set; }
    public decimal? Profit7After { get; set; }
    public decimal? Profit30Before { get; set; }
    public decimal? Profit30After { get; set; }
    public decimal? Profit90Before { get; set; }
    public decimal? Profit90After { get; set; }

    /// <summary>Last time the before/after profit figures were refreshed from the source. Null until first.</summary>
    public DateTime? ProfitRefreshedUtc { get; set; }

    public DateTime CreatedUtc { get; set; }

    public List<ApprovalItem> Items { get; set; } = new();
}
