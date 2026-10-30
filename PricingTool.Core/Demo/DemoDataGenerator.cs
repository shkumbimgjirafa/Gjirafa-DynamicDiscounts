using PricingTool.Core.Domain;

namespace PricingTool.Core.Demo;

/// <summary>
/// Deterministic fake-data generator for demo mode (UseDemoData=true). Produces a realistic
/// catalog of ~320 SKUs across all price bands with distinct behavioral archetypes (fast movers,
/// steady, decelerating, discount-insensitive, dead stock, supplier-only, missing-cost…), and can
/// generate the same catalog "as of" any date so DailySnapshots history can be backfilled.
/// </summary>
public class DemoDataGenerator
{
    private const int CatalogSeed = 941_217; // fixed → same catalog every day, evolving sales

    private record Archetype(
        string Name,
        decimal BaseDailyVelocity,
        decimal VelocityTrend,        // multiplier applied per 30 days elapsed (demo evolution)
        decimal DiscountFraction,
        bool DiscountLiftsVelocity,
        bool DeadStock,
        bool SupplierOnly,
        bool MissingCost);

    private static readonly Archetype[] Archetypes =
    {
        new("fast-mover",       3.5m, 1.10m, 0.10m, true,  false, false, false),
        new("steady-seller",    1.2m, 1.00m, 0.05m, true,  false, false, false),
        new("accelerating",     0.8m, 1.60m, 0.00m, true,  false, false, false),
        new("decelerating",     1.5m, 0.45m, 0.15m, true,  false, false, false),
        new("discount-deaf",    0.6m, 1.00m, 0.30m, false, false, false, false),
        new("slow-mover",       0.15m,1.00m, 0.20m, false, false, false, false),
        new("dead-stock",       0m,   1.00m, 0.25m, false, true,  false, false),
        new("supplier-slow",    0.1m, 1.00m, 0.10m, false, false, true,  false),
        new("missing-cost",     0.9m, 1.00m, 0.10m, true,  false, false, true),
    };

    /// <summary>Fake ZStores-style suppliers. Picked arithmetically (not via the seeded random) so
    /// adding suppliers didn't shift the existing catalog's random sequence.</summary>
    private static readonly (int Id, string Name)[] Suppliers =
    {
        (1, "Demo Distribution"), (2, "Alfa Suppliers"), (3, "TechSource"),
        (4, "Balkan Wholesale"), (5, "PrimeParts"),
    };

    /// <summary>Fake Manufacturer-style brands. Picked arithmetically (like the suppliers) so adding
    /// them doesn't shift the existing catalog's seeded random sequence.</summary>
    private static readonly (int Id, string Name)[] Brands =
    {
        (101, "Nordis"), (102, "Volteon"), (103, "Aurea"), (104, "Kappa Tech"),
        (105, "Meridian"), (106, "Selva"), (107, "Orbex"),
    };

    private static readonly (decimal Min, decimal Max)[] BandRanges =
    {
        (2m, 10m), (10m, 50m), (50m, 100m), (100m, 250m),
        (250m, 500m), (500m, 750m), (750m, 1000m), (1000m, 2500m),
    };

    public IReadOnlyList<SnapshotRow> Generate(DateTime asOfUtc, int skusPerBandPerArchetype = 5)
    {
        var rows = new List<SnapshotRow>();
        var catalogRandom = new Random(CatalogSeed);

        for (var band = 0; band < BandRanges.Length; band++)
        {
            for (var arch = 0; arch < Archetypes.Length; arch++)
            {
                for (var i = 0; i < skusPerBandPerArchetype; i++)
                {
                    rows.Add(BuildSku(band, arch, i, catalogRandom, asOfUtc));
                }
            }
        }

        return rows;
    }

