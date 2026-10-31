using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Agent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class CampaignTerms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DepthCapLiftedBy",
                schema: "ai",
                table: "CampaignCalendar",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DiscountOptimizationOff",
                schema: "ai",
                table: "CampaignCalendar",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DiscountOptimizationSwitchedOffUtc",
                schema: "ai",
                table: "CampaignCalendar",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EverySkuInStock",
                schema: "ai",
                table: "CampaignCalendar",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DepthCapLiftedBy",
                schema: "ai",
                table: "CampaignCalendar");

            migrationBuilder.DropColumn(
                name: "DiscountOptimizationOff",
                schema: "ai",
                table: "CampaignCalendar");

            migrationBuilder.DropColumn(
                name: "DiscountOptimizationSwitchedOffUtc",
                schema: "ai",
                table: "CampaignCalendar");

            migrationBuilder.DropColumn(
                name: "EverySkuInStock",
                schema: "ai",
                table: "CampaignCalendar");
        }
    }
}
