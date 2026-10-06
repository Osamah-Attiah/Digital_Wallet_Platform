using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DigitalWallet.Wallets.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecoverableLedgerProvisioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // All old wallets enter the durable backfill queue (nullable completion marker).
            // Only previously Active wallets become Pending. Frozen/Restricted/Closed stay protected.
            migrationBuilder.Sql("UPDATE wallets.wallets SET \"Status\" = 'Pending' WHERE \"Status\" = 'Active'");
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LedgerProvisionedAt",
                schema: "wallets",
                table: "wallets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_wallets_pending_ledger",
                schema: "wallets",
                table: "wallets",
                column: "Id",
                filter: "\"LedgerProvisionedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_wallets_pending_ledger",
                schema: "wallets",
                table: "wallets");

            migrationBuilder.DropColumn(
                name: "LedgerProvisionedAt",
                schema: "wallets",
                table: "wallets");
        }
    }
}
