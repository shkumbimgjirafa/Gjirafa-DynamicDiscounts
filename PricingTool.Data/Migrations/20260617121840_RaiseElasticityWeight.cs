using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class RaiseElasticityWeight : Migration
    {
        /// <summary>
        /// Raise the ELASTICITY default per-band weight 50 → 80 (it now actively sets the profit-max
        /// price, so it's one of the strongest signals). Only bumps bands still on the old default of
        /// 50, so any hand-tuned weight is preserved.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE [PricingTool].[BandAlgorithmSettings] SET [Weight] = 80 WHERE [AlgorithmCode] = 'ELASTICITY' AND [Weight] = 50;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE [PricingTool].[BandAlgorithmSettings] SET [Weight] = 50 WHERE [AlgorithmCode] = 'ELASTICITY' AND [Weight] = 80;");
        }
    }
}
