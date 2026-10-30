using Microsoft.EntityFrameworkCore;
using PricingTool.Core.Algorithms;
using PricingTool.Core.Domain;
using PricingTool.Core.Options;
using PricingTool.Data.Entities;

namespace PricingTool.Data.Services;

/// <summary>
/// Role names for the tool. Porta SSO authenticates the user; the role attached to their email
/// in the Users allowlist (<see cref="Entities.User"/>) is what the [Authorize(Roles = …)] markers
/// enforce. Analyst = day-to-day pricing work; Manager = Analyst + user management.
/// </summary>
public static class PricingRoles
{
    public const string Analyst = "Analyst";
    public const string Manager = "Manager";

    /// <summary>True for a role string the app recognises.</summary>
    public static bool IsValid(string? role) => role is Analyst or Manager;
}

/// <summary>
/// Idempotent startup seeding: the layers (Brand + Country), then 8 placeholder price bands per
/// layer with per-band algorithm settings. Band boundaries are PLACEHOLDERS (confirm before go-live);
/// they are denominated in the LAYER'S currency — EUR layers use the plain EUR tiers, and non-EUR
/// layers (MKD/ALL) scale those tiers into local currency (see <see cref="NonEurBandScale"/>) because
/// bands bucket a SKU by its PPTCV cost, which the source stores in the local currency.
/// The initial Manager is seeded via <see cref="SeedInitialUserAsync"/> so the app is usable before
/// anyone is added through the Users admin screen.
/// </summary>
public static class DbSeeder
{
    /// <summary>
    /// The canonical layer set. The migration seeds the same rows (so existing data can be backfilled
    /// to the KS layer); this list keeps a fresh / in-memory DB self-sufficient. KS is first so it
    /// takes Id 1 when identity-generated. Confirmed source ids per Gjirafa.
    /// </summary>
    public static readonly (string Brand, string Country, string Display, string OpDb, int StoreId, int CountryId, int WarehouseStoreId, int WmsWarehouseId, string Currency, bool FilterVendors, int SrPlatformId, int SrCompanyId, decimal VatRatePct)[] LayerSeeds =
    {
        ("GjirafaMall", "KS", "GjirafaMall — Kosovo",          "GjirafaMall",      2, 1, 2, 1, "EUR", true,  2, 0,  18m),
        ("GjirafaMall", "MK", "GjirafaMall — North Macedonia", "GjirafaMall",      1, 3, 1, 6, "MKD", true,  2, 1,  18m),
        ("GjirafaMall", "AL", "GjirafaMall — Albania",         "GjirafaMall",      3, 2, 3, 5, "ALL", true,  2, 3,  20m),
        ("Gjirafa50",   "KS", "Gjirafa50 — Kosovo",            "GjirafaEcommerce", 2, 1, 2, 1, "EUR", false, 1, 1,  18m),
        ("Gjirafa50",   "MK", "Gjirafa50 — North Macedonia",   "GjirafaEcommerce", 1, 3, 1, 6, "MKD", false, 3, 2,  18m),
        ("Gjirafa50",   "AL", "Gjirafa50 — Albania",           "GjirafaEcommerce", 3, 2, 3, 5, "ALL", false, 1, 19, 20m),
    };

    /// <summary>
    /// Band seed defaults (currency-agnostic). Margin floors are conservative starting points.
    /// All EUR bands use <see cref="RoundingConvention.Gj50Charm"/> — the Weber-scaled charm grid is
    /// magnitude-general (a single convention across every band), so the per-band .99/.95/whole/995
    /// mix is no longer needed. Non-EUR layers (MKD/ALL) use the whole-currency Weber charms set in the
    /// per-layer override below (Gj50 → …50, GjirafaMall → …90/…50).
    /// </summary>
    private static readonly (string Name, decimal Min, decimal Max, decimal MarginFloor, RoundingConvention Rounding)[] BandSeeds =
    {
        ("0–10",        0m,    10m,     8m, RoundingConvention.Gj50Charm),
        ("10–50",       10m,   50m,    10m, RoundingConvention.Gj50Charm),
        ("50–100",      50m,   100m,   10m, RoundingConvention.Gj50Charm),
        ("100–250",     100m,  250m,   12m, RoundingConvention.Gj50Charm),
        ("250–500",     250m,  500m,   12m, RoundingConvention.Gj50Charm),
        ("500–750",     500m,  750m,   12m, RoundingConvention.Gj50Charm),
        ("750–1,000",   750m,  1000m,  12m, RoundingConvention.Gj50Charm),
        ("1,000+",      1000m, 999999m,15m, RoundingConvention.Gj50Charm),
    };

