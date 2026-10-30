using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApprovedBatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApprovedBatches",
                schema: "PricingTool",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LayerId = table.Column<int>(type: "int", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    PushedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SkuCount = table.Column<int>(type: "int", nullable: false),
                    SalesRefreshedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SourceRunId = table.Column<long>(type: "bigint", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovedBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApprovedBatches_Layers_LayerId",
                        column: x => x.LayerId,
                        principalSchema: "PricingTool",
                        principalTable: "Layers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApprovedBatchItems",
                schema: "PricingTool",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ApprovedBatchId = table.Column<long>(type: "bigint", nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProductName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Brand = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Band = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    OldPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PrePushPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PushedPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Pptcv = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    StockAtPush = table.Column<int>(type: "int", nullable: false),
                    SoldUnits = table.Column<int>(type: "int", nullable: false),
                    SoldNet = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovedBatchItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApprovedBatchItems_ApprovedBatches_ApprovedBatchId",
                        column: x => x.ApprovedBatchId,
                        principalSchema: "PricingTool",
                        principalTable: "ApprovedBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovedBatches_LayerId_PushedUtc",
                schema: "PricingTool",
                table: "ApprovedBatches",
                columns: new[] { "LayerId", "PushedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovedBatchItems_ApprovedBatchId_Sku",
                schema: "PricingTool",
                table: "ApprovedBatchItems",
                columns: new[] { "ApprovedBatchId", "Sku" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApprovedBatchItems",
                schema: "PricingTool");

            migrationBuilder.DropTable(
                name: "ApprovedBatches",
                schema: "PricingTool");
        }
    }
}
