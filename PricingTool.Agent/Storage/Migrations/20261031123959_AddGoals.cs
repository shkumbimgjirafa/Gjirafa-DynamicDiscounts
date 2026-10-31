using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Agent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddGoals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Goal",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LayerId = table.Column<int>(type: "int", nullable: false),
                    Period = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Start = table.Column<DateOnly>(type: "date", nullable: false),
                    End = table.Column<DateOnly>(type: "date", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Value = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    SetBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Instruction = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    MessageId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RetiredUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RetiredBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Goal", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Goal_LayerId_Start",
                schema: "ai",
                table: "Goal",
                columns: new[] { "LayerId", "Start" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Goal",
                schema: "ai");
        }
    }
}
