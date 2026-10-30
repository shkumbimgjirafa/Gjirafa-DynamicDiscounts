namespace PricingTool.Core.Options;

/// <summary>
/// Engine-wide configuration bound from appsettings ("PricingEngine" section).
/// Band-specific knobs (guardrails, weights, rounding) live in the PriceBands tables instead.
/// </summary>
public class PricingEngineOptions
{
    public const string SectionName = "PricingEngine";

    /// <summary>Kosovo standard VAT rate, percent. Shelf prices are VAT-inclusive, costs and net revenue are VAT-exclusive.</summary>
    public decimal VatRatePct { get; set; } = 18m;

    /// <summary>Algorithm 4: projected sellout within this many days counts as stockout risk.</summary>
    public int StockoutRiskDays { get; set; } = 14;

    /// <summary>Algorithm 2: products launched within this many days vote for 0% discount.</summary>
    public int NewProductProtectionDays { get; set; } = 90;

    /// <summary>Proposals with |change| above this percent require explicit confirmation in the UI.</summary>
    public decimal ChangeConfirmationThresholdPct { get; set; } = 20m;

    /// <summary>
    /// Minimum meaningful storefront discount, in percent OFF the shelf (Old) price. A proposed price
    /// that would show a strike-through discount smaller than this — a token markdown not worth
    /// advertising — is held at the full Old price so no discount is shown at all ("if the discount off
    /// the old price is under X%, don't discount at all"). Measured on the FINAL, post-rounding price:
    /// (OldPrice − proposed) / OldPrice. Set to 0 to disable. Deliberate below-shelf prices are exempt:
    /// dead stock (clearing markdowns) and pinned/held prices (clearance lock, dead-stock holds).
    /// </summary>
    public decimal MinDisplayedDiscountPct { get; set; } = 3m;

    /// <summary>
    /// Below this price (in the layer's currency), the EUR .99 charm grid tightens from €1 steps to a
    /// finer 10-cent .x9 grid (…0.99, 1.09, 1.19) so rounding a cheap item can't swing its margin
    /// between, say, 0.99 and 1.99. Applies to the EndsIn99 convention; ties go to the lower price.
    /// </summary>
    public decimal LowPriceRoundingThreshold { get; set; } = 5m;

    /// <summary>
    /// Weber fraction for the <c>Gj50Charm</c> rounding convention (default 0.02 = 2%). Controls both
    /// the charm grid granularity (step stays within this fraction of the price, so the snap is
    /// magnitude-proportional with no hard cliffs) and the round-up tolerance (a price is only rounded
    /// up to the higher charm point if the move is within this fraction). Lower = tighter to the
    /// engine's optimal price; higher = coarser, more aggressive charm endings. See docs/Webers-Law-Pricing.md.
    /// </summary>
    public decimal CharmRelativePrecision { get; set; } = 0.02m;

    // NOTE: the dead-stock tunnel floor moved to the per-band PriceBand.DeadStockFloorCostPct
    // (default 100 — at cost, never below); the old engine-wide DeadStockFloorCostFraction option
    // was dead config (nothing read it) and has been removed.

    /// <summary>
    /// Cross-dock (supplier-fulfilled) progressive markdown — the initial discount, in percent of the
    /// anchor, applied to a non-selling supplier-only SKU on the first step. Deliberately softer than
    /// dead-stock's 10% start: a cross-dock markdown is demand discovery on inventory we don't hold, not
    /// loss-recovery on stock we own. The markdown never runs below the band margin floor.
    /// </summary>
    public decimal CrossDockStartDiscountPct { get; set; } = 5m;

    /// <summary>Cross-dock markdown: extra percentage points of discount added per step interval (softer than dead-stock's 5pp/2wk via a longer interval).</summary>
    public decimal CrossDockStepPct { get; set; } = 5m;

    /// <summary>
    /// Cross-dock markdown: snapshot rows (≈ calendar days at the 24h cadence) per markdown step. Longer
    /// than dead-stock's 14 so a non-selling supplier SKU is discounted more gently over time.
    /// </summary>
    public int CrossDockStepIntervalDays { get; set; } = 21;

    /// <summary>
    /// Grace (in days) added to the outcome measurement window before an un-graded (Pending) outcome
    /// is force-resolved to a terminal Neutral. A Pending outcome can only stay un-graded once its
    /// anchoring pre/post snapshots have aged out of retention, so this is set with SnapshotRetentionDays
    /// in mind: expiry fires at (outcome window = 7d) + this grace, which should be ≥ SnapshotRetentionDays
    /// so we never force-Neutral an outcome whose snapshot data still exists. Default 28 → expiry at
    /// 7 + 28 = 35 days = the default retention window.
    /// </summary>
    public int OutcomeMaturityGraceDays { get; set; } = 28;

