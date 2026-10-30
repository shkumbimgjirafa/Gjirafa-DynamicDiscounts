using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAnchorPrice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AnchorPrice",
                schema: "PricingTool",
                table: "ProposedPrices",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "AnchorPrice",
                schema: "PricingTool",
                table: "DailySnapshots",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnchorPrice",
                schema: "PricingTool",
                table: "ProposedPrices");

            migrationBuilder.DropColumn(
                name: "AnchorPrice",
                schema: "PricingTool",
                table: "DailySnapshots");
        }
    }
}
