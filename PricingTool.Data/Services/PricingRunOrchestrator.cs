using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PricingTool.Core.Abstractions;
using PricingTool.Core.Algorithms;
using PricingTool.Core.Domain;
using PricingTool.Core.Options;
using PricingTool.Core.Services;
using PricingTool.Data.Entities;

namespace PricingTool.Data.Services;

/// <summary>
/// Executes one full pricing run: pull dataset → snapshot it → price every SKU through the
/// Core pipeline → write ProposedPrices + AlgorithmVotes, all wrapped in a PricingRuns record.
/// The engine only ever writes to the tool's own tables — never to live platform prices.
/// </summary>
public class PricingRunOrchestrator
{
    /// <summary>A Running run older than this is considered crashed and is failed-out (e.g. the
    /// process was killed mid-run). Also used by the UI so a stale orphan doesn't read as "in progress".</summary>
    public static readonly TimeSpan StaleRunCutoff = TimeSpan.FromHours(2);

    /// <summary>How many proposals to buffer before bulk-flushing them (and their votes) to SQL.</summary>
    private const int FlushChunkSize = 20_000;

    private readonly PricingToolDbContext _db;
    private readonly ISourceDataReader _reader;
    private readonly SnapshotService _snapshots;
    private readonly IBulkWriteService _bulk;
    private readonly BandConfigProvider _bandProvider;
    private readonly PriceCalculator _calculator;
    private readonly GuardrailService _guardrails;
    private readonly MarginTargetService _marginTarget;
    private readonly IEnumerable<IPricingAlgorithm> _algorithms;
    private readonly AuditService _audit;
    private readonly OutcomeEvaluationService _outcomes;
    private readonly DeadStockEpisodeService _deadStockEpisodes;
    private readonly PricingEngineOptions _options;
    private readonly ILogger<PricingRunOrchestrator> _logger;

