using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPerBandDeadStockSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Defaults backfill existing bands to the previous hardcoded constants so behaviour is
            // unchanged until an admin edits a band: 10% start, +5pp per 14 days, floor at 50% of cost.
            migrationBuilder.AddColumn<decimal>(
                name: "DeadStockFloorCostPct",
                schema: "PricingTool",
                table: "PriceBands",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 50m);

            migrationBuilder.AddColumn<int>(
                name: "DeadStockPeriodDays",
                schema: "PricingTool",
                table: "PriceBands",
                type: "int",
                nullable: false,
                defaultValue: 14);

            migrationBuilder.AddColumn<decimal>(
                name: "DeadStockStartDiscountPct",
                schema: "PricingTool",
                table: "PriceBands",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 10m);

            migrationBuilder.AddColumn<decimal>(
                name: "DeadStockStepDiscountPct",
                schema: "PricingTool",
                table: "PriceBands",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 5m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeadStockFloorCostPct",
                schema: "PricingTool",
                table: "PriceBands");

            migrationBuilder.DropColumn(
                name: "DeadStockPeriodDays",
                schema: "PricingTool",
                table: "PriceBands");

            migrationBuilder.DropColumn(
                name: "DeadStockStartDiscountPct",
                schema: "PricingTool",
                table: "PriceBands");

            migrationBuilder.DropColumn(
                name: "DeadStockStepDiscountPct",
                schema: "PricingTool",
                table: "PriceBands");
        }
    }
}
