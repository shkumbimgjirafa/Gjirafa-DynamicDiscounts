using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProposedPriceElasticity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Elasticity",
                schema: "PricingTool",
                table: "ProposedPrices",
                type: "decimal(12,6)",
                precision: 12,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ElasticityStdError",
                schema: "PricingTool",
                table: "ProposedPrices",
                type: "decimal(12,6)",
                precision: 12,
                scale: 6,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Elasticity",
                schema: "PricingTool",
                table: "ProposedPrices");

            migrationBuilder.DropColumn(
                name: "ElasticityStdError",
                schema: "PricingTool",
                table: "ProposedPrices");
        }
    }
}
