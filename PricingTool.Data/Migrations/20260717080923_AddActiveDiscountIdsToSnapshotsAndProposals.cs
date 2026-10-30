using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddActiveDiscountIdsToSnapshotsAndProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActiveDiscountIds",
                schema: "PricingTool",
                table: "ProposedPrices",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActiveDiscountIds",
                schema: "PricingTool",
                table: "DailySnapshots",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActiveDiscountIds",
                schema: "PricingTool",
                table: "ProposedPrices");

            migrationBuilder.DropColumn(
                name: "ActiveDiscountIds",
                schema: "PricingTool",
                table: "DailySnapshots");
        }
    }
}
