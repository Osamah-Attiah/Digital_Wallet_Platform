using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DigitalWallet.Transactions.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "transactions");

            migrationBuilder.CreateTable(
                name: "transactions",
                schema: "transactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicReference = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TransferId = table.Column<Guid>(type: "uuid", nullable: false),
                    JournalId = table.Column<Guid>(type: "uuid", nullable: false),
                    SenderCustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientCustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SenderWalletId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientWalletId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    FeeMinor = table.Column<long>(type: "bigint", nullable: false),
                    SenderDebitMinor = table.Column<long>(type: "bigint", nullable: false),
                    RecipientCreditMinor = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    MaskedSenderName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MaskedRecipientName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReversalOf = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transactions", x => x.Id);
                    table.CheckConstraint("CK_transactions_money", "\"AmountMinor\" > 0 AND \"FeeMinor\" >= 0 AND \"SenderDebitMinor\" > 0 AND \"RecipientCreditMinor\" > 0 AND ((\"Type\" = 'InternalTransfer' AND \"SenderDebitMinor\"::numeric = \"AmountMinor\"::numeric + \"FeeMinor\"::numeric AND \"RecipientCreditMinor\" = \"AmountMinor\") OR (\"Type\" = 'Reversal' AND \"SenderDebitMinor\" = \"AmountMinor\" AND \"RecipientCreditMinor\"::numeric = \"AmountMinor\"::numeric + \"FeeMinor\"::numeric))");
                    table.CheckConstraint("CK_transactions_values", "\"Currency\" = 'SAR' AND \"SenderCustomerId\" <> \"RecipientCustomerId\" AND \"SenderWalletId\" <> \"RecipientWalletId\" AND ((\"Type\" = 'InternalTransfer' AND \"ReversalOf\" IS NULL AND \"Id\" = \"TransferId\") OR (\"Type\" = 'Reversal' AND \"ReversalOf\" IS NOT NULL AND \"ReversalOf\" <> \"Id\"))");
                    table.ForeignKey(
                        name: "FK_transactions_transactions_ReversalOf",
                        column: x => x.ReversalOf,
                        principalSchema: "transactions",
                        principalTable: "transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reversal_requests",
                schema: "transactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalTransactionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DecidedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecisionNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ApprovalKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ApprovalHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OtpChallengeId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversalTransactionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reversal_requests", x => x.Id);
                    table.CheckConstraint("CK_reversal_requests_reason", "length(btrim(\"Reason\")) > 0 AND length(\"RequestKey\") > 0");
                    table.CheckConstraint("CK_reversal_requests_state", "(\"Status\" = 'Pending' AND \"DecidedBy\" IS NULL AND \"DecidedAt\" IS NULL AND \"DecisionNote\" IS NULL AND \"ApprovalKey\" IS NULL AND \"ApprovalHash\" IS NULL AND \"OtpChallengeId\" IS NULL AND \"ReversalTransactionId\" IS NULL) OR (\"Status\" IN ('Completed','Rejected') AND \"DecidedBy\" IS NOT NULL AND \"DecidedBy\" <> \"RequestedBy\" AND \"DecidedAt\" IS NOT NULL AND \"DecidedAt\" >= \"CreatedAt\" AND ((\"Status\" = 'Completed' AND \"ApprovalKey\" IS NOT NULL AND \"ApprovalHash\" IS NOT NULL AND \"OtpChallengeId\" IS NOT NULL AND \"ReversalTransactionId\" IS NOT NULL AND \"ReversalTransactionId\" = \"Id\") OR (\"Status\" = 'Rejected' AND \"DecisionNote\" IS NOT NULL AND length(btrim(\"DecisionNote\")) > 0 AND \"ReversalTransactionId\" IS NULL AND \"ApprovalKey\" IS NULL AND \"ApprovalHash\" IS NULL AND \"OtpChallengeId\" IS NULL)))");
                    table.ForeignKey(
                        name: "FK_reversal_requests_transactions_OriginalTransactionId",
                        column: x => x.OriginalTransactionId,
                        principalSchema: "transactions",
                        principalTable: "transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reversal_requests_transactions_ReversalTransactionId",
                        column: x => x.ReversalTransactionId,
                        principalSchema: "transactions",
                        principalTable: "transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_reversal_requests_DecidedBy_ApprovalKey",
                schema: "transactions",
                table: "reversal_requests",
                columns: new[] { "DecidedBy", "ApprovalKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reversal_requests_OriginalTransactionId",
                schema: "transactions",
                table: "reversal_requests",
                column: "OriginalTransactionId",
                unique: true,
                filter: "\"Status\" IN ('Pending','Completed')");

            migrationBuilder.CreateIndex(
                name: "IX_reversal_requests_RequestedBy_RequestKey",
                schema: "transactions",
                table: "reversal_requests",
                columns: new[] { "RequestedBy", "RequestKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reversal_requests_ReversalTransactionId",
                schema: "transactions",
                table: "reversal_requests",
                column: "ReversalTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_transactions_JournalId",
                schema: "transactions",
                table: "transactions",
                column: "JournalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_transactions_OccurredAt_Id",
                schema: "transactions",
                table: "transactions",
                columns: new[] { "OccurredAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_transactions_PublicReference",
                schema: "transactions",
                table: "transactions",
                column: "PublicReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_transactions_RecipientCustomerId_OccurredAt_Id",
                schema: "transactions",
                table: "transactions",
                columns: new[] { "RecipientCustomerId", "OccurredAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_transactions_ReversalOf",
                schema: "transactions",
                table: "transactions",
                column: "ReversalOf",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_transactions_SenderCustomerId_OccurredAt_Id",
                schema: "transactions",
                table: "transactions",
                columns: new[] { "SenderCustomerId", "OccurredAt", "Id" });
            using var stream = typeof(InitialTransactions).Assembly.GetManifestResourceStream("DigitalWallet.Transactions.Infrastructure.Migrations.TransactionGuards.sql")!;
            using var reader = new System.IO.StreamReader(stream);
            migrationBuilder.Sql(reader.ReadToEnd());
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION transactions.check_reversal() CASCADE; DROP FUNCTION transactions.guard_request() CASCADE; DROP FUNCTION transactions.retain_fact() CASCADE;");
            migrationBuilder.DropTable(
                name: "reversal_requests",
                schema: "transactions");

            migrationBuilder.DropTable(
                name: "transactions",
                schema: "transactions");
        }
    }
}
