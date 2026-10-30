using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class RevertToDailyCadenceAndAddSlowLane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastSlowLaneRunUtc",
                schema: "PricingTool",
                table: "Layers",
                type: "datetime2",
                nullable: true);

            // Revert existing layers to DAILY and set the scheduler / slow-lane / elasticity phase
            // ATOMICALLY with the schema change. Prod auto-deploys from main, so doing this here (rather
            // than in a separate manual anchor script) closes the window where the pod could see cadence=24
            // with the stale 2026-07-17 anchor and fire a surprise catch-up run on restart.
            //
            // Touches EXISTING rows only. On a fresh install Layers is still EMPTY at migration time
            // (DbSeeder inserts AFTER migrations run — see Engine/Program.cs), so a new install inherits the
            // 24h entity default and NULL clocks (= due on first run); the baked dates never leak into it.
            //
            // Dates (authored 2026-07-20): LastScheduledRunUtc = 2026-07-20 → first automated daily run
            // 2026-07-21 at RunTimeUtc (no run at deploy-time pod restart). LastSlowLaneRunUtc &
            // LastElasticityFitUtc = 2026-07-17 → the next slow-lane tick / elasticity refit is 2026-08-01
            // (7/17 + 15d), preserving the existing 15-day beat and avoiding an immediate post-deploy
            // re-tick/refit. If this migration is first applied AFTER ~2026-07-21, expect a single on-grid
            // catch-up run at deploy (self-corrects); after 2026-08-01 the slow lane is simply due on the
            // first run. RunTimeUtc is the zero-padded "HH:mm" string, as in scripts/anchor-15day-cadence.sql.
            migrationBuilder.Sql(@"
UPDATE [PricingTool].[Layers]
SET [CadenceHours]         = 24,
    [LastScheduledRunUtc]  = CONVERT(datetime2, '2026-07-20 ' + [RunTimeUtc]),
    [LastSlowLaneRunUtc]   = CONVERT(datetime2, '2026-07-17 ' + [RunTimeUtc]),
    [LastElasticityFitUtc] = CONVERT(datetime2, '2026-07-17 ' + [RunTimeUtc]);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Inverse of the data change: restore the 15-day (360h) cadence that preceded this migration.
            // The re-anchored timestamps can't be un-set to their exact prior values and are left as-is.
            migrationBuilder.Sql("UPDATE [PricingTool].[Layers] SET [CadenceHours] = 360;");

            migrationBuilder.DropColumn(
                name: "LastSlowLaneRunUtc",
                schema: "PricingTool",
                table: "Layers");
        }
    }
}
