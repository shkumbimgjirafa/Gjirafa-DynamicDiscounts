using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApprovedBatchIsActive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                schema: "PricingTool",
                table: "ApprovedBatches",
                type: "bit",
                nullable: false,
                defaultValue: true);

            // Pre-existing batches predate the clearance-lock feature — they were dashboard-only tracking
            // rows (e.g. the imported KS dead-stock batch), NOT enforced locks. Deactivate every existing
            // row so deploying the feature doesn't retroactively pin (and, for below-cost dead-stock members,
            // move) their prices on the next run. Batches created through the tool from now on set IsActive
            // themselves (entity default true).
            migrationBuilder.Sql("UPDATE [PricingTool].[ApprovedBatches] SET [IsActive] = 0;");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovedBatches_LayerId_IsActive",
                schema: "PricingTool",
                table: "ApprovedBatches",
                columns: new[] { "LayerId", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ApprovedBatches_LayerId_IsActive",
                schema: "PricingTool",
                table: "ApprovedBatches");

            migrationBuilder.DropColumn(
                name: "IsActive",
                schema: "PricingTool",
                table: "ApprovedBatches");
        }
    }
}
