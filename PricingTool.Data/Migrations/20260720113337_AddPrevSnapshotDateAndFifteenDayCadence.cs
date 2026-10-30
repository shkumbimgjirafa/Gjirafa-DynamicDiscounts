using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPrevSnapshotDateAndFifteenDayCadence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PrevSnapshotDate",
                schema: "PricingTool",
                table: "SkuDeadStockStates",
                type: "date",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // Backfill existing counters as if the last snapshot spanned a single day (the pre-change daily
            // behaviour), so a same-UTC-date re-pull right after upgrade computes a 1-day gap, not a huge one.
            migrationBuilder.Sql(
                "UPDATE [PricingTool].[SkuDeadStockStates] SET [PrevSnapshotDate] = DATEADD(day, -1, [LastSnapshotDate]);");

            // Move every layer to the 15-day (360h) cadence. Existing rows are never touched by seeding, so
            // the switch has to be an explicit data update; the appsettings/options default (also 360) covers
            // brand-new installs. The one-time 2026-07-17 anchor (LastScheduledRunUtc) is a separate prod/
            // LocalDB script, deliberately NOT baked in here so a fresh install doesn't inherit a stale date.
            migrationBuilder.Sql(
                "UPDATE [PricingTool].[Layers] SET [CadenceHours] = 360;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE [PricingTool].[Layers] SET [CadenceHours] = 24;");

            migrationBuilder.DropColumn(
                name: "PrevSnapshotDate",
                schema: "PricingTool",
                table: "SkuDeadStockStates");
        }
    }
}
