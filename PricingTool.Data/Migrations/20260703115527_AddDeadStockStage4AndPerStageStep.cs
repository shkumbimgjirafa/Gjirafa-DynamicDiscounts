using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeadStockStage4AndPerStageStep : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Defaults match PriceBand's C# defaults so EXISTING band rows are backfilled with a valid,
            // monotonic ladder. Critically Stage4 age defaults to 1080 (not 0) — a 0 would classify every
            // dead SKU as Stage 4 — and every per-stage step defaults to 5pp so the walk keeps pacing.
            migrationBuilder.AddColumn<int>(
                name: "DeadStockStage2PeriodDays",
                schema: "PricingTool",
                table: "PriceBands",
                type: "int",
                nullable: false,
                defaultValue: 14);

            migrationBuilder.AddColumn<decimal>(
                name: "DeadStockStage2StepDiscountPct",
                schema: "PricingTool",
                table: "PriceBands",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 5m);

            migrationBuilder.AddColumn<int>(
                name: "DeadStockStage3PeriodDays",
                schema: "PricingTool",
                table: "PriceBands",
                type: "int",
                nullable: false,
                defaultValue: 14);

            migrationBuilder.AddColumn<decimal>(
                name: "DeadStockStage3StepDiscountPct",
                schema: "PricingTool",
                table: "PriceBands",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 5m);

            migrationBuilder.AddColumn<int>(
                name: "DeadStockStage4AgeDays",
                schema: "PricingTool",
                table: "PriceBands",
                type: "int",
                nullable: false,
                defaultValue: 1080);

            migrationBuilder.AddColumn<decimal>(
                name: "DeadStockStage4FloorCostPct",
                schema: "PricingTool",
                table: "PriceBands",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 40m);

            migrationBuilder.AddColumn<int>(
                name: "DeadStockStage4PeriodDays",
                schema: "PricingTool",
                table: "PriceBands",
                type: "int",
                nullable: false,
                defaultValue: 14);

            migrationBuilder.AddColumn<decimal>(
                name: "DeadStockStage4StepDiscountPct",
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
                name: "DeadStockStage2PeriodDays",
                schema: "PricingTool",
                table: "PriceBands");

            migrationBuilder.DropColumn(
                name: "DeadStockStage2StepDiscountPct",
                schema: "PricingTool",
                table: "PriceBands");

            migrationBuilder.DropColumn(
                name: "DeadStockStage3PeriodDays",
                schema: "PricingTool",
                table: "PriceBands");

            migrationBuilder.DropColumn(
                name: "DeadStockStage3StepDiscountPct",
                schema: "PricingTool",
                table: "PriceBands");

            migrationBuilder.DropColumn(
                name: "DeadStockStage4AgeDays",
                schema: "PricingTool",
                table: "PriceBands");

            migrationBuilder.DropColumn(
                name: "DeadStockStage4FloorCostPct",
                schema: "PricingTool",
                table: "PriceBands");

            migrationBuilder.DropColumn(
                name: "DeadStockStage4PeriodDays",
                schema: "PricingTool",
                table: "PriceBands");

            migrationBuilder.DropColumn(
                name: "DeadStockStage4StepDiscountPct",
                schema: "PricingTool",
                table: "PriceBands");
        }
    }
}
