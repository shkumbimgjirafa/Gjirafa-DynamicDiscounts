using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropUnreadSnapshotColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Disc14",
                schema: "PricingTool",
                table: "DailySnapshots");

            migrationBuilder.DropColumn(
                name: "Disc30",
                schema: "PricingTool",
                table: "DailySnapshots");

            migrationBuilder.DropColumn(
                name: "Disc60",
                schema: "PricingTool",
                table: "DailySnapshots");

            migrationBuilder.DropColumn(
                name: "Disc7",
                schema: "PricingTool",
                table: "DailySnapshots");

            migrationBuilder.DropColumn(
                name: "Disc90",
                schema: "PricingTool",
                table: "DailySnapshots");

            migrationBuilder.DropColumn(
                name: "LaunchDateUtc",
                schema: "PricingTool",
                table: "DailySnapshots");

            migrationBuilder.DropColumn(
                name: "Net14",
                schema: "PricingTool",
                table: "DailySnapshots");

            migrationBuilder.DropColumn(
                name: "Net60",
                schema: "PricingTool",
                table: "DailySnapshots");

            migrationBuilder.DropColumn(
                name: "PulledAtUtc",
                schema: "PricingTool",
                table: "DailySnapshots");

            migrationBuilder.DropColumn(
                name: "Qty14",
                schema: "PricingTool",
                table: "DailySnapshots");

            migrationBuilder.DropColumn(
                name: "Qty60",
                schema: "PricingTool",
                table: "DailySnapshots");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Disc14",
                schema: "PricingTool",
                table: "DailySnapshots",
                type: "decimal(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Disc30",
                schema: "PricingTool",
                table: "DailySnapshots",
                type: "decimal(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Disc60",
                schema: "PricingTool",
                table: "DailySnapshots",
                type: "decimal(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Disc7",
                schema: "PricingTool",
                table: "DailySnapshots",
                type: "decimal(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Disc90",
                schema: "PricingTool",
                table: "DailySnapshots",
                type: "decimal(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LaunchDateUtc",
                schema: "PricingTool",
                table: "DailySnapshots",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Net14",
                schema: "PricingTool",
                table: "DailySnapshots",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "Net60",
                schema: "PricingTool",
                table: "DailySnapshots",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "PulledAtUtc",
                schema: "PricingTool",
                table: "DailySnapshots",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<int>(
                name: "Qty14",
                schema: "PricingTool",
                table: "DailySnapshots",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Qty60",
                schema: "PricingTool",
                table: "DailySnapshots",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }
    }
}
