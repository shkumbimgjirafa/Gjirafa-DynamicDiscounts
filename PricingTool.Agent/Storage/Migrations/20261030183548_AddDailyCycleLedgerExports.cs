using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Agent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddDailyCycleLedgerExports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DailyCycleStep",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CycleDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Step = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FinishedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyCycleStep", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExportBatch",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LayerId = table.Column<int>(type: "int", nullable: false),
                    RunId = table.Column<long>(type: "bigint", nullable: false),
                    CheckId = table.Column<long>(type: "bigint", nullable: false),
                    FilePath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Rows = table.Column<int>(type: "int", nullable: false),
                    LeftOutByCheck = table.Column<int>(type: "int", nullable: false),
                    LeftOutHoldout = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PushedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PushedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ApprovalId = table.Column<long>(type: "bigint", nullable: true),
                    ExpectedGp30Eur = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExportBatch", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MoneyLedgerEntry",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Source = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    RefId = table.Column<long>(type: "bigint", nullable: false),
                    LayerId = table.Column<int>(type: "int", nullable: false),
                    AppliedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Skus = table.Column<int>(type: "int", nullable: false),
                    ExpectedEur = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    ExpectedBasis = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Measured7Eur = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Measured30Eur = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Evidence = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    MeasuredUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MoneyLedgerEntry", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DailyCycleStep_CycleDate_Step",
                schema: "ai",
                table: "DailyCycleStep",
                columns: new[] { "CycleDate", "Step" });

            migrationBuilder.CreateIndex(
                name: "IX_ExportBatch_RunId",
                schema: "ai",
                table: "ExportBatch",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_MoneyLedgerEntry_Source_RefId",
                schema: "ai",
                table: "MoneyLedgerEntry",
                columns: new[] { "Source", "RefId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyCycleStep",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "ExportBatch",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "MoneyLedgerEntry",
                schema: "ai");
        }
    }
}
