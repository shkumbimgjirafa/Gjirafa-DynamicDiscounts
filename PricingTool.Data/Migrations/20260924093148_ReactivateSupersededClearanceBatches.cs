using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <summary>
    /// Clearance locks were being released by BATCH ROLLOVER rather than by the stock clearing.
    ///
    /// <para>Every dead-stock push deactivated the previous generation of batches, and the clearance lock
    /// only read active ones. A SKU that SOLD under the markdown leaves the dead-stock population because
    /// it sold, so it is never carried into the next batch — meaning the moment the next push landed, every
    /// SKU the clearance had actually worked on lost its pin with stock still on the shelf. Unpinned, a
    /// below-cost clearance price reads as a margin-floor breach, so the no-vote floor audit and
    /// margin-target proposed raising it back toward the anchor (on GjirafaMall KS: 618 SKUs stranded,
    /// 301 of them being proposed above their approved clearance price).</para>
    ///
    /// <para>The engine now retires a clearance on its own — newest batch per SKU, bounded by the refresh
    /// tracking window, released when local stock hits 0 — so <c>IsActive</c> goes back to meaning only
    /// "a Manager deliberately ended this". Every false currently in the table is a rollover artifact (the
    /// batches were switched off in generational blocks by the out-of-band push script, never by a manager
    /// ending a clearance), so they are all restored. Batches that have aged out of the tracking window pin
    /// nothing regardless, so reactivating those is inert.</para>
    /// </summary>
    public partial class ReactivateSupersededClearanceBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE [PricingTool].[ApprovedBatches] SET [IsActive] = 1 WHERE [IsActive] = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible: which batches were switched off, and when, was never recorded.
        }
    }
}
