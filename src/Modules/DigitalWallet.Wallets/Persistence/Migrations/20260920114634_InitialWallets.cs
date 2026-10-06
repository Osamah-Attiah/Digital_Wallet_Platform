using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DigitalWallet.Wallets.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialWallets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "wallets");

            migrationBuilder.CreateTable(
                name: "wallets",
                schema: "wallets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PublicHandle = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyVersion = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wallets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "wallet_status_history",
                schema: "wallets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WalletId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ToStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ActorIdentityUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wallet_status_history", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wallet_status_history_wallets_WalletId",
                        column: x => x.WalletId,
                        principalSchema: "wallets",
                        principalTable: "wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_wallet_status_history_WalletId_OccurredAt",
                schema: "wallets",
                table: "wallet_status_history",
                columns: new[] { "WalletId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_wallets_CustomerId_Currency",
                schema: "wallets",
                table: "wallets",
                columns: new[] { "CustomerId", "Currency" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wallets_PublicHandle",
                schema: "wallets",
                table: "wallets",
                column: "PublicHandle",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wallet_status_history",
                schema: "wallets");

            migrationBuilder.DropTable(
                name: "wallets",
                schema: "wallets");
        }
    }
}
