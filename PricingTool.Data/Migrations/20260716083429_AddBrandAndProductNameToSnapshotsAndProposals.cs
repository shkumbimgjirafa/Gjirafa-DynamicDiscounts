using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBrandAndProductNameToSnapshotsAndProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Brand",
                schema: "PricingTool",
                table: "ProposedPrices",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BrandId",
                schema: "PricingTool",
                table: "ProposedPrices",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProductName",
                schema: "PricingTool",
                table: "ProposedPrices",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Brand",
                schema: "PricingTool",
                table: "DailySnapshots",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BrandId",
                schema: "PricingTool",
                table: "DailySnapshots",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProductName",
                schema: "PricingTool",
                table: "DailySnapshots",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Brand",
                schema: "PricingTool",
                table: "ProposedPrices");

            migrationBuilder.DropColumn(
                name: "BrandId",
                schema: "PricingTool",
                table: "ProposedPrices");

            migrationBuilder.DropColumn(
                name: "ProductName",
                schema: "PricingTool",
                table: "ProposedPrices");

            migrationBuilder.DropColumn(
                name: "Brand",
                schema: "PricingTool",
                table: "DailySnapshots");

            migrationBuilder.DropColumn(
                name: "BrandId",
                schema: "PricingTool",
                table: "DailySnapshots");

            migrationBuilder.DropColumn(
                name: "ProductName",
                schema: "PricingTool",
                table: "DailySnapshots");
        }
    }
}
