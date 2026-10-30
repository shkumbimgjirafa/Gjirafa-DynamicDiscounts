using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class SetMkAlBandBoundariesToLocalCurrency : Migration
    {
        // No schema change — re-denominates seeded reference data (the MK/AL band boundaries).
        //
        // A price band buckets a SKU by its PPTCV COST, and that cost is stored per country in the LOCAL
        // currency, not EUR (verified 2026-07-28 over 1.6M SKUs priced in all three countries: the same
        // SKU's cost is ≈62× the EUR value in MKD — the denar's hard peg — and ≈98× in ALL). But the MK/AL
        // bands were seeded with the EUR-scale tier numbers (0–10 … 1000+), so every SKU costing more than
        // ~€16 (MK) / ~€10 (AL) collapsed into the top "1000+" band, and the per-band margin floors,
        // dead-stock rules and rounding were applied to the wrong bucket.
        //
        // Re-scale each MK/AL band's [MinPrice, MaxPrice) into local currency by the layer's EUR→local
        // factor (MKD ×62, ALL ×98) and rename it to the € tier it maps to (e.g. "€10–50"), so a €30 item
        // (≈1,860 MKD) now lands in the €10–50 bucket. The open-ended top band gets a clean ≈€1M-equivalent
        // ceiling so nothing falls out. Fresh DBs get the scaled bands straight from DbSeeder (NonEurBandScale);
        // this only fixes existing rows. Mirrors SetMkAlBandRoundingWholeCurrencyCharm.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Keyed off the current EUR lower/upper bounds, so it touches ONLY bands still on the EUR seed
            // scale — idempotent (a re-run finds no EUR-scale rows) and a no-op on any hand-tuned band.
            // All SET right-hand sides read the row's pre-update values, so Name keys off the old EUR MinPrice.
            migrationBuilder.Sql(@"
UPDATE pb
SET pb.MinPrice = pb.MinPrice * f.Factor,
    pb.MaxPrice = CASE WHEN pb.MaxPrice >= 999999 THEN CAST(1000000 AS decimal(18,2)) * f.Factor
                       ELSE pb.MaxPrice * f.Factor END,
    pb.Name = CASE pb.MinPrice
        WHEN 0    THEN N'€0–10'
        WHEN 10   THEN N'€10–50'
        WHEN 50   THEN N'€50–100'
        WHEN 100  THEN N'€100–250'
        WHEN 250  THEN N'€250–500'
        WHEN 500  THEN N'€500–750'
        WHEN 750  THEN N'€750–1,000'
        WHEN 1000 THEN N'€1,000+'
        ELSE pb.Name END
FROM [PricingTool].[PriceBands] pb
INNER JOIN [PricingTool].[Layers] l ON l.Id = pb.LayerId
CROSS APPLY (SELECT CASE l.Currency
                        WHEN 'MKD' THEN CAST(62 AS decimal(18,2))
                        WHEN 'ALL' THEN CAST(98 AS decimal(18,2))
                    END AS Factor) f
WHERE l.Currency IN ('MKD', 'ALL')
  AND f.Factor IS NOT NULL
  AND pb.MinPrice IN (0, 10, 50, 100, 250, 500, 750, 1000)
  AND pb.MaxPrice IN (10, 50, 100, 250, 500, 750, 1000, 999999);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the original EUR-scale seed boundaries and names on the MK/AL bands, keyed by the
            // stable SortOrder (0–7) so it works regardless of the (now local) values in the row.
            migrationBuilder.Sql(@"
UPDATE pb
SET pb.MinPrice = CASE pb.SortOrder WHEN 0 THEN 0 WHEN 1 THEN 10 WHEN 2 THEN 50 WHEN 3 THEN 100
                                    WHEN 4 THEN 250 WHEN 5 THEN 500 WHEN 6 THEN 750 WHEN 7 THEN 1000
                                    ELSE pb.MinPrice END,
    pb.MaxPrice = CASE pb.SortOrder WHEN 0 THEN 10 WHEN 1 THEN 50 WHEN 2 THEN 100 WHEN 3 THEN 250
                                    WHEN 4 THEN 500 WHEN 5 THEN 750 WHEN 6 THEN 1000 WHEN 7 THEN 999999
                                    ELSE pb.MaxPrice END,
    pb.Name = CASE pb.SortOrder WHEN 0 THEN N'0–10' WHEN 1 THEN N'10–50' WHEN 2 THEN N'50–100'
                                WHEN 3 THEN N'100–250' WHEN 4 THEN N'250–500' WHEN 5 THEN N'500–750'
                                WHEN 6 THEN N'750–1,000' WHEN 7 THEN N'1,000+'
                                ELSE pb.Name END
FROM [PricingTool].[PriceBands] pb
INNER JOIN [PricingTool].[Layers] l ON l.Id = pb.LayerId
WHERE l.Currency IN ('MKD', 'ALL') AND pb.SortOrder BETWEEN 0 AND 7;");
        }
    }
}
