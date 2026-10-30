using Microsoft.EntityFrameworkCore;
using PricingTool.Data.Entities;

namespace PricingTool.Data.Services;

/// <summary>One band's target margin and margin floor as they were set by matching the band to the
/// brand's primary-country (KS) band in the same position.</summary>
public sealed record MatchedBandChange(
    int BandId, string BandName, int SortOrder,
    decimal? OldTargetMarginPct, decimal? NewTargetMarginPct,
    decimal OldMarginFloorPct, decimal NewMarginFloorPct);

/// <summary>The outcome of a "match to KS" on one layer: the source (KS) layer, the uplift applied to
/// every target margin, and the bands that actually moved.</summary>
public sealed record BandMatchResult(
    int SourceLayerId, string SourceLayerName, decimal TargetMarginUpliftPct,
    IReadOnlyList<MatchedBandChange> Changes)
{
    public bool AnyChange => Changes.Count > 0;
}

/// <summary>
/// Pulls a brand's Kosovo (primary) band settings onto one of its MK/AL sibling layers on demand — the
/// "Match to KS" button on the Price Bands page. It copies only the two currency-agnostic numbers a
/// manager tunes on KS and wants mirrored: the per-band TARGET MARGIN (plus an optional uplift, since
/// MK/AL commonly run a point or two higher than KS) and the MARGIN FLOOR.
///
/// What it deliberately leaves alone, because those legitimately differ per country: the band
/// BOUNDARIES (denominated in denar/lek — see <see cref="DbSeeder"/> NonEurBandScale), the ROUNDING
/// convention (local whole-currency charm), and the dead-stock stage ladder. Bands are matched on
/// <see cref="PriceBand.SortOrder"/>, never on MinPrice or Name, for the same reason the floor mirror is.
///
/// This is the pull (sibling → KS) counterpart of <see cref="BandFloorMirrorService"/>'s push (a KS save
/// flows the floor out to the siblings); the floor stays in sync either way.
/// </summary>
public class BandMatchService
{
    /// <summary>The country whose bands are the source of truth.</summary>
    public const string PrimaryCountryCode = "KS";

    /// <summary>Default points added to each copied target margin (MK/AL typically run slightly above KS).</summary>
    public const decimal DefaultTargetMarginUpliftPct = 1.5m;

    private readonly PricingToolDbContext _db;

    public BandMatchService(PricingToolDbContext db) => _db = db;

    /// <summary>The primary (KS) layer whose target margins and floors a match would pull into
    /// <paramref name="layerId"/>, or null when it is itself the primary layer or its brand has no KS
    /// layer. Used by the UI to show the button only where a match is possible.</summary>
    public async Task<Layer?> GetSourceAsync(int layerId, CancellationToken ct = default)
    {
        var target = await _db.Layers.AsNoTracking().FirstOrDefaultAsync(l => l.Id == layerId, ct);
        if (target is null || IsPrimary(target)) return null;
        return await _db.Layers.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Brand == target.Brand && l.CountryCode == PrimaryCountryCode, ct);
    }

    /// <summary>
    /// Copies the brand's KS target margins (each plus <paramref name="targetMarginUpliftPct"/> points) and
    /// margin floors onto <paramref name="layerId"/>'s bands, matched by <see cref="PriceBand.SortOrder"/>.
    /// Returns null when the layer is itself primary or has no KS sibling (nothing to match). A KS band with
    /// no target margin leaves the sibling's target unset too. The resulting target is clamped into the valid
    /// (0, 100) range so an extreme uplift can't write an invalid goal. Mutates tracked entities but does NOT
    /// save — the caller commits so the audit entries and the edits land together. Only bands that actually
    /// moved are returned.
    /// </summary>
    public async Task<BandMatchResult?> MatchToPrimaryAsync(
        int layerId, decimal targetMarginUpliftPct, CancellationToken ct = default)
    {
        var source = await GetSourceAsync(layerId, ct);
        if (source is null) return null;

        var sourceBands = await _db.PriceBands.AsNoTracking()
            .Where(b => b.LayerId == source.Id)
            .ToDictionaryAsync(b => b.SortOrder, ct);
        var targetBands = await _db.PriceBands
            .Where(b => b.LayerId == layerId)
            .OrderBy(b => b.SortOrder)
            .ToListAsync(ct);

        var changes = new List<MatchedBandChange>();
        foreach (var target in targetBands)
        {
            if (!sourceBands.TryGetValue(target.SortOrder, out var src)) continue;

            var newTarget = src.TargetMarginPct is decimal t
                ? Math.Clamp(t + targetMarginUpliftPct, 0.01m, 99.99m)
                : (decimal?)null;
            var newFloor = src.MarginFloorPct;

            if (target.TargetMarginPct == newTarget && target.MarginFloorPct == newFloor) continue;

            changes.Add(new MatchedBandChange(
                target.Id, target.Name, target.SortOrder,
                target.TargetMarginPct, newTarget,
                target.MarginFloorPct, newFloor));
            target.TargetMarginPct = newTarget;
            target.MarginFloorPct = newFloor;
        }

        return new BandMatchResult(source.Id, source.DisplayName, targetMarginUpliftPct, changes);
    }

    private static bool IsPrimary(Layer layer) =>
        string.Equals(layer.CountryCode, PrimaryCountryCode, StringComparison.OrdinalIgnoreCase);
}
