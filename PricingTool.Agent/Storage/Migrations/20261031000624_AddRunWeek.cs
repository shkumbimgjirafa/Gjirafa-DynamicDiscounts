using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Agent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddRunWeek : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RunWeek",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LayerId = table.Column<int>(type: "int", nullable: false),
                    StartedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FinishedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PassId = table.Column<long>(type: "bigint", nullable: true),
                    EngineRunId = table.Column<long>(type: "bigint", nullable: true),
                    CheckId = table.Column<long>(type: "bigint", nullable: true),
                    PublishBatchId = table.Column<long>(type: "bigint", nullable: true),
                    AiCostUsd = table.Column<decimal>(type: "decimal(12,6)", precision: 12, scale: 6, nullable: false),
                    AiCalls = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunWeek", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RunWeekStep",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunWeekId = table.Column<long>(type: "bigint", nullable: false),
                    Step = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FinishedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Why = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunWeekStep", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RunWeek_LayerId_StartedUtc",
                schema: "ai",
                table: "RunWeek",
                columns: new[] { "LayerId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RunWeekStep_RunWeekId",
                schema: "ai",
                table: "RunWeekStep",
                column: "RunWeekId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RunWeek",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "RunWeekStep",
                schema: "ai");
        }
    }
}
