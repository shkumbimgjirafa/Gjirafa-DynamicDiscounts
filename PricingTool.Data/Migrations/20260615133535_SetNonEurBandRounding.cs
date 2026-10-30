using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class SetNonEurBandRounding : Migration
    {
        // No schema change — corrects already-seeded reference data: non-EUR layers (MKD/ALL) were
        // first seeded with the EUR charm conventions (.99/.95/whole/995), which don't apply to
        // currencies with no minor unit. Switch them to whole-currency …99 (RoundingConvention 5).
        // Fresh DBs get this directly from DbSeeder; this only fixes the dev/existing rows.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE pb SET pb.RoundingConvention = 5
FROM [PricingTool].[PriceBands] pb
INNER JOIN [PricingTool].[Layers] l ON l.Id = pb.LayerId
WHERE l.Currency <> 'EUR' AND pb.RoundingConvention <> 5;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE pb SET pb.RoundingConvention = 1
FROM [PricingTool].[PriceBands] pb
INNER JOIN [PricingTool].[Layers] l ON l.Id = pb.LayerId
WHERE l.Currency <> 'EUR' AND pb.RoundingConvention = 5;");
        }
    }
}
