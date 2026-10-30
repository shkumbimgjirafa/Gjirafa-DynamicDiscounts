using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLayerWmsWarehouseId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WmsWarehouseId",
                schema: "PricingTool",
                table: "Layers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Backfill existing layers by country: WarehouseManagmentSystem warehouse ids KS=1, AL=5, MK=6.
            // (DbSeeder sets these on insert for fresh DBs; this covers DBs seeded before the column existed.)
            migrationBuilder.Sql("UPDATE [PricingTool].[Layers] SET [WmsWarehouseId] = 1 WHERE [CountryCode] = 'KS';");
            migrationBuilder.Sql("UPDATE [PricingTool].[Layers] SET [WmsWarehouseId] = 5 WHERE [CountryCode] = 'AL';");
            migrationBuilder.Sql("UPDATE [PricingTool].[Layers] SET [WmsWarehouseId] = 6 WHERE [CountryCode] = 'MK';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WmsWarehouseId",
                schema: "PricingTool",
                table: "Layers");
        }
    }
}
