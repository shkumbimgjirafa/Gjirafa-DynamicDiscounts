using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Agent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanGoalVerdict : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Feasibility",
                schema: "ai",
                table: "CampaignPlan",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(500)",
                oldMaxLength: 500);

            migrationBuilder.AddColumn<string>(
                name: "GoalVerdict",
                schema: "ai",
                table: "CampaignPlan",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ReviewedBy",
                schema: "ai",
                table: "CampaignPlan",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewedUtc",
                schema: "ai",
                table: "CampaignPlan",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WaitsForPerson",
                schema: "ai",
                table: "CampaignPlan",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GoalVerdict",
                schema: "ai",
                table: "CampaignPlan");

            migrationBuilder.DropColumn(
                name: "ReviewedBy",
                schema: "ai",
                table: "CampaignPlan");

            migrationBuilder.DropColumn(
                name: "ReviewedUtc",
                schema: "ai",
                table: "CampaignPlan");

            migrationBuilder.DropColumn(
                name: "WaitsForPerson",
                schema: "ai",
                table: "CampaignPlan");

            migrationBuilder.AlterColumn<string>(
                name: "Feasibility",
                schema: "ai",
                table: "CampaignPlan",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(1000)",
                oldMaxLength: 1000);
        }
    }
}
