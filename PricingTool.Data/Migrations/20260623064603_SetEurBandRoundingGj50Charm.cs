using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class SetEurBandRoundingGj50Charm : Migration
    {
        // No schema change — updates already-seeded reference data. Gj50Charm (convention 7) is the
        // Weber-scaled charm grid (.50 endings) and is magnitude-general, so it replaces the per-band
        // EUR mix (.99/.95/whole/995 = 1/2/3/4) across ALL bands of EUR layers. Non-EUR layers (MKD/ALL)
        // keep their whole-currency …99 (convention 5) — the .50 ending needs a minor unit they lack.
        // Fresh DBs get this directly from DbSeeder; this only updates dev/existing rows.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE pb SET pb.RoundingConvention = 7
FROM [PricingTool].[PriceBands] pb
INNER JOIN [PricingTool].[Layers] l ON l.Id = pb.LayerId
WHERE l.Currency = 'EUR' AND pb.RoundingConvention <> 7;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revert EUR bands to the prior per-band conventions, keyed by price range:
            // <250 → EndsIn99 (1), 250–1,000 → WholeEuro (3), 1,000+ → Charm995 (4).
            migrationBuilder.Sql(@"
UPDATE pb SET pb.RoundingConvention =
    CASE
        WHEN pb.MinPrice >= 1000 THEN 4
        WHEN pb.MinPrice >= 250  THEN 3
        ELSE 1
    END
FROM [PricingTool].[PriceBands] pb
INNER JOIN [PricingTool].[Layers] l ON l.Id = pb.LayerId
WHERE l.Currency = 'EUR' AND pb.RoundingConvention = 7;");
        }
    }
}
