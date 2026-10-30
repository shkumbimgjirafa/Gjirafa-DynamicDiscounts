using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConsolidateAlgorithmRoster : Migration
    {
        /// <summary>
        /// Roster 10 → 6: retire STOCK_AGING + SUPPLIER_LOCAL and the merged velocity family
        /// (VELOCITY_FORECAST + STOCKOUT_RISK + MOMENTUM), and seed the new SELL_THROUGH advisor on
        /// every existing band. The seeder only seeds fresh DBs (it skips layers that already have
        /// bands), so existing per-band settings need this data migration. Historical AlgorithmVotes
        /// keep the retired codes (they are not touched here).
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DELETE FROM [PricingTool].[BandAlgorithmSettings]
WHERE [AlgorithmCode] IN ('VELOCITY_FORECAST','STOCKOUT_RISK','MOMENTUM','STOCK_AGING','SUPPLIER_LOCAL');");

            migrationBuilder.Sql(@"
INSERT INTO [PricingTool].[BandAlgorithmSettings] ([PriceBandId],[AlgorithmCode],[Enabled],[Weight])
SELECT b.[Id], 'SELL_THROUGH', 1, 75
FROM [PricingTool].[PriceBands] b
WHERE NOT EXISTS (
    SELECT 1 FROM [PricingTool].[BandAlgorithmSettings] s
    WHERE s.[PriceBandId] = b.[Id] AND s.[AlgorithmCode] = 'SELL_THROUGH');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DELETE FROM [PricingTool].[BandAlgorithmSettings] WHERE [AlgorithmCode] = 'SELL_THROUGH';");

            migrationBuilder.Sql(@"
INSERT INTO [PricingTool].[BandAlgorithmSettings] ([PriceBandId],[AlgorithmCode],[Enabled],[Weight])
SELECT b.[Id], v.code, 1, v.wt
FROM [PricingTool].[PriceBands] b
CROSS APPLY (VALUES
    ('VELOCITY_FORECAST',70),('STOCKOUT_RISK',80),('MOMENTUM',45),('STOCK_AGING',50),('SUPPLIER_LOCAL',10)
) v(code, wt)
WHERE NOT EXISTS (
    SELECT 1 FROM [PricingTool].[BandAlgorithmSettings] s
    WHERE s.[PriceBandId] = b.[Id] AND s.[AlgorithmCode] = v.code);");
        }
    }
}
