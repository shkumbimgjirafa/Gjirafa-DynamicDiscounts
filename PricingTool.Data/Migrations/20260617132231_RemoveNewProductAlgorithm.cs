using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveNewProductAlgorithm : Migration
    {
        /// <summary>
        /// NEW_PRODUCT is no longer a voting algorithm — new-product protection is now a hard engine
        /// rule driven by the platform MarkAsNew window (PriceCalculator short-circuit → price held,
        /// no discount). Remove its per-band settings so the Bands UI no longer lists it. Historical
        /// AlgorithmVotes / proposals keep the NEW_PRODUCT_PROTECTED reason code (not touched).
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DELETE FROM [PricingTool].[BandAlgorithmSettings] WHERE [AlgorithmCode] = 'NEW_PRODUCT';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
INSERT INTO [PricingTool].[BandAlgorithmSettings] ([PriceBandId],[AlgorithmCode],[Enabled],[Weight])
SELECT b.[Id], 'NEW_PRODUCT', 1, 90
FROM [PricingTool].[PriceBands] b
WHERE NOT EXISTS (
    SELECT 1 FROM [PricingTool].[BandAlgorithmSettings] s
    WHERE s.[PriceBandId] = b.[Id] AND s.[AlgorithmCode] = 'NEW_PRODUCT');");
        }
    }
}
