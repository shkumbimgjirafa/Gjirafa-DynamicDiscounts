using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Agent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddRunReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RunReport",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LayerId = table.Column<int>(type: "int", nullable: false),
                    RunId = table.Column<long>(type: "bigint", nullable: false),
                    BatchId = table.Column<long>(type: "bigint", nullable: false),
                    RunWeekId = table.Column<long>(type: "bigint", nullable: true),
                    PreviousBatchId = table.Column<long>(type: "bigint", nullable: true),
                    PublishedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PricedSkus = table.Column<int>(type: "int", nullable: false),
                    Changed = table.Column<int>(type: "int", nullable: false),
                    Raised = table.Column<int>(type: "int", nullable: false),
                    Lowered = table.Column<int>(type: "int", nullable: false),
                    HoldoutChanged = table.Column<int>(type: "int", nullable: false),
                    HeldByCheck = table.Column<int>(type: "int", nullable: false),
                    Requested = table.Column<int>(type: "int", nullable: false),
                    OnShelf = table.Column<int>(type: "int", nullable: false),
                    LeftOut = table.Column<int>(type: "int", nullable: false),
                    LeftOutByKind = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ExpectedGp30Eur = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: true),
                    ExpectedSkus = table.Column<int>(type: "int", nullable: false),
                    AlphaPassId = table.Column<long>(type: "bigint", nullable: true),
                    Headline = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    AlphaText = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    AlphaWatch = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    AlphaStatus = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    AiCallLogId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunReport", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RunReportCell",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReportId = table.Column<long>(type: "bigint", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    BandId = table.Column<int>(type: "int", nullable: false),
                    FloorPct = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: true),
                    TargetPct = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: true),
                    ByAlpha = table.Column<bool>(type: "bit", nullable: false),
                    Holdout = table.Column<bool>(type: "bit", nullable: false),
                    Skus = table.Column<int>(type: "int", nullable: false),
                    Changed = table.Column<int>(type: "int", nullable: false),
                    Up = table.Column<int>(type: "int", nullable: false),
                    Down = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunReportCell", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunReportCell_RunReport_ReportId",
                        column: x => x.ReportId,
                        principalSchema: "ai",
                        principalTable: "RunReport",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RunReport_BatchId",
                schema: "ai",
                table: "RunReport",
                column: "BatchId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RunReport_LayerId_PublishedUtc",
                schema: "ai",
                table: "RunReport",
                columns: new[] { "LayerId", "PublishedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RunReportCell_ReportId",
                schema: "ai",
                table: "RunReportCell",
                column: "ReportId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RunReportCell",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "RunReport",
                schema: "ai");
        }
    }
}
