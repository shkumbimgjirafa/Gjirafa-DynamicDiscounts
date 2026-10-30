using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSkuDeadStockState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SkuDeadStockStates",
                schema: "PricingTool",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LayerId = table.Column<int>(type: "int", nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ZeroSaleStreakDays = table.Column<int>(type: "int", nullable: false),
                    PrevStreakDays = table.Column<int>(type: "int", nullable: false),
                    LastSnapshotDate = table.Column<DateTime>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SkuDeadStockStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SkuDeadStockStates_Layers_LayerId",
                        column: x => x.LayerId,
                        principalSchema: "PricingTool",
                        principalTable: "Layers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SkuDeadStockStates_LayerId_Sku",
                schema: "PricingTool",
                table: "SkuDeadStockStates",
                columns: new[] { "LayerId", "Sku" },
                unique: true);

            // One-time backfill from the existing DailySnapshots so the counter is correct on the very
            // first run after deploy (otherwise every streak would restart at 0 and dead-stock markdown
            // depth would be lost). Reproduces the old backward scan exactly, set-based:
            //   * SalesUpTo groups each SKU's rows into runs since the last sale (gaps-and-islands).
            //   * Streak per row = position in that run, minus the leading sale row (if any).
            //   * Take the streak at the latest date (ZeroSaleStreakDays) and the date before it
            //     (PrevStreakDays — keeps a same-UTC-date re-pull right after deploy idempotent).
            // DailySnapshots is unique per (LayerId, SnapshotDate, Sku), so there are no same-date ties.
            // This is a one-time pass; on a large table it may sort in tempdb, which is acceptable here.
            migrationBuilder.Sql(@"
WITH ranked AS (
    SELECT
        LayerId, Sku, SnapshotDate, Qty7,
        SUM(CASE WHEN Qty7 <> 0 THEN 1 ELSE 0 END)
            OVER (PARTITION BY LayerId, Sku ORDER BY SnapshotDate ROWS UNBOUNDED PRECEDING) AS SalesUpTo,
        ROW_NUMBER() OVER (PARTITION BY LayerId, Sku ORDER BY SnapshotDate DESC) AS RnDesc
    FROM [PricingTool].[DailySnapshots]
),
streaks AS (
    SELECT
        LayerId, Sku, SnapshotDate, RnDesc,
        ROW_NUMBER() OVER (PARTITION BY LayerId, Sku, SalesUpTo ORDER BY SnapshotDate)
            - CASE WHEN SalesUpTo > 0 THEN 1 ELSE 0 END AS Streak
    FROM ranked
),
pick AS (
    SELECT
        LayerId, Sku,
        MAX(CASE WHEN RnDesc = 1 THEN SnapshotDate END) AS LastSnapshotDate,
        MAX(CASE WHEN RnDesc = 1 THEN Streak END)       AS CurrentStreak,
        MAX(CASE WHEN RnDesc = 2 THEN Streak END)       AS PrevStreak
    FROM streaks
    WHERE RnDesc <= 2
    GROUP BY LayerId, Sku
)
INSERT INTO [PricingTool].[SkuDeadStockStates]
    ([LayerId], [Sku], [ZeroSaleStreakDays], [PrevStreakDays], [LastSnapshotDate])
SELECT LayerId, Sku, CurrentStreak, ISNULL(PrevStreak, 0), LastSnapshotDate
FROM pick;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SkuDeadStockStates",
                schema: "PricingTool");
        }
    }
}
