using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Agent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentDecisionsAndTimeSheet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentPass",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LayerId = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FinishedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CallTag = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    AiCallLogId = table.Column<long>(type: "bigint", nullable: true),
                    Applied = table.Column<int>(type: "int", nullable: false),
                    Held = table.Column<int>(type: "int", nullable: false),
                    Rejected = table.Column<int>(type: "int", nullable: false),
                    Pending = table.Column<int>(type: "int", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsTest = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentPass", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TimeSheetTaskType",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MinutesBefore = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimeSheetTaskType", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentDecision",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PassId = table.Column<long>(type: "bigint", nullable: false),
                    LayerId = table.Column<int>(type: "int", nullable: false),
                    BandId = table.Column<int>(type: "int", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    CurrentFloorPct = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    ProposedFloorPct = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    CurrentTargetPct = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    ProposedTargetPct = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    Confidence = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    EvidenceRefs = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Limit = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    LimitDetail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AiCallLogId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AppliedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDecision", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentDecision_AgentPass_PassId",
                        column: x => x.PassId,
                        principalSchema: "ai",
                        principalTable: "AgentPass",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TimeSheetEntry",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    UserEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    TaskTypeId = table.Column<int>(type: "int", nullable: false),
                    What = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    MinutesBefore = table.Column<int>(type: "int", nullable: true),
                    MinutesAfter = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    Proof = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimeSheetEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TimeSheetEntry_TimeSheetTaskType_TaskTypeId",
                        column: x => x.TaskTypeId,
                        principalSchema: "ai",
                        principalTable: "TimeSheetTaskType",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "ai",
                table: "TimeSheetTaskType",
                columns: new[] { "Id", "MinutesBefore", "Name" },
                values: new object[] { 1, null, "Weekly floor and target review" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentDecision_LayerId_BandId_Category_CreatedUtc",
                schema: "ai",
                table: "AgentDecision",
                columns: new[] { "LayerId", "BandId", "Category", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentDecision_PassId",
                schema: "ai",
                table: "AgentDecision",
                column: "PassId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentDecision_Status",
                schema: "ai",
                table: "AgentDecision",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AgentPass_LayerId_StartedUtc",
                schema: "ai",
                table: "AgentPass",
                columns: new[] { "LayerId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TimeSheetEntry_TaskTypeId",
                schema: "ai",
                table: "TimeSheetEntry",
                column: "TaskTypeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentDecision",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "TimeSheetEntry",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "AgentPass",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "TimeSheetTaskType",
                schema: "ai");
        }
    }
}
