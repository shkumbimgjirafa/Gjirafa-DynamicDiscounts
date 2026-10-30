using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PricingTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLayerIgnoreNewProducts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Default TRUE so every existing layer keeps today's behaviour on deploy — platform-new
            // products stay ignored (held at current price) unless a Manager opts a layer into pricing them.
            migrationBuilder.AddColumn<bool>(
                name: "IgnoreNewProducts",
                schema: "PricingTool",
                table: "Layers",
                type: "bit",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IgnoreNewProducts",
                schema: "PricingTool",
                table: "Layers");
        }
    }
}
