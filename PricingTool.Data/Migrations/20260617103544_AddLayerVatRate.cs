using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLayerVatRate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "VatRatePct",
                schema: "PricingTool",
                table: "Layers",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 18m);

            // Albania is 20% VAT; KS and MK are 18% (the column default).
            migrationBuilder.Sql(
                "UPDATE [PricingTool].[Layers] SET [VatRatePct] = 20 WHERE [CountryCode] = 'AL';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VatRatePct",
                schema: "PricingTool",
                table: "Layers");
        }
    }
}
