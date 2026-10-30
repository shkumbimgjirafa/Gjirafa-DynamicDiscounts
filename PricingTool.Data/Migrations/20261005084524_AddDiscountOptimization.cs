using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscountOptimization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Default FALSE so every existing layer keeps today's behaviour on deploy — no trim is applied
            // until a Manager ticks "Discount optimization" for a layer on the Schedule page. The trim table
            // itself is seeded per layer at startup by DbSeeder (inputs only; it moves no price while off).
            migrationBuilder.AddColumn<bool>(
                name: "DiscountOptimizationEnabled",
                schema: "PricingTool",
                table: "Layers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "DiscountTrimRules",
                schema: "PricingTool",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LayerId = table.Column<int>(type: "int", nullable: false),
                    MinDiscountPct = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    MaxDiscountPct = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    TrimPct = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscountTrimRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscountTrimRules_Layers_LayerId",
                        column: x => x.LayerId,
                        principalSchema: "PricingTool",
                        principalTable: "Layers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiscountTrimRules_LayerId",
                schema: "PricingTool",
                table: "DiscountTrimRules",
                column: "LayerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscountTrimRules",
                schema: "PricingTool");

            migrationBuilder.DropColumn(
                name: "DiscountOptimizationEnabled",
                schema: "PricingTool",
                table: "Layers");
        }
    }
}
