using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProposedPriceAbsChangePct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AbsChangePct",
                schema: "PricingTool",
                table: "ProposedPrices",
                type: "decimal(9,4)",
                precision: 9,
                scale: 4,
                nullable: false,
                computedColumnSql: "ABS([ChangePct])");

            migrationBuilder.CreateIndex(
                name: "IX_ProposedPrices_PricingRunId_Status_AbsChangePct",
                schema: "PricingTool",
                table: "ProposedPrices",
                columns: new[] { "PricingRunId", "Status", "AbsChangePct" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProposedPrices_PricingRunId_Status_AbsChangePct",
                schema: "PricingTool",
                table: "ProposedPrices");

            migrationBuilder.DropColumn(
                name: "AbsChangePct",
                schema: "PricingTool",
                table: "ProposedPrices");
        }
    }
}
