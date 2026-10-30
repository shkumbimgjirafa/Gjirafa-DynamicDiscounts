using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeadStockMarkdownPacing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MarkdownCaughtUpStreak",
                schema: "PricingTool",
                table: "SkuDeadStockStates",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MarkdownTargetPct",
                schema: "PricingTool",
                table: "SkuDeadStockStates",
                type: "decimal(9,4)",
                precision: 9,
                scale: 4,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MarkdownCaughtUpStreak",
                schema: "PricingTool",
                table: "SkuDeadStockStates");

            migrationBuilder.DropColumn(
                name: "MarkdownTargetPct",
                schema: "PricingTool",
                table: "SkuDeadStockStates");
        }
    }
}
