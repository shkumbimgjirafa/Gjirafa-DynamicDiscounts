using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Agent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddPublishAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PublishBatch",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LayerId = table.Column<int>(type: "int", nullable: false),
                    Db = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    StoreId = table.Column<int>(type: "int", nullable: false),
                    Target = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ShopSchema = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CampaignKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    EngineRunId = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FinishedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RolledBackUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Requested = table.Column<int>(type: "int", nullable: false),
                    Created = table.Column<int>(type: "int", nullable: false),
                    Updated = table.Column<int>(type: "int", nullable: false),
                    Unchanged = table.Column<int>(type: "int", nullable: false),
                    Skipped = table.Column<int>(type: "int", nullable: false),
                    RoundTripChecked = table.Column<int>(type: "int", nullable: false),
                    RoundTripMismatches = table.Column<int>(type: "int", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishBatch", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PublishAudit",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BatchId = table.Column<long>(type: "bigint", nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: true),
                    Change = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DiscountId = table.Column<int>(type: "int", nullable: true),
                    DiscountName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    OldDiscountAmount = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    OldStartDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    OldEndDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NewDiscountAmount = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    NewStartDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NewEndDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    OldHasDiscountsApplied = table.Column<bool>(type: "bit", nullable: true),
                    OldStoreIds = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    NewStoreIds = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Applied = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RolledBackUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishAudit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublishAudit_PublishBatch_BatchId",
                        column: x => x.BatchId,
                        principalSchema: "ai",
                        principalTable: "PublishBatch",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PublishException",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BatchId = table.Column<long>(type: "bigint", nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishException", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublishException_PublishBatch_BatchId",
                        column: x => x.BatchId,
                        principalSchema: "ai",
                        principalTable: "PublishBatch",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "ai",
                table: "TimeSheetTaskType",
                columns: new[] { "Id", "MinutesBefore", "Name" },
                values: new object[] { 2, null, "Create and push the discounts for a run" });

            migrationBuilder.CreateIndex(
                name: "IX_PublishAudit_BatchId",
                schema: "ai",
                table: "PublishAudit",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_PublishBatch_LayerId_StartedUtc",
                schema: "ai",
                table: "PublishBatch",
                columns: new[] { "LayerId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PublishException_BatchId",
                schema: "ai",
                table: "PublishException",
                column: "BatchId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublishAudit",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "PublishException",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "PublishBatch",
                schema: "ai");

            migrationBuilder.DeleteData(
                schema: "ai",
                table: "TimeSheetTaskType",
                keyColumn: "Id",
                keyValue: 2);
        }
    }
}
