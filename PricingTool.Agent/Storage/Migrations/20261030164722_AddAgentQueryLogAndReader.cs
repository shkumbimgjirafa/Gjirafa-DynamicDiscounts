using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Agent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentQueryLogAndReader : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CallTag",
                schema: "ai",
                table: "AiCallLog",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AgentQueryLog",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CalledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Agent = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CallTag = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Tool = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Request = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Accepted = table.Column<bool>(type: "bit", nullable: false),
                    RowCount = table.Column<int>(type: "int", nullable: true),
                    Truncated = table.Column<bool>(type: "bit", nullable: false),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentQueryLog", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiCallLog_CallTag",
                schema: "ai",
                table: "AiCallLog",
                column: "CallTag");

            migrationBuilder.CreateIndex(
                name: "IX_AgentQueryLog_CalledAtUtc",
                schema: "ai",
                table: "AgentQueryLog",
                column: "CalledAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AgentQueryLog_CallTag",
                schema: "ai",
                table: "AgentQueryLog",
                column: "CallTag");

            // The user every query_data call runs as: no login, SELECT on the agent views only. Ownership
            // chaining (views and tables all owned by dbo) lets the views read their tables without any
            // grant on PricingTool.* or hk.*. The views themselves are created by the data-prep script
            // (docs/hackathon-2026/exports/agent_views.sql); the schema is made here if it isn't there yet.
            migrationBuilder.Sql("IF SCHEMA_ID('agent') IS NULL EXEC('CREATE SCHEMA agent');");
            migrationBuilder.Sql("IF DATABASE_PRINCIPAL_ID('agent_reader') IS NULL CREATE USER agent_reader WITHOUT LOGIN;");
            migrationBuilder.Sql("GRANT SELECT ON SCHEMA::agent TO agent_reader;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF DATABASE_PRINCIPAL_ID('agent_reader') IS NOT NULL DROP USER agent_reader;");

            migrationBuilder.DropTable(
                name: "AgentQueryLog",
                schema: "ai");

            migrationBuilder.DropIndex(
                name: "IX_AiCallLog_CallTag",
                schema: "ai",
                table: "AiCallLog");

            migrationBuilder.DropColumn(
                name: "CallTag",
                schema: "ai",
                table: "AiCallLog");
        }
    }
}
