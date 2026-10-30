using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPriceChangeOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PriceChangeOutcomes",
                schema: "PricingTool",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProposedPriceId = table.Column<long>(type: "bigint", nullable: true),
                    Sku = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceRunId = table.Column<long>(type: "bigint", nullable: false),
                    PriceBandId = table.Column<int>(type: "int", nullable: true),
                    AppliedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Direction = table.Column<int>(type: "int", nullable: false),
                    Intent = table.Column<int>(type: "int", nullable: false),
                    OldPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    NewPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    WindowDays = table.Column<int>(type: "int", nullable: false),
                    PreUnitsPerDay = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: false),
                    PreMarginPct = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    PreGrossProfitPerDay = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    PostUnitsPerDay = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: true),
                    PostMarginPct = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    PostGrossProfitPerDay = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Verdict = table.Column<int>(type: "int", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    MeasuredUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MeasuredOnRunId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceChangeOutcomes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceChangeOutcomes_ProposedPrices_ProposedPriceId",
                        column: x => x.ProposedPriceId,
                        principalSchema: "PricingTool",
                        principalTable: "ProposedPrices",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_PriceChangeOutcomes_AppliedUtc",
                schema: "PricingTool",
                table: "PriceChangeOutcomes",
                column: "AppliedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PriceChangeOutcomes_ProposedPriceId",
                schema: "PricingTool",
                table: "PriceChangeOutcomes",
                column: "ProposedPriceId",
                unique: true,
                filter: "[ProposedPriceId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PriceChangeOutcomes_Verdict",
                schema: "PricingTool",
                table: "PriceChangeOutcomes",
                column: "Verdict");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PriceChangeOutcomes",
                schema: "PricingTool");
        }
    }
}
