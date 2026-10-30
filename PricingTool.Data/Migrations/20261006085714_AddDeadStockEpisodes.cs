using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeadStockEpisodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeadStockDailyKpi",
                schema: "PricingTool",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CountryCode = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Date = table.Column<DateTime>(type: "date", nullable: false),
                    PrevDate = table.Column<DateTime>(type: "date", nullable: true),
                    DeadSkus = table.Column<int>(type: "int", nullable: false),
                    DeadUnits = table.Column<long>(type: "bigint", nullable: false),
                    DeadCost = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Stage1Skus = table.Column<int>(type: "int", nullable: false),
                    Stage2Skus = table.Column<int>(type: "int", nullable: false),
                    Stage3Skus = table.Column<int>(type: "int", nullable: false),
                    Stage4Skus = table.Column<int>(type: "int", nullable: false),
                    EnteredSkus = table.Column<int>(type: "int", nullable: false),
                    LeftSkus = table.Column<int>(type: "int", nullable: false),
                    LeftCleared = table.Column<int>(type: "int", nullable: false),
                    LeftRevived = table.Column<int>(type: "int", nullable: false),
                    LeftRestocked = table.Column<int>(type: "int", nullable: false),
                    LeftDelisted = table.Column<int>(type: "int", nullable: false),
                    LeftOther = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeadStockDailyKpi", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeadStockEpisodes",
                schema: "PricingTool",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LayerId = table.Column<int>(type: "int", nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EnteredDate = table.Column<DateTime>(type: "date", nullable: false),
                    EntryStage = table.Column<byte>(type: "tinyint", nullable: false),
                    EntryLocalStock = table.Column<int>(type: "int", nullable: false),
                    EntryPptcv = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    EntryPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    MaxStage = table.Column<byte>(type: "tinyint", nullable: false),
                    LastSeenDate = table.Column<DateTime>(type: "date", nullable: false),
                    LastStage = table.Column<byte>(type: "tinyint", nullable: false),
                    LastLocalStock = table.Column<int>(type: "int", nullable: false),
                    LastPptcv = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    LastPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    ExitedDate = table.Column<DateTime>(type: "date", nullable: true),
                    ExitReason = table.Column<byte>(type: "tinyint", nullable: false),
                    ExitLocalStock = table.Column<int>(type: "int", nullable: true),
                    ExitQty90 = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeadStockEpisodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeadStockEpisodes_Layers_LayerId",
                        column: x => x.LayerId,
                        principalSchema: "PricingTool",
                        principalTable: "Layers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeadStockDailyKpi_CountryCode_Date",
                schema: "PricingTool",
                table: "DeadStockDailyKpi",
                columns: new[] { "CountryCode", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeadStockEpisodes_LayerId_ExitedDate",
                schema: "PricingTool",
                table: "DeadStockEpisodes",
                columns: new[] { "LayerId", "ExitedDate" });

            migrationBuilder.CreateIndex(
                name: "IX_DeadStockEpisodes_LayerId_Sku_EnteredDate",
                schema: "PricingTool",
                table: "DeadStockEpisodes",
                columns: new[] { "LayerId", "Sku", "EnteredDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeadStockDailyKpi",
                schema: "PricingTool");

            migrationBuilder.DropTable(
                name: "DeadStockEpisodes",
                schema: "PricingTool");
        }
    }
}
