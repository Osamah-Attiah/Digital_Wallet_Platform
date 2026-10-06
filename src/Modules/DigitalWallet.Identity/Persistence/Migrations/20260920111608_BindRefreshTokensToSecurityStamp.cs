using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DigitalWallet.Identity.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BindRefreshTokensToSecurityStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SecurityStamp",
                schema: "identity",
                table: "refresh_tokens",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SecurityStamp",
                schema: "identity",
                table: "refresh_tokens");
        }
    }
}
