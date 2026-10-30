using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class RaiseDeadStockAgeGateAndFloorToCost : Migration
    {
        // No schema change — tightens the per-band dead-stock policy on existing bands, following the
        // 2026-07 stock-age sales analysis (~90% of stock-sourced sales happen within 90 days of
        // check-in; 84% within 60):
        //  - Freshness gate DeadStockMinStockAgeDays: 30 → 90. Stock younger than 90 days still has a
        //    real chance to sell naturally and must not be treated as aged.
        //  - Tunnel floor DeadStockFloorCostPct: 50 → 100. The markdown may still pierce the margin
        //    floor, but never prices below cost (safety: no negative-margin clearing for now).
        // Only rows still looser than the new policy are raised; a band an admin already set stricter
        // (e.g. min age 120) is left alone. Fresh DBs get the same values from the PriceBand defaults.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE [PricingTool].[PriceBands] SET DeadStockMinStockAgeDays = 90 WHERE DeadStockMinStockAgeDays < 90;
UPDATE [PricingTool].[PriceBands] SET DeadStockFloorCostPct = 100 WHERE DeadStockFloorCostPct < 100;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Pre-migration values were per-band and not uniformly recoverable; restore the previous
            // defaults (30-day gate, 50%-of-cost floor) as a defined baseline.
            migrationBuilder.Sql(@"
UPDATE [PricingTool].[PriceBands] SET DeadStockMinStockAgeDays = 30 WHERE DeadStockMinStockAgeDays = 90;
UPDATE [PricingTool].[PriceBands] SET DeadStockFloorCostPct = 50 WHERE DeadStockFloorCostPct = 100;");
        }
    }
}
