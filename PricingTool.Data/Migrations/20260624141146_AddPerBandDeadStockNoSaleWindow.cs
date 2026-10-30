using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPerBandDeadStockNoSaleWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Default 90 backfills existing bands to the previous hardcoded no-sale window so dead-stock
            // behaviour is unchanged until an admin edits a band (matches the entity/PriceBandConfig default).
            migrationBuilder.AddColumn<int>(
                name: "DeadStockNoSaleWindowDays",
                schema: "PricingTool",
                table: "PriceBands",
                type: "int",
                nullable: false,
                defaultValue: 90);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeadStockNoSaleWindowDays",
                schema: "PricingTool",
                table: "PriceBands");
        }
    }
}
