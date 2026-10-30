using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Agent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignCalendar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CampaignCalendar",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LayerId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ScopeKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ScopeValue = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    GoalType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    GoalValue = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SourceCampaignId = table.Column<int>(type: "int", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignCalendar", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignCalendar_LayerId_SourceCampaignId",
                schema: "ai",
                table: "CampaignCalendar",
                columns: new[] { "LayerId", "SourceCampaignId" },
                unique: true,
                filter: "[SourceCampaignId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignCalendar_LayerId_StartDate",
                schema: "ai",
                table: "CampaignCalendar",
                columns: new[] { "LayerId", "StartDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CampaignCalendar",
                schema: "ai");
        }
    }
}
