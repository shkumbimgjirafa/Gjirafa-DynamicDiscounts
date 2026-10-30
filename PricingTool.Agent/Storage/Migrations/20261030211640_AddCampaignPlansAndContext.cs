using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Agent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignPlansAndContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Context",
                schema: "ai",
                table: "CampaignCalendar",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Representative",
                schema: "ai",
                table: "CampaignCalendar",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "CampaignMessage",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BriefId = table.Column<long>(type: "bigint", nullable: false),
                    PlanId = table.Column<long>(type: "bigint", nullable: true),
                    Round = table.Column<int>(type: "int", nullable: false),
                    From = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    AiCallLogId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignMessage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CampaignPlan",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BriefId = table.Column<long>(type: "bigint", nullable: false),
                    CalendarId = table.Column<long>(type: "bigint", nullable: true),
                    LayerId = table.Column<int>(type: "int", nullable: false),
                    Round = table.Column<int>(type: "int", nullable: false),
                    Strategy = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    DiscountedSkus = table.Column<int>(type: "int", nullable: false),
                    SkusInScope = table.Column<int>(type: "int", nullable: false),
                    HoldoutSkus = table.Column<int>(type: "int", nullable: false),
                    ExpectedUnits = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ExpectedNetRevenue = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ExpectedGrossProfit = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ExpectedMarginPct = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    GrossProfitWithoutDiscounts = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    AverageDepthPct = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    Feasible = table.Column<bool>(type: "bit", nullable: false),
                    Feasibility = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    LeftOut = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Alternatives = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Explanation = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Basis = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PublishBatchId = table.Column<long>(type: "bigint", nullable: true),
                    IsTest = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignPlan", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CampaignPlanLine",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PlanId = table.Column<long>(type: "bigint", nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RegularPrice = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    CurrentPrice = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Cost = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    CampaignPrice = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    DepthPct = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    ExpectedUnits = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    ExpectedGrossProfit = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignPlanLine", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignMessage_BriefId",
                schema: "ai",
                table: "CampaignMessage",
                column: "BriefId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignPlan_BriefId",
                schema: "ai",
                table: "CampaignPlan",
                column: "BriefId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignPlanLine_PlanId",
                schema: "ai",
                table: "CampaignPlanLine",
                column: "PlanId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CampaignMessage",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "CampaignPlan",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "CampaignPlanLine",
                schema: "ai");

            migrationBuilder.DropColumn(
                name: "Context",
                schema: "ai",
                table: "CampaignCalendar");

            migrationBuilder.DropColumn(
                name: "Representative",
                schema: "ai",
                table: "CampaignCalendar");
        }
    }
}
