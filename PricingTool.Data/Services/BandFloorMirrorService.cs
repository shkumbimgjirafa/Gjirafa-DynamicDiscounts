using Microsoft.EntityFrameworkCore;
using PricingTool.Data.Entities;

namespace PricingTool.Data.Services;

/// <summary>One band's margin floor as it was copied from the primary-country band onto a sibling layer.</summary>
public sealed record MirroredBandFloor(
    int LayerId, string LayerDisplayName, int BandId, string BandName, decimal OldFloorPct, decimal NewFloorPct);

/// <summary>
/// Keeps the per-band MARGIN FLOOR identical across the three country layers of a brand. The floors are
/// tuned on the Kosovo layer — GjirafaMall KS and Gjirafa50 KS are the primaries — and every save there
/// copies the floor onto that brand's MK and AL bands.
///
/// The floor is a percentage of the selling price, so it carries across currencies unchanged; the band
/// BOUNDARIES do not (MK/AL bands are denominated in denar/lek — see DbSeeder.NonEurBandScale), which is
/// why bands are matched on <see cref="PriceBand.SortOrder"/> and never on MinPrice or Name.
///
/// Only <see cref="PriceBand.MarginFloorPct"/> is mirrored. TargetMarginPct and the dead-stock stage
/// floors are separate settings that legitimately differ per country and are left alone.
/// </summary>
public class BandFloorMirrorService
{
    /// <summary>The country whose bands are the source of truth for the margin floor.</summary>
    public const string PrimaryCountryCode = "KS";

    private readonly PricingToolDbContext _db;

    public BandFloorMirrorService(PricingToolDbContext db) => _db = db;

    /// <summary>The sibling layers a save on <paramref name="layerId"/> would mirror to — empty unless it
    /// is a primary (KS) layer. Used by the UI to explain the propagation before it happens.</summary>
    public async Task<IReadOnlyList<Layer>> GetMirrorTargetsAsync(int layerId, CancellationToken ct = default)
    {
        var source = await _db.Layers.AsNoTracking().FirstOrDefaultAsync(l => l.Id == layerId, ct);
        if (source is null || !IsPrimary(source)) return Array.Empty<Layer>();
        return await SiblingsOfAsync(source, ct);
    }

    /// <summary>The primary (KS) layer whose floors flow into <paramref name="layerId"/>, or null when
    /// <paramref name="layerId"/> is itself the primary (or the brand has no KS layer).</summary>
    public async Task<Layer?> GetMirrorSourceAsync(int layerId, CancellationToken ct = default)
    {
        var target = await _db.Layers.AsNoTracking().FirstOrDefaultAsync(l => l.Id == layerId, ct);
        if (target is null || IsPrimary(target)) return null;
        return await _db.Layers.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Brand == target.Brand && l.CountryCode == PrimaryCountryCode, ct);
    }

    /// <summary>
    /// Copies <paramref name="source"/>'s margin floor onto the same-position band of the brand's other
    /// countries. A no-op when <paramref name="source"/> is not on a primary (KS) layer. The changes are
    /// applied to tracked entities but NOT saved — the caller commits them together with its own edit, so
    /// a failure leaves the layers consistent. Returns only the bands that actually moved, for auditing.
    /// </summary>
    public async Task<IReadOnlyList<MirroredBandFloor>> MirrorMarginFloorAsync(
        PriceBand source, CancellationToken ct = default)
    {
        var sourceLayer = await _db.Layers.AsNoTracking().FirstOrDefaultAsync(l => l.Id == source.LayerId, ct);
        if (sourceLayer is null || !IsPrimary(sourceLayer)) return Array.Empty<MirroredBandFloor>();

        var siblings = await SiblingsOfAsync(sourceLayer, ct);
        if (siblings.Count == 0) return Array.Empty<MirroredBandFloor>();

        var siblingIds = siblings.Select(l => l.Id).ToList();
        var targets = await _db.PriceBands
            .Where(b => siblingIds.Contains(b.LayerId) && b.SortOrder == source.SortOrder)
            .ToListAsync(ct);

        var displayNames = siblings.ToDictionary(l => l.Id, l => l.DisplayName);
        var mirrored = new List<MirroredBandFloor>();
        foreach (var target in targets)
        {
            if (target.MarginFloorPct == source.MarginFloorPct) continue;
            mirrored.Add(new MirroredBandFloor(
                target.LayerId,
                displayNames.TryGetValue(target.LayerId, out var name) ? name : $"Layer {target.LayerId}",
                target.Id, target.Name, target.MarginFloorPct, source.MarginFloorPct));
            target.MarginFloorPct = source.MarginFloorPct;
        }
        return mirrored;
    }

    private static bool IsPrimary(Layer layer) =>
        string.Equals(layer.CountryCode, PrimaryCountryCode, StringComparison.OrdinalIgnoreCase);

    /// <summary>The same brand's non-primary layers, in display order. Inactive layers are included so a
    /// layer that is switched back on does not come back carrying a stale floor.</summary>
    private async Task<IReadOnlyList<Layer>> SiblingsOfAsync(Layer primary, CancellationToken ct) =>
        await _db.Layers.AsNoTracking()
            .Where(l => l.Brand == primary.Brand && l.CountryCode != PrimaryCountryCode)
            .OrderBy(l => l.SortOrder).ThenBy(l => l.Id)
            .ToListAsync(ct);
}
