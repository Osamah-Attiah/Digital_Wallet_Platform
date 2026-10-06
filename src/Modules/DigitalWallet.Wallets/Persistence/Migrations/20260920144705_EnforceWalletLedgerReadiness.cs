using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DigitalWallet.Wallets.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceWalletLedgerReadiness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_wallet_active_has_ledger",
                schema: "wallets",
                table: "wallets",
                sql: "\"Status\" <> 'Active' OR \"LedgerProvisionedAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_wallet_active_has_ledger",
                schema: "wallets",
                table: "wallets");
        }
    }
}
