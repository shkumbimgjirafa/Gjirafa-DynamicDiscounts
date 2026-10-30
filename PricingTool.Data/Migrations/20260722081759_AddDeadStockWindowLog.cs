using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeadStockWindowLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeadStockWindowLogs",
                schema: "PricingTool",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LayerId = table.Column<int>(type: "int", nullable: false),
                    ApprovedBatchId = table.Column<long>(type: "bigint", nullable: false),
                    BatchLabel = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    WindowOpenedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    WindowClosedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    WindowDays = table.Column<int>(type: "int", nullable: false),
                    SupersededByBatchId = table.Column<long>(type: "bigint", nullable: true),
                    OpenSkuCount = table.Column<int>(type: "int", nullable: false),
                    OpenStockUnits = table.Column<long>(type: "bigint", nullable: false),
                    OpenStockCost = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    MovedSkuCount = table.Column<int>(type: "int", nullable: false),
                    SoldUnits = table.Column<long>(type: "bigint", nullable: false),
                    SoldNet = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ClearedSkuCount = table.Column<int>(type: "int", nullable: false),
                    ClearedStockUnits = table.Column<long>(type: "bigint", nullable: false),
                    ClearedStockCost = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeadStockWindowLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeadStockWindowLogs_ApprovedBatches_ApprovedBatchId",
                        column: x => x.ApprovedBatchId,
                        principalSchema: "PricingTool",
                        principalTable: "ApprovedBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DeadStockWindowLogs_Layers_LayerId",
                        column: x => x.LayerId,
                        principalSchema: "PricingTool",
                        principalTable: "Layers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeadStockWindowClearedItems",
                schema: "PricingTool",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeadStockWindowLogId = table.Column<long>(type: "bigint", nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProductName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Brand = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Band = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    PushedPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Pptcv = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    StockAtPush = table.Column<int>(type: "int", nullable: false),
                    SoldUnits = table.Column<int>(type: "int", nullable: false),
                    SoldNet = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    IsClearedOut = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeadStockWindowClearedItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeadStockWindowClearedItems_DeadStockWindowLogs_DeadStockWindowLogId",
                        column: x => x.DeadStockWindowLogId,
                        principalSchema: "PricingTool",
                        principalTable: "DeadStockWindowLogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeadStockWindowClearedItems_DeadStockWindowLogId_Sku",
                schema: "PricingTool",
                table: "DeadStockWindowClearedItems",
                columns: new[] { "DeadStockWindowLogId", "Sku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeadStockWindowLogs_ApprovedBatchId",
                schema: "PricingTool",
                table: "DeadStockWindowLogs",
                column: "ApprovedBatchId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeadStockWindowLogs_LayerId_WindowClosedUtc",
                schema: "PricingTool",
                table: "DeadStockWindowLogs",
                columns: new[] { "LayerId", "WindowClosedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeadStockWindowClearedItems",
                schema: "PricingTool");

            migrationBuilder.DropTable(
                name: "DeadStockWindowLogs",
                schema: "PricingTool");
        }
    }
}
