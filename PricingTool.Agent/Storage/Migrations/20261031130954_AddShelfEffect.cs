using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Agent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddShelfEffect : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExpectedCuts",
                schema: "ai",
                table: "RunReport",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "ExpectedE1Eur",
                schema: "ai",
                table: "RunReport",
                type: "decimal(14,2)",
                precision: 14,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ExpectedE2Eur",
                schema: "ai",
                table: "RunReport",
                type: "decimal(14,2)",
                precision: 14,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ExpectedFromCutsEur",
                schema: "ai",
                table: "RunReport",
                type: "decimal(14,2)",
                precision: 14,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ExpectedFromRaisesEur",
                schema: "ai",
                table: "RunReport",
                type: "decimal(14,2)",
                precision: 14,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExpectedRaises",
                schema: "ai",
                table: "RunReport",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ExpectedSoldSkus",
                schema: "ai",
                table: "RunReport",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpectedCuts",
                schema: "ai",
                table: "RunReport");

            migrationBuilder.DropColumn(
                name: "ExpectedE1Eur",
                schema: "ai",
                table: "RunReport");

            migrationBuilder.DropColumn(
                name: "ExpectedE2Eur",
                schema: "ai",
                table: "RunReport");

            migrationBuilder.DropColumn(
                name: "ExpectedFromCutsEur",
                schema: "ai",
                table: "RunReport");

            migrationBuilder.DropColumn(
                name: "ExpectedFromRaisesEur",
                schema: "ai",
                table: "RunReport");

            migrationBuilder.DropColumn(
                name: "ExpectedRaises",
                schema: "ai",
                table: "RunReport");

            migrationBuilder.DropColumn(
                name: "ExpectedSoldSkus",
                schema: "ai",
                table: "RunReport");
        }
    }
}
