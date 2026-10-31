using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Agent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddRegularPricePublishing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChangedSinceRun",
                schema: "ai",
                table: "PublishBatch",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DiscountsEnded",
                schema: "ai",
                table: "PublishBatch",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RaisedUnderTeamDeal",
                schema: "ai",
                table: "PublishBatch",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TierInserted",
                schema: "ai",
                table: "PublishBatch",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TierRaised",
                schema: "ai",
                table: "PublishBatch",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "NewTierOldPrice",
                schema: "ai",
                table: "PublishAudit",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "NewTierPrice",
                schema: "ai",
                table: "PublishAudit",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OldHasTierPrices",
                schema: "ai",
                table: "PublishAudit",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OldTierOldPrice",
                schema: "ai",
                table: "PublishAudit",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OldTierPrice",
                schema: "ai",
                table: "PublishAudit",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ProposalId",
                schema: "ai",
                table: "PublishAudit",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TierPriceId",
                schema: "ai",
                table: "PublishAudit",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublishAudit_TierPriceId",
                schema: "ai",
                table: "PublishAudit",
                column: "TierPriceId",
                filter: "[TierPriceId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PublishAudit_TierPriceId",
                schema: "ai",
                table: "PublishAudit");

            migrationBuilder.DropColumn(
                name: "ChangedSinceRun",
                schema: "ai",
                table: "PublishBatch");

            migrationBuilder.DropColumn(
                name: "DiscountsEnded",
                schema: "ai",
                table: "PublishBatch");

            migrationBuilder.DropColumn(
                name: "RaisedUnderTeamDeal",
                schema: "ai",
                table: "PublishBatch");

            migrationBuilder.DropColumn(
                name: "TierInserted",
                schema: "ai",
                table: "PublishBatch");

            migrationBuilder.DropColumn(
                name: "TierRaised",
                schema: "ai",
                table: "PublishBatch");

            migrationBuilder.DropColumn(
                name: "NewTierOldPrice",
                schema: "ai",
                table: "PublishAudit");

            migrationBuilder.DropColumn(
                name: "NewTierPrice",
                schema: "ai",
                table: "PublishAudit");

            migrationBuilder.DropColumn(
                name: "OldHasTierPrices",
                schema: "ai",
                table: "PublishAudit");

            migrationBuilder.DropColumn(
                name: "OldTierOldPrice",
                schema: "ai",
                table: "PublishAudit");

            migrationBuilder.DropColumn(
                name: "OldTierPrice",
                schema: "ai",
                table: "PublishAudit");

            migrationBuilder.DropColumn(
                name: "ProposalId",
                schema: "ai",
                table: "PublishAudit");

            migrationBuilder.DropColumn(
                name: "TierPriceId",
                schema: "ai",
                table: "PublishAudit");
        }
    }
}
