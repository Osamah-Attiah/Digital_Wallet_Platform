using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DigitalWallet.RiskLimits.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialRiskLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "risk_limits");

            migrationBuilder.CreateTable(
                name: "rules",
                schema: "risk_limits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    ScopeKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    TransferType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EffectiveFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EffectiveTo = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    PublishedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    MinimumTransferMinor = table.Column<long>(type: "bigint", nullable: true),
                    MaximumTransferMinor = table.Column<long>(type: "bigint", nullable: true),
                    DailyAmountMinor = table.Column<long>(type: "bigint", nullable: true),
                    DailyCount = table.Column<long>(type: "bigint", nullable: true),
                    MonthlyAmountMinor = table.Column<long>(type: "bigint", nullable: true),
                    MonthlyCount = table.Column<long>(type: "bigint", nullable: true),
                    FixedFeeMinor = table.Column<long>(type: "bigint", nullable: true),
                    BasisPoints = table.Column<int>(type: "integer", nullable: true),
                    MinimumFeeMinor = table.Column<long>(type: "bigint", nullable: true),
                    MaximumFeeMinor = table.Column<long>(type: "bigint", nullable: true),
                    ReviewFromMinor = table.Column<long>(type: "bigint", nullable: true),
                    DenyFromMinor = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rules", x => x.Id);
                    table.CheckConstraint("ck_rule_dates", "(\"EffectiveTo\" IS NULL OR \"EffectiveTo\" > \"EffectiveFrom\") AND ((\"PublishedAt\" IS NULL AND \"PublishedBy\" IS NULL) OR (\"PublishedAt\" IS NOT NULL AND \"PublishedBy\" IS NOT NULL AND \"PublishedBy\" <> '00000000-0000-0000-0000-000000000000'::uuid))");
                    table.CheckConstraint("ck_rule_identity", "\"Kind\" BETWEEN 0 AND 2 AND \"Scope\" BETWEEN 0 AND 3 AND \"Version\" > 0 AND \"Currency\" = 'SAR' AND \"TransferType\" = 'Internal' AND length(btrim(\"Reason\")) > 0 AND \"CreatedBy\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_rule_payload", "(\"Kind\" = 0 OR (\"MinimumTransferMinor\" IS NULL AND \"MaximumTransferMinor\" IS NULL AND \"DailyAmountMinor\" IS NULL AND \"DailyCount\" IS NULL AND \"MonthlyAmountMinor\" IS NULL AND \"MonthlyCount\" IS NULL)) AND\n(\"Kind\" = 1 OR (\"FixedFeeMinor\" IS NULL AND \"BasisPoints\" IS NULL AND \"MinimumFeeMinor\" IS NULL AND \"MaximumFeeMinor\" IS NULL)) AND\n(\"Kind\" = 2 OR (\"ReviewFromMinor\" IS NULL AND \"DenyFromMinor\" IS NULL)) AND\n(\"Kind\" <> 0 OR COALESCE(\"MinimumTransferMinor\", \"MaximumTransferMinor\", \"DailyAmountMinor\", \"DailyCount\", \"MonthlyAmountMinor\", \"MonthlyCount\") IS NOT NULL) AND\n(\"Kind\" <> 1 OR (\"FixedFeeMinor\" IS NOT NULL AND \"BasisPoints\" IS NOT NULL))");
                    table.CheckConstraint("ck_rule_scope", "(\"Scope\" = 0 AND \"ScopeKey\" = '*') OR (\"Scope\" = 1 AND \"ScopeKey\" ~ '^[A-Za-z0-9-]{1,64}$') OR (\"Scope\" IN (2,3) AND \"ScopeKey\" ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' AND \"ScopeKey\" <> '00000000-0000-0000-0000-000000000000')");
                    table.CheckConstraint("ck_rule_values", "(\"MinimumTransferMinor\" IS NULL OR \"MinimumTransferMinor\" >= 0) AND\n(\"MaximumTransferMinor\" IS NULL OR \"MaximumTransferMinor\" >= 0) AND\n(\"DailyAmountMinor\" IS NULL OR \"DailyAmountMinor\" >= 0) AND (\"DailyCount\" IS NULL OR \"DailyCount\" >= 0) AND\n(\"MonthlyAmountMinor\" IS NULL OR \"MonthlyAmountMinor\" >= 0) AND (\"MonthlyCount\" IS NULL OR \"MonthlyCount\" >= 0) AND\n(\"FixedFeeMinor\" IS NULL OR \"FixedFeeMinor\" >= 0) AND (\"BasisPoints\" IS NULL OR \"BasisPoints\" BETWEEN 0 AND 10000) AND\n(\"MinimumFeeMinor\" IS NULL OR \"MinimumFeeMinor\" >= 0) AND (\"MaximumFeeMinor\" IS NULL OR \"MaximumFeeMinor\" >= 0) AND\n(\"ReviewFromMinor\" IS NULL OR \"ReviewFromMinor\" >= 0) AND (\"DenyFromMinor\" IS NULL OR \"DenyFromMinor\" >= 0) AND\n(\"MinimumTransferMinor\" IS NULL OR \"MaximumTransferMinor\" IS NULL OR \"MinimumTransferMinor\" <= \"MaximumTransferMinor\") AND\n(\"MinimumFeeMinor\" IS NULL OR \"MaximumFeeMinor\" IS NULL OR \"MinimumFeeMinor\" <= \"MaximumFeeMinor\") AND\n(\"ReviewFromMinor\" IS NULL OR \"DenyFromMinor\" IS NULL OR \"ReviewFromMinor\" <= \"DenyFromMinor\")");
                });

            migrationBuilder.CreateTable(
                name: "usage_operations",
                schema: "risk_limits",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usage_operations", x => x.OperationId);
                });

            migrationBuilder.CreateTable(
                name: "period_usage",
                schema: "risk_limits",
                columns: table => new
                {
                    Subject = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    RuleVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Period = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    PeriodStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    Count = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_period_usage", x => new { x.Subject, x.RuleVersionId, x.Period, x.PeriodStart });
                    table.CheckConstraint("ck_usage", "\"AmountMinor\" >= 0 AND \"Count\" >= 0 AND \"Period\" IN ('Day','Month') AND \"PeriodEnd\" > \"PeriodStart\"");
                    table.ForeignKey(
                        name: "FK_period_usage_rules_RuleVersionId",
                        column: x => x.RuleVersionId,
                        principalSchema: "risk_limits",
                        principalTable: "rules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_period_usage_RuleVersionId",
                schema: "risk_limits",
                table: "period_usage",
                column: "RuleVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_rules_Kind_Scope_ScopeKey_Currency_TransferType_Version",
                schema: "risk_limits",
                table: "rules",
                columns: new[] { "Kind", "Scope", "ScopeKey", "Currency", "TransferType", "Version" },
                unique: true);
            migrationBuilder.Sql("""
                CREATE EXTENSION IF NOT EXISTS btree_gist;
                ALTER TABLE risk_limits.rules ADD CONSTRAINT ex_rule_effectivity
                  EXCLUDE USING gist ("Kind" WITH =, "Scope" WITH =, "ScopeKey" WITH =,
                    "Currency" WITH =, "TransferType" WITH =,
                    tstzrange("EffectiveFrom", "EffectiveTo", '[)') WITH &&)
                  WHERE ("PublishedAt" IS NOT NULL);
                CREATE FUNCTION risk_limits.guard_rule() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  PERFORM pg_advisory_xact_lock(610060006::bigint);
                  IF TG_OP = 'TRUNCATE' OR (TG_OP IN ('UPDATE','DELETE') AND OLD."PublishedAt" IS NOT NULL) THEN
                    RAISE EXCEPTION 'Published rules are immutable' USING ERRCODE = '23514';
                  END IF;
                  IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER guard_rule BEFORE INSERT OR UPDATE OR DELETE ON risk_limits.rules
                  FOR EACH ROW EXECUTE FUNCTION risk_limits.guard_rule();
                CREATE TRIGGER guard_rule_truncate BEFORE TRUNCATE ON risk_limits.rules
                  FOR EACH STATEMENT EXECUTE FUNCTION risk_limits.guard_rule();
                CREATE FUNCTION risk_limits.guard_operation() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Usage receipts are immutable' USING ERRCODE = '23514'; END $$;
                CREATE TRIGGER guard_operation BEFORE UPDATE OR DELETE ON risk_limits.usage_operations
                  FOR EACH ROW EXECUTE FUNCTION risk_limits.guard_operation();
                CREATE TRIGGER guard_operation_truncate BEFORE TRUNCATE ON risk_limits.usage_operations
                  FOR EACH STATEMENT EXECUTE FUNCTION risk_limits.guard_operation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "period_usage",
                schema: "risk_limits");

            migrationBuilder.DropTable(
                name: "usage_operations",
                schema: "risk_limits");

            migrationBuilder.DropTable(
                name: "rules",
                schema: "risk_limits");
            migrationBuilder.Sql("DROP FUNCTION risk_limits.guard_rule(); DROP FUNCTION risk_limits.guard_operation();");
        }
    }
}
