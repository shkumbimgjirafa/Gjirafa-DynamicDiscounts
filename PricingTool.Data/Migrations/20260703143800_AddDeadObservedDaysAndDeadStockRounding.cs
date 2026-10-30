using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeadObservedDaysAndDeadStockRounding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DeadObservedDays",
                schema: "PricingTool",
                table: "SkuDeadStockStates",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PrevDeadObservedDays",
                schema: "PricingTool",
                table: "SkuDeadStockStates",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DeadStockRoundingConvention",
                schema: "PricingTool",
                table: "PriceBands",
                type: "int",
                nullable: true);

            // Existing DBs: GjirafaMall EUR bands round DEAD-STOCK markdowns on Gjirafa50's .50 charm grid
            // (conv 7 = Gj50Charm) even though their normal rounding is GjmCharm — the two brands share one
            // physical warehouse and their prices are synced, so a Mall markdown must land where Gjirafa50
            // would put it. Other bands leave the override NULL (dead stock rounds like the rest of the
            // band). Fresh DBs get this from DbSeeder. Mirrors SetGjirafaMallKsBandRoundingGjmCharm.
            migrationBuilder.Sql(@"
UPDATE pb SET pb.DeadStockRoundingConvention = 7
FROM [PricingTool].[PriceBands] pb
INNER JOIN [PricingTool].[Layers] l ON l.Id = pb.LayerId
WHERE l.Currency = 'EUR' AND l.Brand = 'GjirafaMall';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeadObservedDays",
                schema: "PricingTool",
                table: "SkuDeadStockStates");

            migrationBuilder.DropColumn(
                name: "PrevDeadObservedDays",
                schema: "PricingTool",
                table: "SkuDeadStockStates");

            migrationBuilder.DropColumn(
                name: "DeadStockRoundingConvention",
                schema: "PricingTool",
                table: "PriceBands");
        }
    }
}
