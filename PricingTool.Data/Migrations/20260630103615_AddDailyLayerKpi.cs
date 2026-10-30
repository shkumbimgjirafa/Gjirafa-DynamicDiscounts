using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDailyLayerKpi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DailyLayerKpi",
                schema: "PricingTool",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LayerId = table.Column<int>(type: "int", nullable: false),
                    Date = table.Column<DateTime>(type: "date", nullable: false),
                    Net7 = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Qty7 = table.Column<long>(type: "bigint", nullable: false),
                    CostedNet7 = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Cost7 = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    SkuCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyLayerKpi", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DailyLayerKpi_Layers_LayerId",
                        column: x => x.LayerId,
                        principalSchema: "PricingTool",
                        principalTable: "Layers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DailyLayerKpi_LayerId_Date",
                schema: "PricingTool",
                table: "DailyLayerKpi",
                columns: new[] { "LayerId", "Date" },
                unique: true);

            // One-time backfill from the existing DailySnapshots so the dashboard's full trend (and its
            // earliest = pre-tool baseline) survives the very first run after deploy and any later
            // snapshot retention. Reproduces HomeController's old per-(layer, date) GROUP BY exactly:
            // Σ Net7, Σ Qty7, and — over SKUs whose cost (Pptcv) is known — Σ Net7 (costable revenue)
            // and Σ Pptcv·Qty7 (their cost). DailySnapshots is unique per (LayerId, SnapshotDate, Sku),
            // so one rollup row per (layer, date) with no ties. One-time pass; may sort in tempdb.
            migrationBuilder.Sql(@"
INSERT INTO [PricingTool].[DailyLayerKpi]
    ([LayerId], [Date], [Net7], [Qty7], [CostedNet7], [Cost7], [SkuCount])
SELECT
    s.[LayerId],
    s.[SnapshotDate],
    SUM(s.[Net7]),
    SUM(CAST(s.[Qty7] AS bigint)),
    SUM(CASE WHEN s.[Pptcv] IS NOT NULL THEN s.[Net7] ELSE 0 END),
    SUM(CASE WHEN s.[Pptcv] IS NOT NULL THEN s.[Pptcv] * s.[Qty7] ELSE 0 END),
    COUNT(*)
FROM [PricingTool].[DailySnapshots] s
GROUP BY s.[LayerId], s.[SnapshotDate];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyLayerKpi",
                schema: "PricingTool");
        }
    }
}