    /// <summary>
    /// Master switch for snapshot/volume-table retention (Step 2). Defaults OFF — the delete pass ships
    /// dormant and is turned on per environment only after the DailyLayerKpi baseline pin and the
    /// pending-outcome expiry are live. Flip back to false to stop all retention deletes instantly
    /// (nothing is ever dropped; retention is a runtime delete, not a schema change).
    /// </summary>
    public bool RetentionEnabled { get; set; } = false;

    /// <summary>DailySnapshots older than this many days are deleted when retention is enabled.</summary>
    public int SnapshotRetentionDays { get; set; } = 35;

    /// <summary>
    /// When true, retention also prunes the co-largest run tables: ProposedPrices (cascading their
    /// AlgorithmVotes) and the PricingRuns older than <see cref="RunRetentionDays"/>. Defaults OFF so
    /// Phase 1 prunes snapshots only (lowest blast radius); flip on for Phase 2.
    /// </summary>
    public bool RetentionIncludesProposals { get; set; } = false;

    /// <summary>
    /// PricingRuns (and their cascaded proposals/votes) older than this are deleted when
    /// <see cref="RetentionIncludesProposals"/> is on. Kept separate from the snapshot window so run
    /// audit history can be retained longer than raw snapshots if desired.
    /// </summary>
    public int RunRetentionDays { get; set; } = 35;

    /// <summary>
    /// Acceptance band for the per-band margin-target guardrail, in percentage points. A band whose
    /// blended margin sits within this many pp of its target is treated as "on goal" — the guardrail
    /// leaves it alone. Because the margin floor and psychological rounding mean a band can rarely land
    /// on an exact margin, a strict goal would just churn prices for no real gain; ±1pp is the default
    /// fair band. When the margin IS outside it, the guardrail aims at the target (centre of the band),
    /// leaving headroom for rounding drift on either side.
    /// </summary>
    public decimal MarginTargetTolerancePct { get; set; } = 1.0m;

    /// <summary>When true the source reader is replaced by the demo data generator (no source DB needed).</summary>
    public bool UseDemoData { get; set; }

    /// <summary>Fallback run time (UTC, "HH:mm") used to seed the schedule setting on first start.</summary>
    public string DefaultRunTimeUtc { get; set; } = "03:00";

    /// <summary>Fallback cadence in hours used to seed the schedule setting on first start (24 = daily).</summary>
    public int DefaultCadenceHours { get; set; } = 24;

    /// <summary>
    /// The two "slow lanes" — the fitted price-elasticity vote and the dead-stock progressive markdown —
    /// only form a NEW opinion every this many days, even though the pricing run itself is daily. Between
    /// ticks the run keeps re-emitting each slow lane's LAST decision (freeze-and-reuse), so their prices
    /// stay put while sell-through, the guardrails, margin-target and rounding keep daily control. The
    /// dead-stock pacer's engage/step is gated to this rhythm (see <c>DeadStockMarkdownPacer</c> /
    /// <c>SkuContext.SlowLanesFrozen</c>). Distinct from <see cref="DefaultCadenceHours"/> (the RUN cadence)
    /// and from <see cref="ElasticityFitCadenceDays"/> (how often the coefficient itself is re-fitted).
    /// </summary>
    public int SlowLaneCadenceDays { get; set; } = 15;

    /// <summary>
    /// How often the background <c>ElasticityFitWorker</c> re-fits each layer's per-SKU elasticity
    /// coefficients. The fit is decoupled from the daily pricing run, and the fitted coefficient is the
    /// elastic lane's actual "opinion", so refitting on this cadence is what keeps the elastic vote stable
    /// between ticks. Distinct from <see cref="DefaultCadenceHours"/> (run cadence) and from
    /// <c>ElasticityFitService.DefaultWindowDays</c> (the 365-day trailing window each fit reads).
    /// </summary>
    public int ElasticityFitCadenceDays { get; set; } = 15;

    /// <summary>Directory where the v1 CSV push integration writes approved-price export files.</summary>
    public string PushExportDirectory { get; set; } = "exports";

    /// <summary>
    /// Anchor for the dashboard's general weekly-profit trend: the chart shows realized profit
    /// (SUM Margin from SR_ProductsData) per ISO week from this Monday forward and grows a bar each week.
    /// Set to the Monday of the week profit tracking began — a single global start, per the "start
    /// tracking from this week" decision. A layer-wide, source-derived figure, independent of approvals.
    /// </summary>
    public DateTime WeeklyProfitTrendStartUtc { get; set; } = new(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
}
