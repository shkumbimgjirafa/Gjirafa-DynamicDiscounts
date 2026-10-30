using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierToSnapshotsAndProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SupplierId",
                schema: "PricingTool",
                table: "ProposedPrices",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupplierName",
                schema: "PricingTool",
                table: "ProposedPrices",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SupplierId",
                schema: "PricingTool",
                table: "DailySnapshots",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupplierName",
                schema: "PricingTool",
                table: "DailySnapshots",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProposedPrices_PricingRunId_SupplierId",
                schema: "PricingTool",
                table: "ProposedPrices",
                columns: new[] { "PricingRunId", "SupplierId" })
                .Annotation("SqlServer:Include", new[] { "SupplierName" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProposedPrices_PricingRunId_SupplierId",
                schema: "PricingTool",
                table: "ProposedPrices");

            migrationBuilder.DropColumn(
                name: "SupplierId",
                schema: "PricingTool",
                table: "ProposedPrices");

            migrationBuilder.DropColumn(
                name: "SupplierName",
                schema: "PricingTool",
                table: "ProposedPrices");

            migrationBuilder.DropColumn(
                name: "SupplierId",
                schema: "PricingTool",
                table: "DailySnapshots");

            migrationBuilder.DropColumn(
                name: "SupplierName",
                schema: "PricingTool",
                table: "DailySnapshots");
        }
    }
}
