using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveRetiredAlgorithmBandSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Delete orphaned per-band algorithm settings left behind when algorithms were retired
            // (notably MARGIN_TIER): the code + DI registration were removed but the BandAlgorithmSettings
            // rows were not, so the Bands list counted them (e.g. "5 / 5" instead of "4 / 4"). They never
            // affected pricing — the engine only evaluates registered algorithms — this is data hygiene.
            migrationBuilder.Sql(@"
DELETE FROM [PricingTool].[BandAlgorithmSettings]
WHERE [AlgorithmCode] NOT IN ('SELL_THROUGH', 'ELASTICITY', 'DEAD_STOCK', 'CROSS_DOCK');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Irreversible: the retired settings (weights/enabled flags) are not recoverable. No-op.
        }
    }
}
