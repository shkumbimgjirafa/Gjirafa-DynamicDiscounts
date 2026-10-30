using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeadStockStageTiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "DeadStockStage",
                schema: "PricingTool",
                table: "ProposedPrices",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0);

            // Defaults backfill existing bands to the intended age ladder (matching the
            // entity/PriceBandConfig defaults): stage 2 "Half-year old" at 180 days → 80% of cost,
            // stage 3 "Older than 1 year" at 360 days → 60% of cost. Stage 1 stays the existing
            // DeadStockMinStockAgeDays / DeadStockFloorCostPct pair (90 days → at cost).
            migrationBuilder.AddColumn<int>(
                name: "DeadStockStage2AgeDays",
                schema: "PricingTool",
                table: "PriceBands",
                type: "int",
                nullable: false,
                defaultValue: 180);

            migrationBuilder.AddColumn<decimal>(
                name: "DeadStockStage2FloorCostPct",
                schema: "PricingTool",
                table: "PriceBands",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 80m);

            migrationBuilder.AddColumn<int>(
                name: "DeadStockStage3AgeDays",
                schema: "PricingTool",
                table: "PriceBands",
                type: "int",
                nullable: false,
                defaultValue: 360);

            migrationBuilder.AddColumn<decimal>(
                name: "DeadStockStage3FloorCostPct",
                schema: "PricingTool",
                table: "PriceBands",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 60m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeadStockStage",
                schema: "PricingTool",
                table: "ProposedPrices");

            migrationBuilder.DropColumn(
                name: "DeadStockStage2AgeDays",
                schema: "PricingTool",
                table: "PriceBands");

            migrationBuilder.DropColumn(
                name: "DeadStockStage2FloorCostPct",
                schema: "PricingTool",
                table: "PriceBands");

            migrationBuilder.DropColumn(
                name: "DeadStockStage3AgeDays",
                schema: "PricingTool",
                table: "PriceBands");

            migrationBuilder.DropColumn(
                name: "DeadStockStage3FloorCostPct",
                schema: "PricingTool",
                table: "PriceBands");
        }
    }
}
