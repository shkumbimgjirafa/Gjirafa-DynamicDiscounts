using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLayers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Drop the unique indexes that are being widened to include LayerId.
            migrationBuilder.DropIndex(
                name: "IX_SkuOverrides_Sku",
                schema: "PricingTool",
                table: "SkuOverrides");

            migrationBuilder.DropIndex(
                name: "IX_DailySnapshots_SnapshotDate_Sku",
                schema: "PricingTool",
                table: "DailySnapshots");

            // 2) Rename the store-specific stock column to a layer-agnostic name.
            migrationBuilder.RenameColumn(
                name: "KsWarehouseStock",
                schema: "PricingTool",
                table: "DailySnapshots",
                newName: "LocalWarehouseStock");

            // 3) Create the Layers table FIRST and seed the canonical six layers (KS = Id 1) so
            //    existing rows can be backfilled to a real layer before any FK is added.
            migrationBuilder.CreateTable(
                name: "Layers",
                schema: "PricingTool",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Brand = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CountryCode = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OperationalDatabase = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    StoreId = table.Column<int>(type: "int", nullable: false),
                    TranslationCountryId = table.Column<int>(type: "int", nullable: false),
                    WarehouseStoreId = table.Column<int>(type: "int", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    FilterVendors = table.Column<bool>(type: "bit", nullable: false),
                    ExcludeUnpublished = table.Column<bool>(type: "bit", nullable: false),
                    RunTimeUtc = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    CadenceHours = table.Column<int>(type: "int", nullable: false),
                    LastScheduledRunUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Layers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Layers_Brand_CountryCode",
                schema: "PricingTool",
                table: "Layers",
                columns: new[] { "Brand", "CountryCode" },
                unique: true);

            migrationBuilder.InsertData(
                schema: "PricingTool",
                table: "Layers",
                columns: new[] { "Id", "Brand", "CountryCode", "DisplayName", "OperationalDatabase", "StoreId", "TranslationCountryId", "WarehouseStoreId", "Currency", "FilterVendors", "ExcludeUnpublished", "RunTimeUtc", "CadenceHours", "LastScheduledRunUtc", "IsActive", "SortOrder" },
                values: new object[,]
                {
                    { 1, "GjirafaMall", "KS", "GjirafaMall — Kosovo",          "GjirafaMall",      2, 1, 2, "EUR", true,  true, "03:00", 24, null, true, 0 },
                    { 2, "GjirafaMall", "MK", "GjirafaMall — North Macedonia", "GjirafaMall",      1, 3, 1, "MKD", true,  true, "03:00", 24, null, true, 1 },
                    { 3, "GjirafaMall", "AL", "GjirafaMall — Albania",         "GjirafaMall",      3, 2, 3, "ALL", true,  true, "03:00", 24, null, true, 2 },
                    { 4, "Gjirafa50",   "KS", "Gjirafa50 — Kosovo",            "GjirafaEcommerce", 2, 1, 2, "EUR", false, true, "03:00", 24, null, true, 3 },
                    { 5, "Gjirafa50",   "MK", "Gjirafa50 — North Macedonia",   "GjirafaEcommerce", 1, 3, 1, "MKD", false, true, "03:00", 24, null, true, 4 },
                    { 6, "Gjirafa50",   "AL", "Gjirafa50 — Albania",           "GjirafaEcommerce", 3, 2, 3, "ALL", false, true, "03:00", 24, null, true, 5 },
                });

            // 4) Add LayerId columns nullable, backfill existing rows to the KS layer (Id 1), then
            //    tighten the non-null tables to NOT NULL. AuditLog stays nullable (global entries).
            foreach (var t in new[] { "SkuOverrides", "ProposedPrices", "PricingRuns", "PriceChangeOutcomes", "PriceBands", "DailySnapshots" })
            {
                migrationBuilder.AddColumn<int>(
                    name: "LayerId", schema: "PricingTool", table: t, type: "int", nullable: true);
                migrationBuilder.Sql($"UPDATE [PricingTool].[{t}] SET [LayerId] = 1 WHERE [LayerId] IS NULL;");
                migrationBuilder.AlterColumn<int>(
                    name: "LayerId", schema: "PricingTool", table: t, type: "int",
                    nullable: false, oldClrType: typeof(int), oldType: "int", oldNullable: true);
            }

            migrationBuilder.AddColumn<int>(
                name: "LayerId",
                schema: "PricingTool",
                table: "AuditLog",
                type: "int",
                nullable: true);

            // 5) Recreate the widened indexes (existing rows all carry LayerId = 1, so the
            //    composite uniques hold wherever the old (SnapshotDate, Sku) / (Sku) uniques held).
            migrationBuilder.CreateIndex(
                name: "IX_SkuOverrides_LayerId_Sku",
                schema: "PricingTool",
                table: "SkuOverrides",
                columns: new[] { "LayerId", "Sku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProposedPrices_LayerId",
                schema: "PricingTool",
                table: "ProposedPrices",
                column: "LayerId");

            migrationBuilder.CreateIndex(
                name: "IX_PricingRuns_LayerId_Status",
                schema: "PricingTool",
                table: "PricingRuns",
                columns: new[] { "LayerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PriceChangeOutcomes_LayerId",
                schema: "PricingTool",
                table: "PriceChangeOutcomes",
                column: "LayerId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceBands_LayerId",
                schema: "PricingTool",
                table: "PriceBands",
                column: "LayerId");

            migrationBuilder.CreateIndex(
                name: "IX_DailySnapshots_LayerId_SnapshotDate_Sku",
                schema: "PricingTool",
                table: "DailySnapshots",
                columns: new[] { "LayerId", "SnapshotDate", "Sku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_LayerId",
                schema: "PricingTool",
                table: "AuditLog",
                column: "LayerId");

            // 6) Add the FKs now that every LayerId points at a real Layer row.
            migrationBuilder.AddForeignKey(
                name: "FK_AuditLog_Layers_LayerId",
                schema: "PricingTool",
                table: "AuditLog",
                column: "LayerId",
                principalSchema: "PricingTool",
                principalTable: "Layers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DailySnapshots_Layers_LayerId",
                schema: "PricingTool",
                table: "DailySnapshots",
                column: "LayerId",
                principalSchema: "PricingTool",
                principalTable: "Layers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PriceBands_Layers_LayerId",
                schema: "PricingTool",
                table: "PriceBands",
                column: "LayerId",
                principalSchema: "PricingTool",
                principalTable: "Layers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PriceChangeOutcomes_Layers_LayerId",
                schema: "PricingTool",
                table: "PriceChangeOutcomes",
                column: "LayerId",
                principalSchema: "PricingTool",
                principalTable: "Layers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PricingRuns_Layers_LayerId",
                schema: "PricingTool",
                table: "PricingRuns",
                column: "LayerId",
                principalSchema: "PricingTool",
                principalTable: "Layers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ProposedPrices_Layers_LayerId",
                schema: "PricingTool",
                table: "ProposedPrices",
                column: "LayerId",
                principalSchema: "PricingTool",
                principalTable: "Layers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SkuOverrides_Layers_LayerId",
                schema: "PricingTool",
                table: "SkuOverrides",
                column: "LayerId",
                principalSchema: "PricingTool",
                principalTable: "Layers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuditLog_Layers_LayerId",
                schema: "PricingTool",
                table: "AuditLog");

            migrationBuilder.DropForeignKey(
                name: "FK_DailySnapshots_Layers_LayerId",
                schema: "PricingTool",
                table: "DailySnapshots");

            migrationBuilder.DropForeignKey(
                name: "FK_PriceBands_Layers_LayerId",
                schema: "PricingTool",
                table: "PriceBands");

            migrationBuilder.DropForeignKey(
                name: "FK_PriceChangeOutcomes_Layers_LayerId",
                schema: "PricingTool",
                table: "PriceChangeOutcomes");

            migrationBuilder.DropForeignKey(
                name: "FK_PricingRuns_Layers_LayerId",
                schema: "PricingTool",
                table: "PricingRuns");

            migrationBuilder.DropForeignKey(
                name: "FK_ProposedPrices_Layers_LayerId",
                schema: "PricingTool",
                table: "ProposedPrices");

            migrationBuilder.DropForeignKey(
                name: "FK_SkuOverrides_Layers_LayerId",
                schema: "PricingTool",
                table: "SkuOverrides");

            migrationBuilder.DropTable(
                name: "Layers",
                schema: "PricingTool");

            migrationBuilder.DropIndex(
                name: "IX_SkuOverrides_LayerId_Sku",
                schema: "PricingTool",
                table: "SkuOverrides");

            migrationBuilder.DropIndex(
                name: "IX_ProposedPrices_LayerId",
                schema: "PricingTool",
                table: "ProposedPrices");

            migrationBuilder.DropIndex(
                name: "IX_PricingRuns_LayerId_Status",
                schema: "PricingTool",
                table: "PricingRuns");

            migrationBuilder.DropIndex(
                name: "IX_PriceChangeOutcomes_LayerId",
                schema: "PricingTool",
                table: "PriceChangeOutcomes");

            migrationBuilder.DropIndex(
                name: "IX_PriceBands_LayerId",
                schema: "PricingTool",
                table: "PriceBands");

            migrationBuilder.DropIndex(
                name: "IX_DailySnapshots_LayerId_SnapshotDate_Sku",
                schema: "PricingTool",
                table: "DailySnapshots");

            migrationBuilder.DropIndex(
                name: "IX_AuditLog_LayerId",
                schema: "PricingTool",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "LayerId",
                schema: "PricingTool",
                table: "SkuOverrides");

            migrationBuilder.DropColumn(
                name: "LayerId",
                schema: "PricingTool",
                table: "ProposedPrices");

            migrationBuilder.DropColumn(
                name: "LayerId",
                schema: "PricingTool",
                table: "PricingRuns");

            migrationBuilder.DropColumn(
                name: "LayerId",
                schema: "PricingTool",
                table: "PriceChangeOutcomes");

            migrationBuilder.DropColumn(
                name: "LayerId",
                schema: "PricingTool",
                table: "PriceBands");

            migrationBuilder.DropColumn(
                name: "LayerId",
                schema: "PricingTool",
                table: "DailySnapshots");

            migrationBuilder.DropColumn(
                name: "LayerId",
                schema: "PricingTool",
                table: "AuditLog");

            migrationBuilder.RenameColumn(
                name: "LocalWarehouseStock",
                schema: "PricingTool",
                table: "DailySnapshots",
                newName: "KsWarehouseStock");

            migrationBuilder.CreateIndex(
                name: "IX_SkuOverrides_Sku",
                schema: "PricingTool",
                table: "SkuOverrides",
                column: "Sku",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DailySnapshots_SnapshotDate_Sku",
                schema: "PricingTool",
                table: "DailySnapshots",
                columns: new[] { "SnapshotDate", "Sku" },
                unique: true);
        }
    }
}
