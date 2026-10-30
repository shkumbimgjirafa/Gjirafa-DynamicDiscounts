using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class SetBrandBandMarginFloors : Migration
    {
        // No schema change — sets the confirmed per-band minimum margin floors (2026-06-30) for every
        // layer of each brand, matched on the band's lower bound (MinPrice). The floor is a percentage,
        // so it is currency-agnostic across the KS/MK/AL layers. Fresh DBs get the same values from
        // DbSeeder (BrandMarginFloors). Other brands are untouched.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE pb SET pb.MarginFloorPct = CASE pb.MinPrice
        WHEN 0 THEN 15 WHEN 10 THEN 13 WHEN 50 THEN 12 WHEN 100 THEN 11
        WHEN 250 THEN 10 WHEN 500 THEN 9 WHEN 750 THEN 9 WHEN 1000 THEN 6
        ELSE pb.MarginFloorPct END
FROM [PricingTool].[PriceBands] pb
INNER JOIN [PricingTool].[Layers] l ON l.Id = pb.LayerId
WHERE l.Brand = 'GjirafaMall';

UPDATE pb SET pb.MarginFloorPct = CASE pb.MinPrice
        WHEN 0 THEN 15 WHEN 10 THEN 13 WHEN 50 THEN 10 WHEN 100 THEN 9
        WHEN 250 THEN 7 WHEN 500 THEN 6 WHEN 750 THEN 5 WHEN 1000 THEN 5
        ELSE pb.MarginFloorPct END
FROM [PricingTool].[PriceBands] pb
INNER JOIN [PricingTool].[Layers] l ON l.Id = pb.LayerId
WHERE l.Brand = 'Gjirafa50';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The pre-migration floors were environment-specific and not uniformly recoverable; restore
            // the generic DbSeeder placeholder floors for both brands as a defined baseline.
            migrationBuilder.Sql(@"
UPDATE pb SET pb.MarginFloorPct = CASE pb.MinPrice
        WHEN 0 THEN 8 WHEN 10 THEN 10 WHEN 50 THEN 10 WHEN 100 THEN 12
        WHEN 250 THEN 12 WHEN 500 THEN 12 WHEN 750 THEN 12 WHEN 1000 THEN 15
        ELSE pb.MarginFloorPct END
FROM [PricingTool].[PriceBands] pb
INNER JOIN [PricingTool].[Layers] l ON l.Id = pb.LayerId
WHERE l.Brand IN ('GjirafaMall', 'Gjirafa50');");
        }
    }
}
