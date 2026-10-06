using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DigitalWallet.Ledger.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ledger");

            migrationBuilder.CreateTable(
                name: "accounts",
                schema: "ledger",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WalletId = table.Column<Guid>(type: "uuid", nullable: true),
                    Code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Currency = table.Column<string>(type: "character(3)", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    NormalSide = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_accounts", x => x.Id);
                    table.UniqueConstraint("AK_accounts_Id_Currency", x => new { x.Id, x.Currency });
                    table.CheckConstraint("ck_account_currency", "\"Currency\" IN ('SAR','USD','EUR','GBP','JPY','AED','KWD','BHD','QAR','OMR')");
                    table.CheckConstraint("ck_account_kind", "\"Kind\" IN ('CustomerWalletLiability','AdjustmentControl','PlatformFeeRevenue','Suspense')");
                    table.CheckConstraint("ck_account_side", "(\"Kind\" IN ('CustomerWalletLiability','PlatformFeeRevenue') AND \"NormalSide\" = 'Credit') OR (\"Kind\" IN ('AdjustmentControl','Suspense') AND \"NormalSide\" = 'Debit')");
                    table.CheckConstraint("ck_account_wallet", "(\"Kind\" = 'CustomerWalletLiability') = (\"WalletId\" IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "journals",
                schema: "ledger",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Currency = table.Column<string>(type: "character(3)", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversalOf = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    PostedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_journals", x => x.Id);
                    table.UniqueConstraint("AK_journals_Id_Currency", x => new { x.Id, x.Currency });
                    table.CheckConstraint("ck_journal_currency", "\"Currency\" IN ('SAR','USD','EUR','GBP','JPY','AED','KWD','BHD','QAR','OMR')");
                    table.CheckConstraint("ck_journal_source", "length(btrim(\"SourceType\")) > 0 AND length(btrim(\"SourceId\")) > 0 AND length(btrim(\"Reason\")) > 0");
                    table.CheckConstraint("ck_journal_status", "(\"Status\" = 'Draft' AND \"PostedAt\" IS NULL) OR (\"Status\" = 'Posted' AND \"PostedAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_journals_journals_ReversalOf",
                        column: x => x.ReversalOf,
                        principalSchema: "ledger",
                        principalTable: "journals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "account_balances",
                schema: "ledger",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    BalanceMinor = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_balances", x => x.AccountId);
                    table.CheckConstraint("ck_balance_version", "\"Version\" >= 0");
                    table.ForeignKey(
                        name: "FK_account_balances_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "ledger",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "entries",
                schema: "ledger",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JournalId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Side = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "character(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entries", x => x.Id);
                    table.CheckConstraint("ck_entry_positive", "\"AmountMinor\" > 0");
                    table.CheckConstraint("ck_entry_side", "\"Side\" IN ('Debit','Credit')");
                    table.ForeignKey(
                        name: "FK_entries_accounts_AccountId_Currency",
                        columns: x => new { x.AccountId, x.Currency },
                        principalSchema: "ledger",
                        principalTable: "accounts",
                        principalColumns: new[] { "Id", "Currency" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_entries_journals_JournalId_Currency",
                        columns: x => new { x.JournalId, x.Currency },
                        principalSchema: "ledger",
                        principalTable: "journals",
                        principalColumns: new[] { "Id", "Currency" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_accounts_Code",
                schema: "ledger",
                table: "accounts",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_accounts_Kind_Currency",
                schema: "ledger",
                table: "accounts",
                columns: new[] { "Kind", "Currency" },
                unique: true,
                filter: "\"WalletId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_accounts_WalletId_Currency",
                schema: "ledger",
                table: "accounts",
                columns: new[] { "WalletId", "Currency" },
                unique: true,
                filter: "\"WalletId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_entries_AccountId_Currency",
                schema: "ledger",
                table: "entries",
                columns: new[] { "AccountId", "Currency" });

            migrationBuilder.CreateIndex(
                name: "IX_entries_AccountId_JournalId",
                schema: "ledger",
                table: "entries",
                columns: new[] { "AccountId", "JournalId" });

            migrationBuilder.CreateIndex(
                name: "IX_entries_JournalId_Currency",
                schema: "ledger",
                table: "entries",
                columns: new[] { "JournalId", "Currency" });

            migrationBuilder.CreateIndex(
                name: "IX_journals_PostedAt",
                schema: "ledger",
                table: "journals",
                column: "PostedAt");

            migrationBuilder.CreateIndex(
                name: "IX_journals_ReversalOf",
                schema: "ledger",
                table: "journals",
                column: "ReversalOf",
                unique: true,
                filter: "\"ReversalOf\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_journals_SourceType_SourceId",
                schema: "ledger",
                table: "journals",
                columns: new[] { "SourceType", "SourceId" },
                unique: true);

            using var sql = new System.IO.StreamReader(typeof(InitialLedger).Assembly.GetManifestResourceStream(
                "DigitalWallet.Ledger.Infrastructure.Migrations.InitialLedgerGuards.sql")!);
            migrationBuilder.Sql(sql.ReadToEnd());
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_balances",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "entries",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "accounts",
                schema: "ledger");

            migrationBuilder.DropTable(
                name: "journals",
                schema: "ledger");
            migrationBuilder.Sql("""
                DROP FUNCTION ledger.require_posted(), ledger.approve_journal(), ledger.guard_entry(),
                    ledger.guard_balance(), ledger.initialize_balance(), ledger.reject_mutation();
                """);
        }
    }
}
