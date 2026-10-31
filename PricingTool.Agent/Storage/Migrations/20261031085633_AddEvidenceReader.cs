using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Agent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddEvidenceReader : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The user the Evidence page's live recomputes run as (Prompt 11): no login, read-only on every schema
            // (db_datareader), never the app's owner. The evidence scripts read hk.*, PricingTool.*, ai.*, agent.* and the
            // sandbox, and make only temp tables.
            migrationBuilder.Sql("IF DATABASE_PRINCIPAL_ID('evidence_reader') IS NULL CREATE USER evidence_reader WITHOUT LOGIN;");
            migrationBuilder.Sql("ALTER ROLE db_datareader ADD MEMBER evidence_reader;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF DATABASE_PRINCIPAL_ID('evidence_reader') IS NOT NULL DROP USER evidence_reader;");
        }
    }
}
