using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveDiscountEffectiveness : Migration
    {
        /// <summary>
        /// Retire DISCOUNT_EFFECTIVENESS (roster 6 → 5). It was a crude heuristic (raw velocity vs
        /// today's shelf discount) that ignored the discount actually in effect during the window and
        /// over-fired on near-dead items; its job is left to the fitted ELASTICITY signal + the margin
        /// floor. Removes its per-band settings from existing DBs. Historical AlgorithmVotes keep the
        /// DISCOUNT_WASTED reason code (not touched).
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DELETE FROM [PricingTool].[BandAlgorithmSettings] WHERE [AlgorithmCode] = 'DISCOUNT_EFFECTIVENESS';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
INSERT INTO [PricingTool].[BandAlgorithmSettings] ([PriceBandId],[AlgorithmCode],[Enabled],[Weight])
SELECT b.[Id], 'DISCOUNT_EFFECTIVENESS', 1, 65
FROM [PricingTool].[PriceBands] b
WHERE NOT EXISTS (
    SELECT 1 FROM [PricingTool].[BandAlgorithmSettings] s
    WHERE s.[PriceBandId] = b.[Id] AND s.[AlgorithmCode] = 'DISCOUNT_EFFECTIVENESS');");
        }
    }
}
