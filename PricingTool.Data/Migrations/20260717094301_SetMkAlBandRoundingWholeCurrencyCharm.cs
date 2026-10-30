using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class SetMkAlBandRoundingWholeCurrencyCharm : Migration
    {
        // No schema change — updates seeded reference data. The non-EUR layers (MKD/ALL, i.e. the MK and
        // AL layers of both brands) were on the plain whole-currency …99 grid (RoundingConvention 5).
        // Give them the same Weber charm logic as the EUR layers, only with whole-currency endings:
        //   Gjirafa50  MKD/ALL -> Gj50CharmHundreds (9)  — Weber-scaled, always ends …50.
        //   GjirafaMall MKD/ALL -> GjmCharmHundreds  (10) — Weber-scaled, ends …90 (or …50).
        // Fresh DBs get this directly from DbSeeder; this only updates dev/existing rows.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE pb SET pb.RoundingConvention =
    CASE WHEN l.Brand = 'GjirafaMall' THEN 10 ELSE 9 END
FROM [PricingTool].[PriceBands] pb
INNER JOIN [PricingTool].[Layers] l ON l.Id = pb.LayerId
WHERE l.Currency <> 'EUR' AND pb.RoundingConvention NOT IN (9, 10);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revert the non-EUR bands to the plain whole-currency …99 grid (RoundingConvention 5).
            migrationBuilder.Sql(@"
UPDATE pb SET pb.RoundingConvention = 5
FROM [PricingTool].[PriceBands] pb
INNER JOIN [PricingTool].[Layers] l ON l.Id = pb.LayerId
WHERE l.Currency <> 'EUR' AND pb.RoundingConvention IN (9, 10);");
        }
    }
}
