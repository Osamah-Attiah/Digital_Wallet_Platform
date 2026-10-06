using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DigitalWallet.Identity.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BindSessionsToAuthorizationVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AuthorizationVersion",
                schema: "identity",
                table: "refresh_tokens",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthorizationVersion",
                schema: "identity",
                table: "refresh_tokens");
        }
    }
}
