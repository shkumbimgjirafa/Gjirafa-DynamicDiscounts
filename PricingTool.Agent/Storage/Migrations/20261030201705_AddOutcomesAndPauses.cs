using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Agent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddOutcomesAndPauses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentOutcome",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DecisionId = table.Column<long>(type: "bigint", nullable: false),
                    LayerId = table.Column<int>(type: "int", nullable: false),
                    BandId = table.Column<int>(type: "int", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LiveFromUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LiveExportBatchId = table.Column<long>(type: "bigint", nullable: true),
                    WindowDays = table.Column<int>(type: "int", nullable: false),
                    GpPerDayBefore = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    GpPerDayAfter = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    UnitsPerDayBefore = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    UnitsPerDayAfter = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    HoldoutGpRatio = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: true),
                    HoldoutUnitsRatio = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: true),
                    ExpectedGpPerDayAfter = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    GpVsExpectedPct = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: true),
                    UnitsVsExpectedPct = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: true),
                    Verdict = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Action = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ActionUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Evidence = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    MeasuredUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentOutcome", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CategoryPause",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LayerId = table.Column<int>(type: "int", nullable: false),
                    BandId = table.Column<int>(type: "int", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OutcomeId = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    PausedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReleasedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReleasedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CategoryPause", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentOutcome_DecisionId",
                schema: "ai",
                table: "AgentOutcome",
                column: "DecisionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CategoryPause_LayerId_BandId_Category",
                schema: "ai",
                table: "CategoryPause",
                columns: new[] { "LayerId", "BandId", "Category" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentOutcome",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "CategoryPause",
                schema: "ai");
        }
    }
}
