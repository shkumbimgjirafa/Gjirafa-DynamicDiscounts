using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPerBandDeadStockMinStockAge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Default 30 backfills existing bands to the previous freshness gate so dead-stock behaviour
            // is unchanged until an admin edits a band (matches the entity/PriceBandConfig default).
            migrationBuilder.AddColumn<int>(
                name: "DeadStockMinStockAgeDays",
                schema: "PricingTool",
                table: "PriceBands",
                type: "int",
                nullable: false,
                defaultValue: 30);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeadStockMinStockAgeDays",
                schema: "PricingTool",
                table: "PriceBands");
        }
    }
}
