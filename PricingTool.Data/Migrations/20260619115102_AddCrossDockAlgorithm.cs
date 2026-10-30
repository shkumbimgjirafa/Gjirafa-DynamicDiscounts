using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <summary>
    /// Add the CROSS_DOCK (supplier-fulfilled) markdown algorithm to every existing band at a low default
    /// weight (40). The seeder only seeds fresh DBs (it skips layers that already have bands), so existing
    /// per-band settings need this data migration — without it the new algorithm is treated as disabled
    /// (GetAlgorithm returns Enabled=false for a missing code) and never votes in existing environments.
    /// </summary>
    public partial class AddCrossDockAlgorithm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
INSERT INTO [PricingTool].[BandAlgorithmSettings] ([PriceBandId],[AlgorithmCode],[Enabled],[Weight])
SELECT b.[Id], 'CROSS_DOCK', 1, 40
FROM [PricingTool].[PriceBands] b
WHERE NOT EXISTS (
    SELECT 1 FROM [PricingTool].[BandAlgorithmSettings] s
    WHERE s.[PriceBandId] = b.[Id] AND s.[AlgorithmCode] = 'CROSS_DOCK');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DELETE FROM [PricingTool].[BandAlgorithmSettings] WHERE [AlgorithmCode] = 'CROSS_DOCK';");
        }
    }
}
