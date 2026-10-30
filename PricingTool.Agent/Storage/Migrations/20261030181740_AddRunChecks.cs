using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Agent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddRunChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentRunCheck",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<long>(type: "bigint", nullable: false),
                    LayerId = table.Column<int>(type: "int", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FinishedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    Verdict = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ModelVerdict = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RowsChecked = table.Column<int>(type: "int", nullable: false),
                    RowsFlagged = table.Column<int>(type: "int", nullable: false),
                    RowsSentToModel = table.Column<int>(type: "int", nullable: false),
                    Blocks = table.Column<int>(type: "int", nullable: false),
                    Warns = table.Column<int>(type: "int", nullable: false),
                    Fixed = table.Column<int>(type: "int", nullable: false),
                    CallTag = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    AiCallLogId = table.Column<long>(type: "bigint", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IsTest = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentRunCheck", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentRunCheckRow",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CheckId = table.Column<long>(type: "bigint", nullable: false),
                    ProposalId = table.Column<long>(type: "bigint", nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    BandId = table.Column<int>(type: "int", nullable: true),
                    BandName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    NewPrice = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    CurrentPrice = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Cost = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    MarginPct = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    FloorPct = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    ChangePct = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    Stock = table.Column<int>(type: "int", nullable: true),
                    Flags = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    RuleSeverity = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ModelSeverity = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    ModelReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    FixPrice = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    FixAccepted = table.Column<bool>(type: "bit", nullable: false),
                    FinalSeverity = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentRunCheckRow", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentRunCheckRow_AgentRunCheck_CheckId",
                        column: x => x.CheckId,
                        principalSchema: "ai",
                        principalTable: "AgentRunCheck",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "ai",
                table: "TimeSheetTaskType",
                columns: new[] { "Id", "MinutesBefore", "Name" },
                values: new object[] { 3, null, "Pre-publish check of a pricing run" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRunCheck_RunId_StartedUtc",
                schema: "ai",
                table: "AgentRunCheck",
                columns: new[] { "RunId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRunCheckRow_CheckId",
                schema: "ai",
                table: "AgentRunCheckRow",
                column: "CheckId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRunCheckRow_ProposalId",
                schema: "ai",
                table: "AgentRunCheckRow",
                column: "ProposalId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentRunCheckRow",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "AgentRunCheck",
                schema: "ai");

            migrationBuilder.DeleteData(
                schema: "ai",
                table: "TimeSheetTaskType",
                keyColumn: "Id",
                keyValue: 3);
        }
    }
}
