using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DigitalWallet.RiskLimits.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PreserveUsageAcrossRuleVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Both phase-six migrations are deployed together before any traffic. Refuse to fabricate
            // missing subject references if someone ran the intermediate schema independently.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM risk_limits.usage_operations) THEN
                    RAISE EXCEPTION 'Apply both phase-six migrations before accepting usage';
                  END IF;
                END $$;
                """);
            migrationBuilder.AddColumn<long>(
                name: "AmountMinor",
                schema: "risk_limits",
                table: "usage_operations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "Consumed",
                schema: "risk_limits",
                table: "usage_operations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                schema: "risk_limits",
                table: "usage_operations",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "CustomerId",
                schema: "risk_limits",
                table: "usage_operations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EvaluatedAt",
                schema: "risk_limits",
                table: "usage_operations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "TransferType",
                schema: "risk_limits",
                table: "usage_operations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "WalletId",
                schema: "risk_limits",
                table: "usage_operations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_usage_operations_CustomerId_Currency_TransferType_Evaluated~",
                schema: "risk_limits",
                table: "usage_operations",
                columns: new[] { "CustomerId", "Currency", "TransferType", "EvaluatedAt" },
                filter: "\"Consumed\"");

            migrationBuilder.CreateIndex(
                name: "IX_usage_operations_WalletId_Currency_TransferType_EvaluatedAt",
                schema: "risk_limits",
                table: "usage_operations",
                columns: new[] { "WalletId", "Currency", "TransferType", "EvaluatedAt" },
                filter: "\"Consumed\"");

            migrationBuilder.AddCheckConstraint(
                name: "ck_operation_amount",
                schema: "risk_limits",
                table: "usage_operations",
                sql: "\"AmountMinor\" > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_usage_operations_CustomerId_Currency_TransferType_Evaluated~",
                schema: "risk_limits",
                table: "usage_operations");

            migrationBuilder.DropIndex(
                name: "IX_usage_operations_WalletId_Currency_TransferType_EvaluatedAt",
                schema: "risk_limits",
                table: "usage_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_operation_amount",
                schema: "risk_limits",
                table: "usage_operations");

            migrationBuilder.DropColumn(
                name: "AmountMinor",
                schema: "risk_limits",
                table: "usage_operations");

            migrationBuilder.DropColumn(
                name: "Consumed",
                schema: "risk_limits",
                table: "usage_operations");

            migrationBuilder.DropColumn(
                name: "Currency",
                schema: "risk_limits",
                table: "usage_operations");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                schema: "risk_limits",
                table: "usage_operations");

            migrationBuilder.DropColumn(
                name: "EvaluatedAt",
                schema: "risk_limits",
                table: "usage_operations");

            migrationBuilder.DropColumn(
                name: "TransferType",
                schema: "risk_limits",
                table: "usage_operations");

            migrationBuilder.DropColumn(
                name: "WalletId",
                schema: "risk_limits",
                table: "usage_operations");
        }
    }
}
