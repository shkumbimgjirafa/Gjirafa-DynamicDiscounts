using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPricingRunCategoryRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PricingRunCategoryRules",
                schema: "PricingTool",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PricingRunId = table.Column<long>(type: "bigint", nullable: false),
                    LayerId = table.Column<int>(type: "int", nullable: false),
                    BandId = table.Column<int>(type: "int", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MarginFloorPct = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    TargetMarginPct = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    Skus = table.Column<int>(type: "int", nullable: false),
                    CapturedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PricingRunCategoryRules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PricingRunCategoryRules_PricingRunId_BandId_Category",
                schema: "PricingTool",
                table: "PricingRunCategoryRules",
                columns: new[] { "PricingRunId", "BandId", "Category" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PricingRunCategoryRules",
                schema: "PricingTool");
        }
    }
}