    public PricingRunOrchestrator(
        PricingToolDbContext db,
        ISourceDataReader reader,
        SnapshotService snapshots,
        IBulkWriteService bulk,
        BandConfigProvider bandProvider,
        PriceCalculator calculator,
        GuardrailService guardrails,
        MarginTargetService marginTarget,
        IEnumerable<IPricingAlgorithm> algorithms,
        AuditService audit,
        OutcomeEvaluationService outcomes,
        DeadStockEpisodeService deadStockEpisodes,
        IOptions<PricingEngineOptions> options,
        ILogger<PricingRunOrchestrator> logger)
    {
        _db = db;
        _reader = reader;
        _snapshots = snapshots;
        _bulk = bulk;
        _bandProvider = bandProvider;
        _calculator = calculator;
        _guardrails = guardrails;
        _marginTarget = marginTarget;
        _algorithms = algorithms;
        _audit = audit;
        _outcomes = outcomes;
        _deadStockEpisodes = deadStockEpisodes;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Runs a full pricing cycle for one layer. Throws <see cref="RunAlreadyInProgressException"/> if a run
    /// is already in progress (runs are serialized globally — the bulk-write path is not concurrency-safe),
    /// and InvalidOperationException if the layer does not exist / is inactive.
    /// </summary>
    public async Task<PricingRun> ExecuteRunAsync(string triggeredBy, bool isOnDemand, int layerId, CancellationToken ct = default)
    {
        // A full-catalog run reads/writes hundreds of thousands of rows (snapshot history, zero-sale
        // streaks, outcome evaluation); the default 30s command timeout isn't enough. This DbContext
        // is scoped to the run, so the longer timeout never affects the web UI's queries.
        // (Guarded: SetCommandTimeout is relational-only — the in-memory test provider rejects it.)
        if (_db.Database.IsRelational())
            _db.Database.SetCommandTimeout(600);

        await FailStaleRunsAsync(ct);

        var layer = await _db.Layers.AsNoTracking().FirstOrDefaultAsync(l => l.Id == layerId && l.IsActive, ct)
            ?? throw new InvalidOperationException($"Layer {layerId} not found or is inactive.");

        // Discount optimization: the layer's trim table, loaded once per run — and only when the layer has
        // "Discount optimization" ticked on the Schedule page. Null means OFF wherever it is read (the
        // SkuContext), and so does a table whose every trim is zero.
        IReadOnlyList<DiscountTrimRange>? discountTrimRanges = null;
        if (layer.DiscountOptimizationEnabled)
        {
            var trimRanges = (await _db.DiscountTrimRules.AsNoTracking()
                    .Where(r => r.LayerId == layerId)
                    .OrderBy(r => r.SortOrder)
                    .ToListAsync(ct))
                .Select(r => r.ToRange())
                .ToList();
            if (trimRanges.Any(r => r.TrimPct > 0m)) discountTrimRanges = trimRanges;
        }

        // Slow-lane 15-day rhythm: the fitted-elasticity and dead-stock lanes only form a fresh opinion
        // every SlowLaneCadenceDays, even though the run is daily. On the daily runs in between they are
        // FROZEN — the dead-stock pacer re-emits its applied target and does NOT step, so those prices stay
        // put while sell-through, the guardrails, margin-target and rounding keep daily control. A
        // never-ticked layer (null) is due and engages on its first run. (The elastic coefficient is frozen
        // independently by the fit worker's own cadence; this gate covers the dead-stock lane.)
        var slowLaneDue = layer.LastSlowLaneRunUtc is not DateTime lastSlow
            || (DateTime.UtcNow.Date - lastSlow.Date).Days >= _options.SlowLaneCadenceDays;
        var slowLanesFrozen = !slowLaneDue;

        // Runs are serialized across ALL layers: BulkWriteService reseeds identity and deletes
        // snapshots at table scope, so two concurrent runs would corrupt each other.
        if (await _db.PricingRuns.AnyAsync(r => r.Status == RunStatus.Running, ct))
            throw new RunAlreadyInProgressException();

        var run = new PricingRun
        {
            LayerId = layerId,
            StartedUtc = DateTime.UtcNow,
            Status = RunStatus.Running,
            TriggeredBy = triggeredBy,
            IsOnDemand = isOnDemand,
        };
        _db.PricingRuns.Add(run);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(triggeredBy, AuditCategories.Run,
            $"Run started ({(isOnDemand ? "on demand" : "scheduled")}) for {layer.DisplayName}",
            nameof(PricingRun), run.Id.ToString(), layerId: layerId, ct: ct);

        try
        {
            // The sibling platform (the other brand in the same country) shares this country's physical
            // warehouse, so its sales fold into the dataset's sales windows (see SourceQueries) — a SKU
            // selling on either brand isn't dead on the other. Looked up regardless of the sibling's
            // active state (sales happen on it whether or not we price it); null for a single-layer
            // country, which leaves the sales query single-platform.
            var sibling = await _db.Layers.AsNoTracking()
                .FirstOrDefaultAsync(l => l.CountryCode == layer.CountryCode && l.Id != layer.Id, ct);

            var sourceContext = new LayerSourceContext
            {
                OperationalDatabase = layer.OperationalDatabase,
                StoreId = layer.StoreId,
                SiblingOperationalDatabase = sibling?.OperationalDatabase,
                SiblingStoreId = sibling?.StoreId ?? -1,
                TranslationCountryId = layer.TranslationCountryId,
                WarehouseStoreId = layer.WarehouseStoreId,
                WmsWarehouseId = layer.WmsWarehouseId,
                FilterVendors = layer.FilterVendors,
                ExcludeUnpublished = layer.ExcludeUnpublished,
            };

            var pulledAt = DateTime.UtcNow;
            var rows = await _reader.GetDailyDatasetAsync(sourceContext, ct);

            // DailySnapshots is unique per (date, Sku) and ProposedPrices per (run, Sku). The source
            // can in principle return the same Sku for two ProductIds; de-dupe defensively so a bulk
            // insert can't collide on a unique key at the very end of a long run.
            var distinct = rows
                .GroupBy(r => r.Sku, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            if (distinct.Count != rows.Count)
                _logger.LogWarning("Source returned {Total} rows for {Distinct} distinct SKUs; de-duped.",
                    rows.Count, distinct.Count);
            rows = distinct;

            run.SkuCount = rows.Count;
            await _db.SaveChangesAsync(ct); // surface SkuCount + "data pulled" progress immediately

            await _snapshots.SaveSnapshotAsync(layerId, rows, pulledAt.Date, ct);

            var bands = await _bandProvider.GetBandsAsync(layerId, ct);

            // Snapshot the band TARGET + FLOOR margins this run is pricing against, so historical views and
            // the Target-impact analysis read the targets the run actually aimed for rather than today's
            // (admin-editable) PriceBands, which drift. One row per band; decoupled from run retention so it
            // outlives the run/proposal purge (see PricingRunBandTarget).
            var bandCapturedUtc = DateTime.UtcNow;
            _db.PricingRunBandTargets.AddRange(bands.Select(b => new PricingRunBandTarget
            {
                PricingRunId = run.Id,
                LayerId = layerId,
                BandId = b.BandId,
                Name = b.Name,
                MinPrice = b.MinPrice,
                MaxPrice = b.MaxPrice,
                TargetMarginPct = b.TargetMarginPct,
                MarginFloorPct = b.MarginFloorPct,
                CapturedUtc = bandCapturedUtc,
            }));
            await _db.SaveChangesAsync(ct);

            var streaks = await _snapshots.GetDeadStockStatesAsync(layerId, ct);
            // Clearance lock: SKUs in an active approved batch → the pushed price to pin them at until
            // their local stock clears. Empty when the layer has no active batch. Loaded once per run.
            var clearanceLocks = await LoadClearanceLocksAsync(layerId, ct);
            // Usable elasticity coefficients only — Algorithm 5 sees a non-null value only when it's trustworthy.
            // Carry the standard error too so the algorithm can require statistical confidence that E < −1.
            var elasticities = (await _db.SkuElasticities.AsNoTracking()
                .Where(e => e.LayerId == layerId && e.IsUsable)
                .Select(e => new { e.Sku, e.Coefficient, e.StandardError })
                .ToListAsync(ct))
                .ToDictionary(e => e.Sku, e => (Coefficient: e.Coefficient, StdErr: e.StandardError),
                    StringComparer.OrdinalIgnoreCase);
            var roundingOverrides = await _db.SkuOverrides.AsNoTracking()
                .Where(o => o.LayerId == layerId && o.RoundingDisabled)
                .Select(o => o.Sku)
                .ToListAsync(ct);
            var roundingDisabledSkus = roundingOverrides.ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Sync-to-Gj50: the price map this Mall run adopts verbatim for any SKU the same-country
            // Gjirafa50 layer already priced (empty for Gj50 / single-brand countries, or until Gj50 has
            // a successful run). Preloaded once, like the dicts above.
            var syncedPrices = await LoadSyncedGj50PricesAsync(layer, ct);

            // Margin-target guardrail — phase A (baseline scan): when a band has a blended-margin goal,
            // price every SKU once to measure each band's margin and collect the algorithm-silent "free"
            // SKUs, then compute the per-SKU nudges that pull each band toward its goal (applied in the
            // pricing loop below). Pure in-memory arithmetic over the already-loaded rows/bands — no extra
            // DB I/O. Runs even for a floor+rounding-only layer: with the demand algorithms off EVERY SKU
            // is "free", so the guardrail (itself a guardrail, like the floor and rounding) moves them all
            // toward the goal. Phase A must pass the SAME FloorAndRoundingOnly flag as the real pass so the
            // baseline prices and locked/free split line up exactly.
            IReadOnlyDictionary<string, MarginOverride> marginOverrides =
                new Dictionary<string, MarginOverride>(StringComparer.OrdinalIgnoreCase);
            if (bands.Any(b => b.TargetMarginPct is > 0m and < 100m))
                marginOverrides = BuildMarginOverrides(
                    rows, bands, streaks, elasticities, roundingDisabledSkus, pulledAt, layer.VatRatePct, layer.FloorAndRoundingOnly, slowLanesFrozen, syncedPrices, clearanceLocks, layer.IgnoreNewProducts, discountTrimRanges);

            // Proposals (and their votes) are streamed to SQL in bulk-copy chunks rather than via EF.
            // We assign proposal Ids ourselves (continuing from the current max) so votes can carry the
            // foreign key; BulkInsertProposalsAsync inserts them with IDENTITY_INSERT semantics.
            var nextProposalId = await _bulk.GetMaxProposedPriceIdAsync(ct) + 1;
            var buffer = new List<ProposedPrice>(FlushChunkSize);
            // Paced dead-stock markdown state advanced by this run (target + period clock, cleared
            // rows included) — collected across the loop, persisted once at the end.
            var pacingUpdates = new List<SkuMarkdownPacingUpdate>();
            // Dead-stock tunnel provenance changed by this run (marker set / retired) — same lifecycle,
            // written separately because a sale must not clear it (see CollectTunnelPriceUpdate).
            var tunnelUpdates = new List<SkuTunnelPriceUpdate>();
            // The SKUs this run flags dead, for the dead-stock history (DeadStockEpisodes) written once the run succeeds.
            var deadToday = new Dictionary<string, DeadSkuToday>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var proposal = PriceOneSku(row, bands, streaks, elasticities, roundingDisabledSkus, pulledAt, layer.VatRatePct, layer.FloorAndRoundingOnly, slowLanesFrozen, marginOverrides, pacingUpdates, tunnelUpdates, syncedPrices, clearanceLocks, layer.IgnoreNewProducts, discountTrimRanges);
                    proposal.Id = nextProposalId++;
                    proposal.PricingRunId = run.Id;
                    proposal.LayerId = layerId;
                    foreach (var vote in proposal.Votes)
                        vote.ProposedPriceId = proposal.Id;
                    buffer.Add(proposal);
                    if (proposal.DeadStockStage != DeadStockStage.None)
                        deadToday[row.Sku] = new DeadSkuToday(proposal.DeadStockStage, row.LocalWarehouseStock, row.Pptcv, row.CurrentPrice);
                    run.ProposalCount++;
                    if (proposal.Status == ProposalStatus.Skipped) run.SkippedCount++;
                }
                catch (Exception ex)
                {
                    run.ErrorCount++;
                    _logger.LogError(ex, "Failed to price SKU {Sku}", row.Sku);
                    run.ErrorMessage ??= $"First error (SKU {row.Sku}): {ex.Message}";
                }

                if (buffer.Count >= FlushChunkSize)
                {
                    await FlushAsync(buffer, run, ct);
                    buffer.Clear();
                }
            }

            if (buffer.Count > 0)
            {
                await FlushAsync(buffer, run, ct);
                buffer.Clear();
            }

            // Persist the paced markdown state the run advanced, so the next run resumes the walk
            // instead of re-engaging. Matched-only against the state rows the snapshot save created.
            if (pacingUpdates.Count > 0)
                await _bulk.UpsertDeadStockMarkdownAsync(layerId, pacingUpdates, ct);

            // Persist the tunnel provenance the run set/retired, so the next run knows which below-floor
            // prices the dead-stock lane owns (and may keep holding once they start selling again).
            if (tunnelUpdates.Count > 0)
                await _bulk.UpsertDeadStockTunnelPriceAsync(layerId, tunnelUpdates, ct);

            // Realign the identity seed after the KeepIdentity inserts so any later insert is safe.
            await _bulk.ReseedProposedPricesAsync(ct);

            run.FinishedUtc = DateTime.UtcNow;
            run.Status = run.ErrorCount == 0 ? RunStatus.Succeeded : RunStatus.SucceededWithErrors;
            await _db.SaveChangesAsync(ct);

            // Consume the slow-lane tick when this run served one (a slow-lane-due run). Stamps the layer's
            // 15-day freeze clock; the next daily runs then freeze the dead-stock lane until a full
            // SlowLaneCadenceDays has elapsed. Guarded like the outcome/retention passes below — the run has
            // already succeeded and been persisted, so a transient failure (or a cancel) stamping this one
            // Layer row must NOT flip the run to Failed; if it fails the layer simply stays due and the next
            // run re-ticks (at worst one extra dead-stock step). Uses None so a Stop mid-save still lands it.
            if (slowLaneDue)
            {
                try
                {
                    var trackedLayer = await _db.Layers.FirstOrDefaultAsync(l => l.Id == layerId, CancellationToken.None);
                    if (trackedLayer is not null)
                    {
                        trackedLayer.LastSlowLaneRunUtc = DateTime.UtcNow;
                        await _db.SaveChangesAsync(CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to stamp the slow-lane tick for run {RunId} (run still succeeded).", run.Id);
                }
            }

            await _audit.LogAsync(triggeredBy, AuditCategories.Run,
                $"Run finished: {run.Status}", nameof(PricingRun), run.Id.ToString(),
                newValue: $"SKUs={run.SkuCount}, proposals={run.ProposalCount}, skipped={run.SkippedCount}, errors={run.ErrorCount}", layerId: layerId, ct: ct);

            // Grade matured price-change outcomes. Never let this fail an otherwise-successful run.
            try
            {
                await _outcomes.EvaluateAsync(run, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outcome evaluation failed after run {RunId} (run still succeeded).", run.Id);
            }

            // Dead-stock history: open, carry or close each SKU's spell and rebuild the country's daily pile row.
            // Small (thousands of rows), and like outcome grading it must never fail an otherwise-successful run.
            try
            {
                await _deadStockEpisodes.RecordAsync(layerId, layer.CountryCode, pulledAt.Date, deadToday, rows, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dead-stock history update failed after run {RunId} (run still succeeded).", run.Id);
            }

            // Retention/TTL: bound the unbounded volume tables. Ships disabled (RetentionEnabled defaults
            // false) and is enabled per environment once the DailyLayerKpi baseline pin and pending-outcome
            // expiry are live. Time-based and global, so it runs once at the end of a successful run; the
            // run's own just-written rows are inside the retention window. FK-safe order: proposals
            // (detaching outcomes, then cascading their votes) → runs → snapshots. SkuDeadStockStates are
            // never trimmed (they replaced the snapshot history scan). Routed through IBulkWriteService —
            // raw SQL in prod, the EF double in tests — so the path is exercised in-memory; never fails the run.
            if (_options.RetentionEnabled)
            {
                try
                {
                    var proposals = 0;
                    var runs = 0;
                    if (_options.RetentionIncludesProposals)
                    {
                        var runCutoff = DateTime.UtcNow.Date.AddDays(-_options.RunRetentionDays);
                        proposals = await _bulk.DeleteProposalsBeforeRunDateAsync(runCutoff, ct);
                        runs = await _bulk.DeleteRunsBeforeDateAsync(runCutoff, ct);
                    }
                    var snapCutoff = DateTime.UtcNow.Date.AddDays(-_options.SnapshotRetentionDays);
                    var snapshots = await _bulk.DeleteSnapshotsBeforeDateAsync(snapCutoff, ct);
                    if (snapshots + proposals + runs > 0)
                        _logger.LogInformation(
                            "Retention deleted {Snapshots} snapshots, {Proposals} proposals, {Runs} runs.",
                            snapshots, proposals, runs);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Retention pass failed after run {RunId} (run still succeeded).", run.Id);
                }
            }

            _logger.LogInformation(
                "Pricing run {RunId} finished: {SkuCount} SKUs, {Proposals} proposals, {Skipped} skipped, {Errors} errors.",
                run.Id, run.SkuCount, run.ProposalCount, run.SkippedCount, run.ErrorCount);
            return run;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Stopped by request (the Stop button). Key off the token, not the exception type: when the
            // cancel fires mid-SQL-command SqlClient surfaces it as a SqlException ("Operation cancelled
            // by user."), not OperationCanceledException. Either way, land the run as Failed with a clear
            // message — the partial data is overwritten by the next successful run. Persist with None so
            // the save itself isn't cancelled.
            run.FinishedUtc = DateTime.UtcNow;
            run.Status = RunStatus.Failed;
            run.ErrorMessage = "Run stopped by request.";
            await _db.SaveChangesAsync(CancellationToken.None);
            await _audit.LogAsync(triggeredBy, AuditCategories.Run, "Run stopped",
                nameof(PricingRun), run.Id.ToString(), layerId: layerId, ct: CancellationToken.None);
            _logger.LogInformation("Pricing run {RunId} stopped by request.", run.Id);
            throw;
        }
        catch (Exception ex)
        {
            run.FinishedUtc = DateTime.UtcNow;
            run.Status = RunStatus.Failed;
            run.ErrorMessage = ex.Message;
            await _db.SaveChangesAsync(CancellationToken.None);
            await _audit.LogAsync(triggeredBy, AuditCategories.Run, "Run failed",
                nameof(PricingRun), run.Id.ToString(), newValue: ex.Message, layerId: layerId, ct: CancellationToken.None);
            _logger.LogError(ex, "Pricing run {RunId} failed.", run.Id);
            throw;
        }
    }

    /// <summary>
    /// Margin-target guardrail, phase A: scans every SKU once to build the per-band candidate set
    /// (excluding skipped, new-product and locally-held dead-stock / pinned SKUs, which are never
    /// margin-tuned and never counted toward a band's margin), then asks <see cref="MarginTargetService"/>
    /// for the per-SKU price overrides that pull each band toward its goal. Pure compute; logs any band
    /// whose goal can't be fully reached within the free SKUs' floor/anchor headroom.
    /// </summary>
    private IReadOnlyDictionary<string, MarginOverride> BuildMarginOverrides(
        IReadOnlyList<SnapshotRow> rows,
        IReadOnlyList<PriceBandConfig> bands,
        IReadOnlyDictionary<string, DeadStockStateView> streaks,
        IReadOnlyDictionary<string, (decimal Coefficient, decimal StdErr)> elasticities,
        HashSet<string> roundingDisabledSkus,
        DateTime pulledAt,
        decimal vatRatePct,
        bool floorAndRoundingOnly,
        bool slowLanesFrozen,
        IReadOnlyDictionary<string, Gj50Adoption> syncedPrices,
        IReadOnlyDictionary<string, decimal> clearanceLocks,
        bool ignoreNewProducts,
        IReadOnlyList<DiscountTrimRange>? discountTrimRanges)
    {
        var candidates = new List<MarginCandidate>(rows.Count);
        foreach (var row in rows)
        {
            if (syncedPrices.ContainsKey(row.Sku)) continue;                  // adopted from Gj50 — price is fixed, not margin-tuned

            // Must mirror the real pass's FloorAndRoundingOnly: with algorithms off every SKU prices to
            // its current/floor baseline and is "free", so the guardrail then moves them toward the goal.
            var (ctx, _) = TryBuildContext(
                row, bands, streaks, elasticities, roundingDisabledSkus, pulledAt, vatRatePct, floorAndRoundingOnly, slowLanesFrozen, clearanceLocks, ignoreNewProducts, discountTrimRanges);
            if (ctx is null) continue;                                        // skipped SKU — not in any band aggregate
            if (ctx.IsNewProduct) continue;                                   // held by the new-product hard rule
            if (ctx.IsDeadStock) continue;                                    // dead stock is the paced dead-stock lane's — never margin-tuned, at any no-sale window (also keeps a frozen/newly-dead SKU from being re-priced off-tick)

            var bounds = _guardrails.GetBounds(ctx);
            if (bounds.Upper <= bounds.Lower) continue;                       // pinned/degenerate (tunnel-held, mispriced)

            var decision = _calculator.Decide(ctx, _algorithms);
            candidates.Add(new MarginCandidate(
                row.Sku,
                ctx.Band.BandId,
                IsLocked: decision.RawWeightedPrice is not null,              // an algorithm voted → its conclusion is fixed
                // Anchor-basis: the guardrail measures the band margin on, and trims from, the anchor
                // (ProductPricing.FinalPrice) — NOT today's current/discounted price. So a SKU showing a
                // fat margin at list gets toned toward the band goal regardless of what it sells at now.
                Price: ctx.AnchorPrice,
                Cost: ctx.Pptcv ?? 0m,
                Floor: bounds.Lower,
                Ceiling: Math.Max(ctx.AnchorPrice, bounds.Lower)));           // never price above list; current plays no part
        }

        var result = _marginTarget.BuildOverrides(bands, candidates, _options);
        foreach (var b in result.Bands.Where(x => x.Unreachable))
            _logger.LogInformation(
                "Margin target for band {BandId} ({Target}%) not fully reachable (baseline {Baseline:0.##}%): {Why}.",
                b.BandId, b.TargetPct, b.BaselineMarginPct, b.NoFreeProducts ? "no free products" : "insufficient free headroom");
        if (result.Overrides.Count > 0)
            _logger.LogInformation("Margin-target guardrail adjusted {Count} SKU(s) across {Bands} band(s).",
                result.Overrides.Count, result.Bands.Count(b => b.MovedCount > 0));
        return result.Overrides;
    }

    /// <summary>
    /// Applies the per-band margin-target guardrail's override for this SKU, if it has one: the
    /// algorithm-silent SKU is re-priced to the guardrail's baseline through the SAME clamp + rounding
    /// tail, so the floor and the anchor cap still bind.
    /// </summary>
    private (PricingDecision Decision, bool MarginAdjusted) ApplyMarginTarget(
        SkuContext ctx, PricingDecision decision, IReadOnlyDictionary<string, MarginOverride> marginOverrides)
    {
        if (!marginOverrides.TryGetValue(ctx.Sku, out var marginOverride))
            return (decision, false);

        return (_calculator.Reprice(ctx, marginOverride.AdjustedPrice,
            new[] { marginOverride.IsTrim ? GuardrailFlags.MarginTargetTrim : GuardrailFlags.MarginTargetLift }), true);
    }

    /// <summary>
    /// Builds the per-SKU pricing context, or returns a skip reason for rows we never price
    /// (unusable price → missing cost → no band, in that policy order). Shared by the main pricing
    /// loop and the margin-target baseline scan so both see identical contexts.
    /// </summary>
    private (SkuContext? Ctx, string? SkipReason) TryBuildContext(
        SnapshotRow row,
        IReadOnlyList<PriceBandConfig> bands,
        IReadOnlyDictionary<string, DeadStockStateView> streaks,
        IReadOnlyDictionary<string, (decimal Coefficient, decimal StdErr)> elasticities,
        HashSet<string> roundingDisabledSkus,
        DateTime pulledAt,
        decimal vatRatePct,
        bool floorAndRoundingOnly,
        bool slowLanesFrozen,
        IReadOnlyDictionary<string, decimal> clearanceLocks,
        bool ignoreNewProducts,
        IReadOnlyList<DiscountTrimRange>? discountTrimRanges)
    {
        if (row.CurrentPrice is not decimal currentPrice || currentPrice <= 0)
            return (null, SkipReasons.MissingPrice);

        // Anchor = ProductPricing.FinalPrice (the SQL already falls back to the shelf OldPrice when
        // absent); guard again defensively so demo/edge rows without an anchor are skipped, not zero-anchored.
        var anchorPrice = row.AnchorPrice is decimal a && a > 0
            ? a
            : row.OldPrice ?? 0m;
        if (anchorPrice <= 0)
            return (null, SkipReasons.MissingPrice);

        // Display-only shelf price; fall back to the anchor when the platform has no OldPrice.
        var oldPrice = row.OldPrice is decimal o && o > 0 ? o : anchorPrice;

        if (row.Pptcv is not decimal pptcv)
            return (null, SkipReasons.MissingCost); // v1 policy: NULL cost is never treated as zero

        // Bands are keyed off PPTCV (cost), not the selling price.
        var band = BandConfigProvider.FindBand(bands, pptcv);
        if (band is null)
            return (null, SkipReasons.NoBand);

        decimal? elasticity = null, elasticityStdErr = null;
        if (elasticities.TryGetValue(row.Sku, out var el))
        {
            elasticity = el.Coefficient;
            elasticityStdErr = el.StdErr;
        }

        var ctx = new SkuContext
        {
            Sku = row.Sku,
            AnchorPrice = anchorPrice,
            OldPrice = oldPrice,
            CurrentPrice = currentPrice,
            Pptcv = row.Pptcv,
            GrossMarginPct = row.GrossMargin,
            Elasticity = elasticity,
            ElasticityStdError = elasticityStdErr,
            KsStock = row.LocalWarehouseStock,
            SupplierStock = row.SupplierWarehouseStock,
            // Platform-new hold, unless the layer opts to price new products (IgnoreNewProducts=false).
            // Gating it here — at the single point the context is built — is what disables the hold
            // everywhere it matters: the PriceCalculator's hard rule, IsDeadStock, the cross-dock lane,
            // the guardrail's IsLocalDeadStock and the margin-target scan all read ctx.IsNewProduct. The
            // snapshot still records the true source value (row.IsNewProduct) for auditing.
            IsNewProduct = ignoreNewProducts && row.IsNewProduct,
            OldestUnitAgeDays = row.OldestUnitAgeDays,
            Qty7 = row.Qty7, Net7 = row.Net7, Disc7 = row.Disc7,
            Qty14 = row.Qty14, Net14 = row.Net14, Disc14 = row.Disc14,
            Qty30 = row.Qty30, Net30 = row.Net30, Disc30 = row.Disc30,
            Qty60 = row.Qty60, Net60 = row.Net60, Disc60 = row.Disc60,
            Qty90 = row.Qty90, Net90 = row.Net90, Disc90 = row.Disc90,
            LaunchDateUtc = row.LaunchDateUtc,
            SnapshotDateUtc = pulledAt,
            ZeroSaleStreakDays = streaks.TryGetValue(row.Sku, out var state) ? state.Streak : 0,
            // Snapshot-tracked observed-dead counter that drives the age stage (0 → Stage 1 at rollout).
            DeadObservedDays = state.DeadObservedDays,
            // Persisted pacing state (percent in the DB, fraction on the context).
            DeadStockPacedTarget = state.MarkdownTargetPct is decimal mt ? mt / 100m : null,
            DeadStockPacedCaughtUpStreak = state.MarkdownCaughtUpStreak,
            // Tunnel provenance: the below-floor price the dead-stock lane owns for this SKU (survives a
            // sale, unlike the pacing state above) — what entitles the guardrail to keep holding it.
            BelowFloorTunnelPrice = state.BelowFloorTunnelPrice,
            Band = band,
            Options = _options,
            VatRatePct = vatRatePct,
            RoundingDisabledForSku = roundingDisabledSkus.Contains(row.Sku),
            FloorAndRoundingOnly = floorAndRoundingOnly,
            SlowLanesFrozen = slowLanesFrozen,
            // Clearance lock: a member of a live approved batch that has sold since the push (see
            // LoadClearanceLocksAsync) and still has locally-held stock is pinned at its approved pushed
            // price by the guardrail. This stock test is the release: the pin lives until the units are
            // gone, and survives the next batch being pushed.
            ClearanceLockPrice = row.LocalWarehouseStock > 0 && clearanceLocks.TryGetValue(row.Sku, out var pushed)
                ? pushed
                : null,
            // Discount optimization: the layer's trim table when the layer has it ticked, else null (off).
            // Set here, at the one place the context is built, so every pass that prices a SKU — the real
            // loop and the margin-target scan — sees the same trimmed prices.
            DiscountTrimRanges = discountTrimRanges,
        };

        return (ctx, null);
    }

    private ProposedPrice PriceOneSku(
        SnapshotRow row,
        IReadOnlyList<PriceBandConfig> bands,
        IReadOnlyDictionary<string, DeadStockStateView> streaks,
        IReadOnlyDictionary<string, (decimal Coefficient, decimal StdErr)> elasticities,
        HashSet<string> roundingDisabledSkus,
        DateTime pulledAt,
        decimal vatRatePct,
        bool floorAndRoundingOnly,
        bool slowLanesFrozen,
        IReadOnlyDictionary<string, MarginOverride> marginOverrides,
        List<SkuMarkdownPacingUpdate> pacingUpdates,
        List<SkuTunnelPriceUpdate> tunnelUpdates,
        IReadOnlyDictionary<string, Gj50Adoption> syncedPrices,
        IReadOnlyDictionary<string, decimal> clearanceLocks,
        bool ignoreNewProducts,
        IReadOnlyList<DiscountTrimRange>? discountTrimRanges)
    {
        var (ctx, skipReason) = TryBuildContext(
            row, bands, streaks, elasticities, roundingDisabledSkus, pulledAt, vatRatePct, floorAndRoundingOnly, slowLanesFrozen, clearanceLocks, ignoreNewProducts, discountTrimRanges);

        // Sync-to-Gj50 (highest precedence): if the same-country Gjirafa50 layer priced this SKU, ADOPT
        // its price verbatim — the product is primarily a Gjirafa50 product (shared warehouse, synced
        // prices), so Gj50's price wins over Mall's own algorithms, guardrails and rounding, and even
        // over a would-be skip / new-product hold. Mall's cost/anchor/stage are still stamped (from the
        // best-effort context) for auditability when available.
        if (syncedPrices.TryGetValue(row.Sku, out var gj50Adoption))
            return SyncedProposal(row, ctx, gj50Adoption);

        if (ctx is null) return Skip(row, skipReason!);

        var decision = _calculator.Decide(ctx, _algorithms);

        // Did the margin-floor audit fire? Captured BEFORE the margin-target override below, which
        // replaces the whole decision object and would otherwise erase the verdict. What the flag
        // records — "the CURRENT price sat under the floor and this proposal lifts it" — stays true
        // either way, because the margin-target price is itself floor-bounded; only the number's author
        // changes, which the reason code (MARGIN_TARGET_*) already says. Without this, a below-floor SKU
        // that margin-target happened to move drops out of the Guardrails filter for below-floor repairs.
        var floorAudited = decision.GuardrailFlagsApplied.Contains(GuardrailFlags.CurrentBelowFloorLifted);

        CollectPacingUpdate(ctx, floorAndRoundingOnly, pacingUpdates);

        // Per-band margin-target guardrail: this algorithm-silent SKU was selected to be nudged toward
        // its band's blended margin goal. Re-priced through the same clamp + rounding tail (so the
        // floor / anchor still bind) and tagged as a guardrail move — it carries no algorithm vote.
        bool marginAdjusted;
        (decision, marginAdjusted) = ApplyMarginTarget(ctx, decision, marginOverrides);

        var guardrailFlags = decision.GuardrailFlagsApplied.ToList();
        if (row.AnchorIsFallback)
            guardrailFlags.Add(GuardrailFlags.AnchorFallbackToShelf);
        if (marginAdjusted)
            guardrailFlags.Add(GuardrailFlags.MarginTargetAdjusted);
        // Re-attach the floor audit's verdict if the override above dropped it (see floorAudited).
        if (floorAudited && !guardrailFlags.Contains(GuardrailFlags.CurrentBelowFloorLifted))
            guardrailFlags.Add(GuardrailFlags.CurrentBelowFloorLifted);

        // Record/retire the dead-stock tunnel provenance for the NEXT run, off the price we actually landed on.
        CollectTunnelPriceUpdate(ctx, decision.FinalPrice, tunnelUpdates);

        // Persist the fitted elasticity the elastic vote acted on (only when it actually voted, i.e. it
        // was confidently elastic). Proposals Review re-projects expected volume from THIS value rather
        // than the live SkuElasticity row, which is overwritten on every fit.
        var elasticVoted = decision.Votes.Any(v => v.AlgorithmCode == AlgorithmCodes.Elasticity);

        var proposal = new ProposedPrice
        {
            Sku = decision.Sku,
            ProductId = row.ProductId,
            ProductName = row.ProductName,
            Gtin = row.Gtin,
            SupplierId = row.SupplierId,
            SupplierName = row.SupplierName,
            BrandId = row.BrandId,
            Brand = row.Brand,
            PriceBandId = ctx.Band.BandId,
            AnchorPrice = decision.AnchorPrice,
            OldPrice = decision.OldPrice,
            CurrentPrice = decision.CurrentPrice,
            ActiveDiscountIds = row.ActiveDiscountIds,
            Pptcv = row.Pptcv,
            RawWeightedPrice = decision.RawWeightedPrice,
            Elasticity = elasticVoted ? ctx.Elasticity : null,
            ElasticityStdError = elasticVoted ? ctx.ElasticityStdError : null,
            ProposedPriceValue = decision.FinalPrice,
            ChangePct = Math.Round(decision.ChangePct, 4),
            HasChange = decision.Changed,
            ReasonCodes = string.Join(",", decision.ReasonCodes),
            GuardrailFlags = string.Join(",", guardrailFlags),
            Status = ProposalStatus.Pending,
            // Stamped here (not derivable later): the WMS oldest-unit age behind the stage is never
            // persisted. Runs for every priced row, so FloorAndRoundingOnly layers get labeled too.
            DeadStockStage = ctx.DeadStockStage,
            // TRUE platform-new signal (independent of the layer's IgnoreNewProducts hold), so the
            // Proposals "new products" filter/badge works whether the row was held or priced.
            IsNewProduct = row.IsNewProduct,
        };

        foreach (var vote in decision.Votes)
        {
            proposal.Votes.Add(new AlgorithmVoteRecord
            {
                AlgorithmCode = vote.AlgorithmCode,
                SuggestedPrice = Math.Round(vote.SuggestedPrice, 4),
                Confidence = vote.Confidence,
                BandWeight = vote.BandWeight,
                EffectiveWeight = vote.EffectiveWeight,
                ReasonCode = vote.ReasonCode,
                ReasonText = vote.ReasonText,
            });
        }

        return proposal;
    }

    /// <summary>
    /// Advances the paced dead-stock markdown state for one priced SKU (see
    /// <see cref="DeadStockMarkdownPacer"/>) and queues the row for the post-run bulk write when it
    /// actually changed. Advances ONLY when the dead-stock lane really votes into the blend — the
    /// layer's algorithms on, the band's DEAD_STOCK enabled with weight. On a frozen (off-tick) daily
    /// run the target can never move and the walk can never step, but the run MAY stamp the period
    /// clock's start when it observes the held target live in the shop — pushes land after the tick
    /// run, so the off-tick days are exactly when "applied" becomes observable. Clears the state once
    /// a tracked SKU stops qualifying as dead (the streak reset in the snapshot transition already
    /// covers sales; this covers the other gates, and still runs while frozen).
    /// </summary>
    private static void CollectPacingUpdate(SkuContext ctx, bool floorAndRoundingOnly, List<SkuMarkdownPacingUpdate> pacingUpdates)
    {
        if (floorAndRoundingOnly) return;
        var setting = ctx.Band.GetAlgorithm(AlgorithmCodes.DeadStock);
        if (!setting.Enabled || setting.Weight <= 0) return;

        if (ctx.IsDeadStock)
        {
            var paced = DeadStockMarkdownPacer.Next(ctx);
            var targetPct = Math.Round(paced.TargetDiscount * 100m, 4);
            var priorPct = ctx.DeadStockPacedTarget is decimal prior ? Math.Round(prior * 100m, 4) : (decimal?)null;

            // Frozen between 15-day ticks: hold the applied target — never step, never move the target
            // (persisting a target change here would let the walk march invisibly on the off-tick daily
            // runs). ONE exception: START THE PERIOD CLOCK when the shop's current price now reflects
            // the held target (the approved proposal's price is live). Prices are pushed AFTER the tick
            // run, so the tick itself can never observe its own push — without this stamp the clock
            // would wait for the NEXT tick and every step would take two 15-day cycles. The stamp is
            // only ever a null → value transition on an unchanged target: the pacer's step transition
            // stays gated (SlowLanesFrozen), so the earliest possible step is still the next tick.
            if (ctx.SlowLanesFrozen)
            {
                if (targetPct == priorPct && ctx.DeadStockPacedCaughtUpStreak is null && paced.CaughtUpStreak is not null)
                    pacingUpdates.Add(new SkuMarkdownPacingUpdate(ctx.Sku, targetPct, paced.CaughtUpStreak));
                return;
            }

            if (targetPct != priorPct || paced.CaughtUpStreak != ctx.DeadStockPacedCaughtUpStreak)
                pacingUpdates.Add(new SkuMarkdownPacingUpdate(ctx.Sku, targetPct, paced.CaughtUpStreak));
        }
        else if (ctx.DeadStockPacedTarget is not null)
        {
            pacingUpdates.Add(new SkuMarkdownPacingUpdate(ctx.Sku, null, null));
        }
    }

    /// <summary>
    /// Maintains the dead-stock tunnel provenance marker (<c>SkuDeadStockState.BelowFloorTunnelPrice</c>) —
    /// the record of which SKUs the dead-stock lane itself took below the margin floor. The guardrail's
    /// "a below-floor price that started selling again is HELD, never raised" rule reads it, so that a price
    /// which fell under a floor that merely MOVED (a PPTCV rise, a stale anchor) is repaired by the floor
    /// instead of being mistaken for a deliberate clearance markdown.
    ///
    /// <para>SET while the guardrail's tunnel path is the one running (locally-held, cost known, not
    /// fresh/new, and no sales in the 90-day window) and the priced result landed under the floor — whether
    /// the tunnel relaxed the floor to get there or the SKU was already below its stage floor and held.
    /// CLEARED once the tunnel is genuinely over: the live price is back at/above the floor, or there is no
    /// locally-held stock left to clear (a later restock earns a fresh marker). A SKU still sitting below the
    /// floor while it sells — exactly the case the hold protects — keeps its marker untouched.</para>
    ///
    /// <para>Unlike <see cref="CollectPacingUpdate"/> this is NOT gated on the DEAD_STOCK algorithm being
    /// enabled or on FloorAndRoundingOnly: it mirrors the GUARDRAIL, which pierces the floor for dead stock
    /// on every layer regardless of which algorithms vote.</para>
    /// </summary>
    private static void CollectTunnelPriceUpdate(
        SkuContext ctx, decimal finalPrice, List<SkuTunnelPriceUpdate> tunnelUpdates)
    {
        var marginFloor = GuardrailService.MarginFloor(ctx);
        if (marginFloor <= 0m) return;   // no cost ⇒ no floor to be under, and nothing meaningful to record

        if (GuardrailService.IsLocalDeadStock(ctx) && ctx.Qty90 == 0 && finalPrice < marginFloor)
        {
            // Rounded to the column's scale so an unchanged marker compares equal and writes nothing.
            var tunnelPrice = Math.Round(finalPrice, 2, MidpointRounding.AwayFromZero);
            if (ctx.BelowFloorTunnelPrice != tunnelPrice)
                tunnelUpdates.Add(new SkuTunnelPriceUpdate(ctx.Sku, tunnelPrice));
            return;
        }

        if (ctx.BelowFloorTunnelPrice is not null && (ctx.CurrentPrice >= marginFloor || ctx.KsStock <= 0))
            tunnelUpdates.Add(new SkuTunnelPriceUpdate(ctx.Sku, null));
    }

    /// <summary>
    /// Bulk-flushes a chunk of proposals and their votes to SQL, then persists the run's progress
    /// counters so the dashboard/monitoring sees the run advancing during a long full-catalog run.
    /// </summary>
    private async Task FlushAsync(List<ProposedPrice> buffer, PricingRun run, CancellationToken ct)
    {
        await _bulk.BulkInsertProposalsAsync(buffer, ct);

        var votes = buffer.SelectMany(p => p.Votes).ToList();
        if (votes.Count > 0)
            await _bulk.BulkInsertVotesAsync(votes, ct);

        await _db.SaveChangesAsync(ct); // flush run.ProposalCount / SkippedCount / ErrorCount progress
    }

    private static ProposedPrice Skip(SnapshotRow row, string reason) => new()
    {
        Sku = row.Sku,
        ProductId = row.ProductId,
        ProductName = row.ProductName,
        Gtin = row.Gtin,
        SupplierId = row.SupplierId,
        SupplierName = row.SupplierName,
        BrandId = row.BrandId,
        Brand = row.Brand,
        OldPrice = row.OldPrice ?? 0,
        AnchorPrice = row.AnchorPrice ?? row.OldPrice ?? 0,
        CurrentPrice = row.CurrentPrice ?? 0,
        ActiveDiscountIds = row.ActiveDiscountIds,
        Pptcv = row.Pptcv,
        ProposedPriceValue = row.CurrentPrice ?? 0,
        ChangePct = 0,
        HasChange = false,
        Status = ProposalStatus.Skipped,
        SkipReason = reason,
        ReasonCodes = reason,
        IsNewProduct = row.IsNewProduct,
    };

    /// <summary>
    /// Builds a proposal that ADOPTS the same-country Gjirafa50 layer's price verbatim (reason
    /// SYNCED_TO_GJ50). No algorithms, guardrails or rounding run — the product is primarily a Gjirafa50
    /// product, so its price wins even past Mall's margin floor / anchor cap. Band / cost / dead-stock
    /// stage are stamped from the best-effort context (for queryability); a SKU Mall couldn't price on
    /// its own (null cost/anchor/band) still adopts the Gj50 price with those left null.
    /// The ProductId shown is the Gj50 (GjirafaEcommerce) id, not Mall's own catalog id, since the row is
    /// primarily a Gjirafa50 product — falling back to Mall's id only if the Gj50 proposal had none. The
    /// ActiveDiscountIds likewise come from the adopted Gj50 proposal (the GjirafaEcommerce discount ids),
    /// not Mall's source row.
    /// </summary>
    private static ProposedPrice SyncedProposal(SnapshotRow row, SkuContext? ctx, Gj50Adoption adoption)
    {
        var syncedPrice = adoption.Price;
        var current = ctx?.CurrentPrice ?? row.CurrentPrice ?? 0m;
        // With no Mall current price (a SKU Mall would otherwise skip) there is no baseline to measure a
        // change against: report ChangePct 0 AND HasChange false so the two never disagree. The adopted
        // Gj50 price is still stamped either way.
        var changePct = current > 0 ? (syncedPrice - current) / current * 100m : 0m;
        var hasChange = current > 0 && Math.Abs(syncedPrice - current) >= 0.01m;
        // SYNCED_TO_GJ50 first (why Mall didn't price it itself), then the Gj50 decision's own reason
        // codes (which Gj50 algorithm actually set the adopted price) so the Mall view can show both.
        var gj50Reasons = adoption.ReasonCodes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => !string.Equals(c, GuardrailFlags.SyncedToGj50, StringComparison.Ordinal));
        var reasonCodes = string.Join(',', new[] { GuardrailFlags.SyncedToGj50 }.Concat(gj50Reasons));
        return new ProposedPrice
        {
            Sku = row.Sku,
            // The Ecommerce (Gj50) ProductId, since a synced row is primarily a Gjirafa50 product; fall
            // back to Mall's own catalog id only if the Gj50 proposal carried none.
            ProductId = adoption.Gj50ProductId ?? row.ProductId,
            ProductName = row.ProductName,
            Gtin = row.Gtin,
            SupplierId = row.SupplierId,
            SupplierName = row.SupplierName,
            BrandId = row.BrandId,
            Brand = row.Brand,
            PriceBandId = ctx?.Band.BandId,
            AnchorPrice = ctx?.AnchorPrice ?? row.AnchorPrice ?? row.OldPrice ?? 0m,
            OldPrice = ctx?.OldPrice ?? row.OldPrice ?? 0m,
            CurrentPrice = current,
            // The Gj50 (GjirafaEcommerce) discount ids that were live on the adopted proposal — a synced
            // row is primarily a Gjirafa50 product, so like ProductId these come from the Gj50 catalog,
            // NOT Mall's own. No fallback to Mall's ids: mixing catalogs would list an id that doesn't
            // exist against the Ecommerce ProductId shown.
            ActiveDiscountIds = adoption.ActiveDiscountIds,
            Pptcv = row.Pptcv,
            ProposedPriceValue = syncedPrice,
            ChangePct = Math.Round(changePct, 4),
            HasChange = hasChange,
            ReasonCodes = reasonCodes,
            GuardrailFlags = string.Empty,
            Status = ProposalStatus.Pending,
            DeadStockStage = ctx?.DeadStockStage ?? DeadStockStage.None,
            IsNewProduct = row.IsNewProduct,
        };
    }

    /// <summary>What a GjirafaMall run adopts from Gjirafa50 for one SKU: the price, the reason codes
    /// of the Gj50 decision that produced it (so the Mall proposal can surface WHICH Gj50 algorithm
    /// drove the adopted price, not just that it was synced), the Gj50 ProductId — which is the
    /// GjirafaEcommerce.dbo.Product id (the Gj50 layer's operational DB), stamped on the Mall proposal
    /// so a synced row shows the Ecommerce id it "really" is, not Mall's own catalog id — and the Gj50
    /// proposal's ActiveDiscountIds, which are the GjirafaEcommerce discount ids that were live on it (a
    /// synced row is primarily a Gjirafa50 product, so its discounts come from the Gj50 catalog too).</summary>
    private readonly record struct Gj50Adoption(decimal Price, string ReasonCodes, int? Gj50ProductId, string? ActiveDiscountIds);

    /// <summary>
    /// SKUs under a live approved clearance batch for this layer that have SOLD at least one unit
    /// since the push, mapped to the price to pin them at (the approved pushed price). The clearance
    /// price found the units a buyer — the guardrail holds each such member with local stock still on
    /// hand at that price, never raised (a sale exits the dead-stock lane, so margin-target would
    /// otherwise bounce the price back up before the stock clears) and never deepened. A member that has
    /// NOT sold is deliberately excluded: it stays in the dead-stock lane's paced walk and keeps stepping
    /// deeper on the slow-lane ticks — pinning it would freeze the whole markdown tunnel at the batch
    /// prices. (SoldUnits counts live orders since push, refreshed from the source alongside the
    /// dashboard; a cancelled order drops back out, releasing the pin the same way it re-enters the
    /// tunnel.)
    ///
    /// <para>The pin's lifetime is the STOCK, not the batch generation. Release happens once local stock
    /// hits 0 (see the call site), when the clearance episode ages out of the tracking window
    /// (<see cref="ApprovedBatchRefreshService.RefreshWindowDays"/> — past that the batch's sold/stock
    /// figures stop being refreshed, so a lock built on them would be enforcing stale data), or when a
    /// Manager explicitly ends the batch (<see cref="ApprovedBatch.IsActive"/>).</para>
    ///
    /// <para>Crucially, pushing the NEXT batch must not release anything: a member that sold under the
    /// markdown leaves the dead-stock population precisely BECAUSE it sold, so it is absent from the
    /// next tick's batch — and if supersession released the pin, every SKU the clearance actually worked
    /// on would be handed straight back to the margin-floor audit with stock still on the shelf. So the
    /// pin is taken per SKU from the NEWEST generation of batches that still contains it (the most recent
    /// approved decision for that SKU), not only from the current one; within a generation — the same
    /// push, split across concurrent batches — the deepest (lowest) pushed price wins.</para>
    ///
    /// <para>Empty when the layer has no live batch. One bulk read per run.</para>
    /// </summary>
    private async Task<IReadOnlyDictionary<string, decimal>> LoadClearanceLocksAsync(int layerId, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.Date.AddDays(-ApprovedBatchRefreshService.RefreshWindowDays);

        var items = await (from item in _db.ApprovedBatchItems.AsNoTracking()
                           join b in _db.ApprovedBatches.AsNoTracking() on item.ApprovedBatchId equals b.Id
                           where b.LayerId == layerId && b.IsActive && b.PushedUtc >= cutoff
                                 && item.PushedPrice > 0m && item.SoldUnits >= 1
                           select new { item.Sku, item.PushedPrice, b.PushedUtc })
                          .ToListAsync(ct);

        return items
            .GroupBy(x => x.Sku, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var newest = g.Max(x => x.PushedUtc);
                    return g.Where(x => x.PushedUtc == newest).Min(x => x.PushedPrice);
                },
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Loads the price map this run adopts verbatim: for a GjirafaMall layer, the same-country Gjirafa50
    /// layer's LATEST SUCCESSFUL run's proposals (changed and unchanged), keyed by SKU (case-insensitive,
    /// like the other per-SKU dicts). Each entry also carries the Gj50 proposal's reason codes so the
    /// adopted decision is visible on the Mall side. Skipped rows are excluded (their price isn't
    /// meaningful) and so are Rejected ones (a manager explicitly declined that Gj50 price, so Mall must
    /// not adopt it — it prices normally instead). Empty for non-Mall layers, single-brand countries, or
    /// until Gjirafa50 has a successful run. One bulk read off the UNIQUE (PricingRunId, Sku) index.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, Gj50Adoption>> LoadSyncedGj50PricesAsync(Layer layer, CancellationToken ct)
    {
        var empty = (IReadOnlyDictionary<string, Gj50Adoption>)new Dictionary<string, Gj50Adoption>();
        if (!string.Equals(layer.Brand, "GjirafaMall", StringComparison.OrdinalIgnoreCase))
            return empty;

        var gj50 = await _db.Layers.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Brand == "Gjirafa50" && l.CountryCode == layer.CountryCode, ct);
        if (gj50 is null) return empty;

        var gj50RunId = await _db.PricingRuns.AsNoTracking()
            .Where(r => r.LayerId == gj50.Id
                        && (r.Status == RunStatus.Succeeded || r.Status == RunStatus.SucceededWithErrors))
            .OrderByDescending(r => r.Id)
            .Select(r => (long?)r.Id)
            .FirstOrDefaultAsync(ct);
        if (gj50RunId is null)
        {
            _logger.LogWarning(
                "Sync-to-Gj50: no successful Gjirafa50 {Country} run found — {Layer} will price normally.",
                layer.CountryCode, layer.DisplayName);
            return empty;
        }

        var rows = await _db.ProposedPrices.AsNoTracking()
            .Where(p => p.PricingRunId == gj50RunId.Value
                        && p.Status != ProposalStatus.Skipped
                        && p.Status != ProposalStatus.Rejected)
            .Select(p => new { p.Sku, p.ProposedPriceValue, p.ReasonCodes, p.ProductId, p.ActiveDiscountIds })
            .ToListAsync(ct);

        var map = new Dictionary<string, Gj50Adoption>(rows.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) // last wins on any case-collision
            map[r.Sku] = new Gj50Adoption(r.ProposedPriceValue, r.ReasonCodes ?? string.Empty, r.ProductId, r.ActiveDiscountIds);

        _logger.LogInformation(
            "Sync-to-Gj50: {Layer} will adopt {Count} prices from Gjirafa50 {Country} run #{Run}.",
            layer.DisplayName, map.Count, layer.CountryCode, gj50RunId.Value);
        return map;
    }

    private async Task FailStaleRunsAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - StaleRunCutoff;
        var stale = await _db.PricingRuns
            .Where(r => r.Status == RunStatus.Running && r.StartedUtc < cutoff)
            .ToListAsync(ct);
        foreach (var run in stale)
        {
            run.Status = RunStatus.Failed;
            run.FinishedUtc = DateTime.UtcNow;
            run.ErrorMessage = "Marked failed: run exceeded the stale-run cutoff (process likely crashed).";
            _logger.LogWarning("Marked stale pricing run {RunId} as failed.", run.Id);
        }
        if (stale.Count > 0) await _db.SaveChangesAsync(ct);
    }
}
