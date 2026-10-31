using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Agent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddDecisionReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReportNote",
                schema: "ai",
                table: "AgentDecision",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReportedTo",
                schema: "ai",
                table: "AgentDecision",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReportedUtc",
                schema: "ai",
                table: "AgentDecision",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReportNote",
                schema: "ai",
                table: "AgentDecision");

            migrationBuilder.DropColumn(
                name: "ReportedTo",
                schema: "ai",
                table: "AgentDecision");

            migrationBuilder.DropColumn(
                name: "ReportedUtc",
                schema: "ai",
                table: "AgentDecision");
        }
    }
}