    /// <summary>
    /// EUR→local cost ratio used to denominate a NON-EUR layer's band edges in its own currency. Bands
    /// bucket a SKU by its PPTCV cost, and that cost is stored per country in the LOCAL currency (MKD/ALL),
    /// so a non-EUR layer's edges must be the EUR tiers scaled into local currency — otherwise a €30 item
    /// (≈1,860 MKD) reads as being in the top band. MKD is the denar's hard EUR peg (62.0); ALL is the
    /// observed median KS→AL cost ratio (~97.6, rounded to 98). Verified 2026-07-28 over 1.6M SKUs priced
    /// in all three countries. These are seed defaults — a Manager can retune any band on the Bands screen
    /// if a rate drifts. EUR layers (factor 1) keep the plain EUR tiers.
    /// </summary>
    private static readonly Dictionary<string, decimal> NonEurBandScale = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MKD"] = 62m,
        ["ALL"] = 98m,
    };

    /// <summary>The open-ended top band's placeholder max in <see cref="BandSeeds"/> (≈ €1M — no real cost reaches it).</summary>
    private const decimal TopBandCeilingEur = 999999m;

    /// <summary>The top band's ceiling for a scaled (non-EUR) layer: a clean ~€1M-equivalent local value,
    /// so nothing falls out of the top bucket (999,999 × 62 ≈ 62M would too, but this keeps a round number).</summary>
    private const decimal LocalTopBandCeiling = 1_000_000m;

    /// <summary>
    /// Confirmed per-band minimum margin floors by brand (2026-06-30), keyed by the band's lower bound.
    /// Applied to every layer of the brand — the floor is a percentage, so it is currency-agnostic even
    /// where the MKD/ALL band boundaries still need tuning. A brand without an entry falls back to the
    /// generic <see cref="BandSeeds"/> placeholder floor. Existing databases get these via the
    /// SetBrandBandMarginFloors migration.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<decimal, decimal>> BrandMarginFloors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GjirafaMall"] = new() { [0m] = 15m, [10m] = 13m, [50m] = 12m, [100m] = 11m, [250m] = 10m, [500m] = 9m, [750m] = 9m, [1000m] = 6m },
        ["Gjirafa50"]   = new() { [0m] = 15m, [10m] = 13m, [50m] = 10m, [100m] = 9m,  [250m] = 7m,  [500m] = 6m, [750m] = 5m, [1000m] = 5m },
    };

    private static decimal MarginFloorFor(string brand, decimal bandMin, decimal seedDefault) =>
        BrandMarginFloors.TryGetValue(brand, out var floors) && floors.TryGetValue(bandMin, out var f) ? f : seedDefault;

    /// <summary>Gjirafa50 layers are scheduled this many minutes BEFORE their same-country GjirafaMall
    /// sibling, so Gj50's proposals exist before Mall runs and can be adopted verbatim (SYNCED_TO_GJ50).</summary>
    public const int Gj50RunLeadMinutes = 30;

    /// <summary>The seeded run time for a layer: Gjirafa50 runs <see cref="Gj50RunLeadMinutes"/> ahead of
    /// the default (GjirafaMall) slot; every other brand runs at the default.</summary>
    private static string RunTimeFor(string brand, string defaultRunTimeUtc) =>
        string.Equals(brand, "Gjirafa50", StringComparison.OrdinalIgnoreCase)
            && TimeOnly.TryParse(defaultRunTimeUtc, out var t)
            ? t.AddMinutes(-Gj50RunLeadMinutes).ToString("HH:mm")
            : defaultRunTimeUtc;

    public static async Task SeedCoreAsync(PricingToolDbContext db, PricingEngineOptions options, CancellationToken ct = default)
    {
        // 1) Ensure layers exist (idempotent — harmless if the migration already inserted them).
        var sortOrder = 0;
        foreach (var l in LayerSeeds)
        {
            if (!await db.Layers.AnyAsync(x => x.Brand == l.Brand && x.CountryCode == l.Country, ct))
            {
                db.Layers.Add(new Layer
                {
                    Brand = l.Brand,
                    CountryCode = l.Country,
                    DisplayName = l.Display,
                    OperationalDatabase = l.OpDb,
                    StoreId = l.StoreId,
                    TranslationCountryId = l.CountryId,
                    WarehouseStoreId = l.WarehouseStoreId,
                    WmsWarehouseId = l.WmsWarehouseId,
                    SrPlatformId = l.SrPlatformId,
                    SrCompanyId = l.SrCompanyId,
                    Currency = l.Currency,
                    VatRatePct = l.VatRatePct,
                    FilterVendors = l.FilterVendors,
                    ExcludeUnpublished = true,
                    RunTimeUtc = RunTimeFor(l.Brand, options.DefaultRunTimeUtc),
                    CadenceHours = options.DefaultCadenceHours,
                    IsActive = true,
                    SortOrder = sortOrder,
                });
            }
            sortOrder++;
        }
        await db.SaveChangesAsync(ct);

        // 2) Seed the default bands PER LAYER (guard is per-layer, not global).
        var layers = await db.Layers.AsNoTracking().ToListAsync(ct);
        foreach (var layer in layers)
        {
            if (await db.PriceBands.AnyAsync(b => b.LayerId == layer.Id, ct)) continue;

            // Rounding convention per layer — the same Weber charm logic across currencies, only the
            // ending differs by brand and whether the currency has a minor unit:
            //  - Gjirafa50 (EUR): Gj50Charm — Weber grid, .50 ending (the seed default).
            //  - GjirafaMall (EUR): GjmCharm — Weber grid, .99 ending; 10-cent .x9 grid below €5.
            //  - Gjirafa50 (MKD/ALL): Gj50CharmHundreds — Weber grid scaled ×100, always ends …50.
            //  - GjirafaMall (MKD/ALL): GjmCharmHundreds — Weber grid scaled ×100, ends …90 (or …50).
            var nonEur = !string.Equals(layer.Currency, "EUR", StringComparison.OrdinalIgnoreCase);
            var isGjirafaMall = string.Equals(layer.Brand, "GjirafaMall", StringComparison.OrdinalIgnoreCase);

            // Non-EUR layers denominate their band edges in local currency (see NonEurBandScale); EUR
            // layers keep the plain EUR tiers (factor 1). The margin floor stays keyed off the EUR lower
            // bound (a percentage, currency-agnostic), and a scaled band's name keeps the € tier it maps
            // to (e.g. "€10–50") so the bucket is legible even though the stored numbers are local.
            var bandScale = NonEurBandScale.GetValueOrDefault(layer.Currency, 1m);
            var scaledBands = bandScale != 1m;

            var sort = 0;
            foreach (var seed in BandSeeds)
            {
                var rounding =
                    nonEur ? (isGjirafaMall ? RoundingConvention.GjmCharmHundreds : RoundingConvention.Gj50CharmHundreds) :
                    isGjirafaMall ? RoundingConvention.GjmCharm :
                    seed.Rounding;
                var minPrice = seed.Min * bandScale;
                var maxPrice = seed.Max >= TopBandCeilingEur
                    ? (scaledBands ? LocalTopBandCeiling * bandScale : seed.Max) // keep 999,999 for EUR
                    : seed.Max * bandScale;
                var band = new PriceBand
                {
                    LayerId = layer.Id,
                    Name = scaledBands ? $"€{seed.Name}" : seed.Name,
                    MinPrice = minPrice,
                    MaxPrice = maxPrice,
                    MarginFloorPct = MarginFloorFor(layer.Brand, seed.Min, seed.MarginFloor),
                    RoundingConvention = (int)rounding,
                    RoundingEnabled = true,
                    SortOrder = sort++,
                };
                foreach (var (code, _, defaultWeight) in AlgorithmCodes.All)
                {
                    band.AlgorithmSettings.Add(new BandAlgorithmSetting
                    {
                        AlgorithmCode = code,
                        Enabled = true,
                        Weight = defaultWeight,
                    });
                }
                db.PriceBands.Add(band);
            }
            await db.SaveChangesAsync(ct);
        }

        // 3) Seed the discount-optimization table PER LAYER (same per-layer guard as the bands). This is
        //    only the table of inputs — the feature itself stays OFF until a Manager ticks "Discount
        //    optimization" for the layer on the Schedule page, so seeding never changes a price.
        foreach (var layer in layers)
        {
            if (await db.DiscountTrimRules.AnyAsync(r => r.LayerId == layer.Id, ct)) continue;
            var order = 0;
            foreach (var range in DiscountOptimizer.DefaultRanges)
            {
                db.DiscountTrimRules.Add(new DiscountTrimRule
                {
                    LayerId = layer.Id,
                    MinDiscountPct = range.MinDiscountPct,
                    MaxDiscountPct = range.MaxDiscountPct,
                    TrimPct = range.TrimPct,
                    SortOrder = order++,
                });
            }
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Seeds the initial Manager so the system is usable before anyone is added via the Users admin
    /// screen. Idempotent: inserts only when the Users table is empty, so an admin can later demote
    /// or deactivate this account without it being recreated on the next startup.
    /// </summary>
    public static async Task SeedInitialUserAsync(
        PricingToolDbContext db, string initialAdminEmail, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(initialAdminEmail)) return;
        if (await db.Users.AnyAsync(ct)) return;

        db.Users.Add(new User
        {
            Email = initialAdminEmail.Trim().ToLowerInvariant(),
            Name = "Drit",
            Role = PricingRoles.Manager,
            Active = true,
            CreatedUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }
}
