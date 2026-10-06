using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DigitalWallet.Transfers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "transfers");

            migrationBuilder.CreateTable(
                name: "quotes",
                schema: "transfers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerCustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SenderWalletId = table.Column<Guid>(type: "uuid", nullable: false),
                    BeneficiaryId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetWalletId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetCustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    FeeMinor = table.Column<long>(type: "bigint", nullable: false),
                    TotalDebitMinor = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    RiskDecision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReasonCodesJson = table.Column<string>(type: "text", nullable: false),
                    AssessmentJson = table.Column<string>(type: "text", nullable: false),
                    FeeRuleVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    FeeRuleVersion = table.Column<int>(type: "integer", nullable: false),
                    LimitRuleVersionsJson = table.Column<string>(type: "text", nullable: false),
                    RiskRuleVersionsJson = table.Column<string>(type: "text", nullable: false),
                    RequestFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MaskedSenderName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MaskedRecipientName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ConcurrencyVersion = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quotes", x => x.Id);
                    table.CheckConstraint("CK_quotes_money", "\"AmountMinor\" > 0 AND \"FeeMinor\" >= 0 AND \"TotalDebitMinor\" > 0 AND \"TotalDebitMinor\"::numeric = \"AmountMinor\"::numeric + \"FeeMinor\"::numeric");
                    table.CheckConstraint("CK_quotes_time", "\"ExpiresAt\" > \"CreatedAt\" AND (\"UsedAt\" IS NULL OR (\"UsedAt\" >= \"CreatedAt\" AND \"UsedAt\" < \"ExpiresAt\"))");
                    table.CheckConstraint("CK_quotes_values", "\"Currency\" = 'SAR' AND \"RiskDecision\" IN ('Allow','Review','Deny') AND \"Version\" > 0 AND \"FeeRuleVersion\" > 0");
                    table.CheckConstraint("CK_quotes_wallets", "\"SenderWalletId\" <> \"TargetWalletId\"");
                });

            migrationBuilder.CreateTable(
                name: "transfers",
                schema: "transfers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicReference = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    OwnerIdentityUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerCustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetCustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SenderWalletId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetWalletId = table.Column<Guid>(type: "uuid", nullable: false),
                    BeneficiaryId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuoteId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    FeeMinor = table.Column<long>(type: "bigint", nullable: false),
                    TotalDebitMinor = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    AssessmentJson = table.Column<string>(type: "text", nullable: false),
                    MaskedSenderName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MaskedRecipientName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OtpChallengeId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    JournalId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ConcurrencyVersion = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transfers", x => x.Id);
                    table.CheckConstraint("CK_transfers_confirmation", "\"Status\" NOT IN ('Validating','Completed','PendingReview','Rejected') OR (\"ConfirmedAt\" IS NOT NULL AND \"OtpChallengeId\" IS NOT NULL)");
                    table.CheckConstraint("CK_transfers_money", "\"AmountMinor\" > 0 AND \"FeeMinor\" >= 0 AND \"TotalDebitMinor\" > 0 AND \"TotalDebitMinor\"::numeric = \"AmountMinor\"::numeric + \"FeeMinor\"::numeric");
                    table.CheckConstraint("CK_transfers_state", "\"Status\" IN ('PendingConfirmation','Validating','PendingReview','Completed','Rejected','Failed') AND (\"Status\" = 'Completed') = (\"JournalId\" IS NOT NULL AND \"CompletedAt\" IS NOT NULL) AND (\"Status\" = 'Completed' OR (\"JournalId\" IS NULL AND \"CompletedAt\" IS NULL))");
                    table.CheckConstraint("CK_transfers_time", "\"ExpiresAt\" > \"CreatedAt\" AND (\"CompletedAt\" IS NULL OR (\"CompletedAt\" >= \"CreatedAt\" AND \"CompletedAt\" < \"ExpiresAt\"))");
                    table.CheckConstraint("CK_transfers_values", "\"Currency\" = 'SAR' AND \"Version\" > 0");
                    table.CheckConstraint("CK_transfers_wallets", "\"SenderWalletId\" <> \"TargetWalletId\" AND \"OwnerCustomerId\" <> \"TargetCustomerId\"");
                    table.ForeignKey(
                        name: "FK_transfers_quotes_QuoteId",
                        column: x => x.QuoteId,
                        principalSchema: "transfers",
                        principalTable: "quotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "idempotency",
                schema: "transfers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerCustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TransferId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResponseJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency", x => x.Id);
                    table.ForeignKey(
                        name: "FK_idempotency_transfers_TransferId",
                        column: x => x.TransferId,
                        principalSchema: "transfers",
                        principalTable: "transfers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_OwnerCustomerId_Scope_Key",
                schema: "transfers",
                table: "idempotency",
                columns: new[] { "OwnerCustomerId", "Scope", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_TransferId",
                schema: "transfers",
                table: "idempotency",
                column: "TransferId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_quotes_OwnerCustomerId_ExpiresAt",
                schema: "transfers",
                table: "quotes",
                columns: new[] { "OwnerCustomerId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_transfers_JournalId",
                schema: "transfers",
                table: "transfers",
                column: "JournalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_transfers_OwnerCustomerId_CreatedAt_Id",
                schema: "transfers",
                table: "transfers",
                columns: new[] { "OwnerCustomerId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_transfers_PublicReference",
                schema: "transfers",
                table: "transfers",
                column: "PublicReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_transfers_QuoteId",
                schema: "transfers",
                table: "transfers",
                column: "QuoteId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_transfers_TargetCustomerId_CreatedAt_Id",
                schema: "transfers",
                table: "transfers",
                columns: new[] { "TargetCustomerId", "CreatedAt", "Id" });
            using var stream = typeof(TransfersDbContext).Assembly.GetManifestResourceStream("DigitalWallet.Transfers.Infrastructure.Migrations.TransferGuards.sql")!;
            using var reader = new System.IO.StreamReader(stream);
            migrationBuilder.Sql(reader.ReadToEnd());

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION transfers.validate_completion() CASCADE; DROP FUNCTION transfers.guard_idempotency() CASCADE; DROP FUNCTION transfers.guard_transfer() CASCADE; DROP FUNCTION transfers.guard_quote() CASCADE;");
            migrationBuilder.DropTable(
                name: "idempotency",
                schema: "transfers");

            migrationBuilder.DropTable(
                name: "transfers",
                schema: "transfers");

            migrationBuilder.DropTable(
                name: "quotes",
                schema: "transfers");
        }
    }
}
