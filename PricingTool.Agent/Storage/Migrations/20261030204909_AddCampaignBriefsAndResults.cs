using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Agent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignBriefsAndResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CampaignBrief",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PassId = table.Column<long>(type: "bigint", nullable: false),
                    LayerId = table.Column<int>(type: "int", nullable: false),
                    CalendarId = table.Column<long>(type: "bigint", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ScopeKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ScopeValue = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    GoalType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    GoalValue = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    MinMarginPct = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    MaxDepthPct = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    Exclusions = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Rationale = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    EvidenceRefs = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Limit = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    LimitDetail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AiCallLogId = table.Column<long>(type: "bigint", nullable: true),
                    IsTest = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignBrief", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CampaignResult",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CalendarId = table.Column<long>(type: "bigint", nullable: false),
                    LayerId = table.Column<int>(type: "int", nullable: false),
                    PassId = table.Column<long>(type: "bigint", nullable: true),
                    Measured = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Lesson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Provisional = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignResult", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignBrief_CalendarId",
                schema: "ai",
                table: "CampaignBrief",
                column: "CalendarId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignBrief_PassId",
                schema: "ai",
                table: "CampaignBrief",
                column: "PassId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignResult_CalendarId",
                schema: "ai",
                table: "CampaignResult",
                column: "CalendarId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CampaignBrief",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "CampaignResult",
                schema: "ai");
        }
    }
}
