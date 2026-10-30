using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class SyncMallToGj50 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The per-band .50 dead-stock rounding override is superseded: shared SKUs now adopt Gjirafa50's
            // exact proposal (SYNCED_TO_GJ50), so a Mall dead-stock markdown no longer needs to be forced
            // onto the .50 grid.
            migrationBuilder.DropColumn(
                name: "DeadStockRoundingConvention",
                schema: "PricingTool",
                table: "PriceBands");

            // Gjirafa50 runs 30 min before its same-country GjirafaMall sibling, so Gj50's proposals exist
            // before Mall runs and can be adopted verbatim. Only shifts layers still at the 03:00 default,
            // so a hand-tuned schedule is left untouched.
            migrationBuilder.Sql(
                "UPDATE PricingTool.Layers SET RunTimeUtc = '02:30' WHERE Brand = 'Gjirafa50' AND RunTimeUtc = '03:00';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE PricingTool.Layers SET RunTimeUtc = '03:00' WHERE Brand = 'Gjirafa50' AND RunTimeUtc = '02:30';");

            migrationBuilder.AddColumn<int>(
                name: "DeadStockRoundingConvention",
                schema: "PricingTool",
                table: "PriceBands",
                type: "int",
                nullable: true);
        }
    }
}
