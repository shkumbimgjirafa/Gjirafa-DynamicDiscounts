using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class SetGjirafaMallKsBandRoundingGjmCharm : Migration
    {
        // No schema change — updates seeded reference data. The previous migration set ALL EUR bands to
        // Gj50Charm (conv 7, .50 ending). Split the brands: GjirafaMall EUR bands -> GjmCharm (conv 8,
        // Weber grid with the k-selected .99/.95/.49/.45 ending, nearest-10c under €5). Gjirafa50 keeps
        // Gj50Charm. Non-EUR layers are untouched. Fresh DBs get this from DbSeeder.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE pb SET pb.RoundingConvention = 8
FROM [PricingTool].[PriceBands] pb
INNER JOIN [PricingTool].[Layers] l ON l.Id = pb.LayerId
WHERE l.Currency = 'EUR' AND l.Brand = 'GjirafaMall' AND pb.RoundingConvention <> 8;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revert GjirafaMall EUR bands to Gj50Charm (conv 7).
            migrationBuilder.Sql(@"
UPDATE pb SET pb.RoundingConvention = 7
FROM [PricingTool].[PriceBands] pb
INNER JOIN [PricingTool].[Layers] l ON l.Id = pb.LayerId
WHERE l.Currency = 'EUR' AND l.Brand = 'GjirafaMall' AND pb.RoundingConvention = 8;");
        }
    }
}
