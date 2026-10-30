using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSkuElasticity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastElasticityFitUtc",
                schema: "PricingTool",
                table: "Layers",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SrCompanyId",
                schema: "PricingTool",
                table: "Layers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SrPlatformId",
                schema: "PricingTool",
                table: "Layers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "SkuElasticity",
                schema: "PricingTool",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LayerId = table.Column<int>(type: "int", nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Coefficient = table.Column<decimal>(type: "decimal(12,6)", precision: 12, scale: 6, nullable: false),
                    Intercept = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    R2 = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false),
                    ObservationCount = table.Column<int>(type: "int", nullable: false),
                    DistinctPricePoints = table.Column<int>(type: "int", nullable: false),
                    PriceCv = table.Column<decimal>(type: "decimal(12,6)", precision: 12, scale: 6, nullable: false),
                    IsUsable = table.Column<bool>(type: "bit", nullable: false),
                    FittedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SkuElasticity", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SkuElasticity_Layers_LayerId",
                        column: x => x.LayerId,
                        principalSchema: "PricingTool",
                        principalTable: "Layers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SkuElasticity_LayerId_IsUsable",
                schema: "PricingTool",
                table: "SkuElasticity",
                columns: new[] { "LayerId", "IsUsable" });

            migrationBuilder.CreateIndex(
                name: "IX_SkuElasticity_LayerId_Sku",
                schema: "PricingTool",
                table: "SkuElasticity",
                columns: new[] { "LayerId", "Sku" },
                unique: true);

            // Backfill SR_ProductsData scoping for existing layers (the new columns default to 0).
            migrationBuilder.Sql(@"
UPDATE [PricingTool].[Layers] SET [SrPlatformId]=2, [SrCompanyId]=0  WHERE [Brand]='GjirafaMall' AND [CountryCode]='KS';
UPDATE [PricingTool].[Layers] SET [SrPlatformId]=2, [SrCompanyId]=1  WHERE [Brand]='GjirafaMall' AND [CountryCode]='MK';
UPDATE [PricingTool].[Layers] SET [SrPlatformId]=2, [SrCompanyId]=3  WHERE [Brand]='GjirafaMall' AND [CountryCode]='AL';
UPDATE [PricingTool].[Layers] SET [SrPlatformId]=1, [SrCompanyId]=1  WHERE [Brand]='Gjirafa50'   AND [CountryCode]='KS';
UPDATE [PricingTool].[Layers] SET [SrPlatformId]=3, [SrCompanyId]=2  WHERE [Brand]='Gjirafa50'   AND [CountryCode]='MK';
UPDATE [PricingTool].[Layers] SET [SrPlatformId]=1, [SrCompanyId]=19 WHERE [Brand]='Gjirafa50'   AND [CountryCode]='AL';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SkuElasticity",
                schema: "PricingTool");

            migrationBuilder.DropColumn(
                name: "LastElasticityFitUtc",
                schema: "PricingTool",
                table: "Layers");

            migrationBuilder.DropColumn(
                name: "SrCompanyId",
                schema: "PricingTool",
                table: "Layers");

            migrationBuilder.DropColumn(
                name: "SrPlatformId",
                schema: "PricingTool",
                table: "Layers");
        }
    }
}