    private static SnapshotRow BuildSku(int bandIndex, int archIndex, int seq, Random catalogRandom, DateTime asOfUtc)
    {
        var archetype = Archetypes[archIndex];
        var (min, max) = BandRanges[bandIndex];
        var sku = $"DEMO-B{bandIndex + 1}-{archetype.Name.ToUpperInvariant().Replace('-', '_')}-{seq + 1:00}";
        // Stable synthetic Product.Id so the demo mirrors production's numeric identifier.
        var productId = 100_000 + (bandIndex * Archetypes.Length + archIndex) * 100 + seq + 1;

        // Catalog-stable attributes come from the shared seeded random (call order is fixed).
        var oldPrice = Math.Round(min + (decimal)catalogRandom.NextDouble() * (max - min), 2);
        var marginPct = 15m + (decimal)catalogRandom.NextDouble() * 40m; // 15..55%
        var stockBase = catalogRandom.Next(5, 120);

        // Sales evolve with the date but stay deterministic per SKU+day.
        var daySeed = HashCode.Combine(CatalogSeed, sku, asOfUtc.Date);
        var dayRandom = new Random(daySeed);

        var currentPrice = Math.Round(oldPrice * (1 - archetype.DiscountFraction), 2);

        // Demo anchor (= ProductPricing.FinalPrice): the shelf OldPrice sits a deterministic 8–25%
        // above the true reference, so the demo exercises the FinalPrice-anchor behaviour
        // (anchor ≠ shelf, and for low-discount archetypes the anchor caps below today's price).
        var anchorInflation = 0.08m + ((seq * 7 + bandIndex * 3 + archIndex) % 18) / 100m;
        var anchorPrice = Math.Round(oldPrice * (1m - anchorInflation), 2);

        // Trend factor: archetype velocity drifts over demo time so momentum/elasticity fire.
        var elapsed30d = (decimal)(asOfUtc.Date - new DateTime(2026, 1, 1)).TotalDays / 30m;
        var trendFactor = (decimal)Math.Pow((double)archetype.VelocityTrend, (double)Math.Min(4m, Math.Max(0m, elapsed30d)));
        var discountLift = archetype.DiscountLiftsVelocity ? 1m + archetype.DiscountFraction * 2.5m : 1m;

        int QtyFor(int days, decimal recencyWeight)
        {
            if (archetype.DeadStock) return 0;
            var expected = archetype.BaseDailyVelocity * discountLift * days *
                           (1m + (trendFactor - 1m) * recencyWeight);
            var noise = 0.7 + dayRandom.NextDouble() * 0.6;
            return (int)Math.Max(0, Math.Round((double)expected * noise));
        }

        // Recent windows feel the trend fully; long windows dilute it.
        var q7 = QtyFor(7, 1.0m);
        var q14 = q7 + QtyFor(7, 0.85m);
        var q30 = q14 + QtyFor(16, 0.6m);
        var q60 = q30 + QtyFor(30, 0.3m);
        var q90 = q60 + QtyFor(30, 0.1m);

        var vatFactor = 1.18m;
        decimal NetFor(int qty) => Math.Round(qty * currentPrice / vatFactor, 2);

        decimal? DiscFor(int qty) => qty == 0 ? null : archetype.DiscountFraction;

        var ksStock = archetype.SupplierOnly ? 0 : stockBase;
        var supplierStock = archetype.SupplierOnly ? stockBase : (stockBase / 3);

        var pptcv = archetype.MissingCost
            ? (decimal?)null
            : Math.Round(currentPrice / vatFactor * (1 - marginPct / 100m), 2);

        // Catalog-stable supplier; ~1 SKU in 30 (~3%) is left unmapped, mirroring the share of priced
        // production SKUs with no ProductStoreMapping row. (mod 30: bandIndex*31 ≡ bandIndex and
        // archIndex*7 stays non-degenerate, so the unmapped SKUs spread across bands AND archetypes.)
        var supplierPick = bandIndex * 31 + archIndex * 7 + seq;
        var supplier = supplierPick % 30 == 6 ? ((int, string)?)null : Suppliers[supplierPick % Suppliers.Length];

        // Catalog-stable brand, chosen arithmetically like the supplier; ~1 SKU in 20 has no manufacturer
        // (mirrors production rows with no Product_Manufacturer_Mapping). A readable product name pairs the
        // brand with the band/archetype so the Proposals list reads like a real catalogue.
        var brandPick = bandIndex * 17 + archIndex * 5 + seq;
        var brand = brandPick % 20 == 3 ? ((int, string)?)null : Brands[brandPick % Brands.Length];
        var productName = $"{brand?.Item2 ?? "Generic"} {archetype.Name.Replace('-', ' ')} B{bandIndex + 1}-{seq + 1:00}";

        return new SnapshotRow
        {
            Sku = sku,
            ProductId = productId,
            ProductName = productName,
            OldPrice = oldPrice,
            AnchorPrice = anchorPrice,
            CurrentPrice = currentPrice,
            CurrentDiscountPct = oldPrice > 0 ? Math.Round((oldPrice - currentPrice) / oldPrice, 4) : 0,
            // A SKU whose current price sits below its shelf price is on an active campaign discount;
            // give the demo a stable synthetic discount id so the "Active discount IDs" column populates.
            ActiveDiscountIds = currentPrice < oldPrice ? (9000 + productId).ToString() : null,
            Pptcv = pptcv,
            GrossMargin = archetype.MissingCost ? null : Math.Round(marginPct, 2),
            LocalWarehouseStock = ksStock,
            SupplierWarehouseStock = supplierStock,
            SupplierId = supplier?.Item1,
            SupplierName = supplier?.Item2,
            BrandId = brand?.Item1,
            Brand = brand?.Item2,
            // Dead-stock demo items are long-stale (well past the freshness gate) so they still mark down;
            // everything else has no check-in age (null), mirroring SKUs the WMS doesn't track.
            OldestUnitAgeDays = archetype.DeadStock ? 120 : (int?)null,
            Qty7 = q7, Net7 = NetFor(q7), Disc7 = DiscFor(q7),
            Qty14 = q14, Net14 = NetFor(q14), Disc14 = DiscFor(q14),
            Qty30 = q30, Net30 = NetFor(q30), Disc30 = DiscFor(q30),
            Qty60 = q60, Net60 = NetFor(q60), Disc60 = DiscFor(q60),
            Qty90 = q90, Net90 = NetFor(q90), Disc90 = DiscFor(q90),
            LaunchDateUtc = null, // mirrors production: launch date unavailable in v1
        };
    }
}
