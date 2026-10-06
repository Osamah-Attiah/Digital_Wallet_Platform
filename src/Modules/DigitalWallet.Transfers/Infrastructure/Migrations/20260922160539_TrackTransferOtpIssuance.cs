using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DigitalWallet.Transfers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TrackTransferOtpIssuance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OtpIssuedAt",
                schema: "transfers",
                table: "transfers",
                type: "timestamp with time zone",
                nullable: true);
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION transfers.guard_transfer() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP IN ('DELETE', 'TRUNCATE') THEN RAISE EXCEPTION 'Transfers are retained' USING ERRCODE = '23514'; END IF;
                    IF OLD."Status" IN ('Completed','Rejected','Failed','PendingReview') OR
                       (to_jsonb(NEW) - ARRAY['Status','ReasonCode','OtpChallengeId','OtpIssuedAt','ConfirmedAt','JournalId','CompletedAt','Version','ConcurrencyVersion']) IS DISTINCT FROM
                       (to_jsonb(OLD) - ARRAY['Status','ReasonCode','OtpChallengeId','OtpIssuedAt','ConfirmedAt','JournalId','CompletedAt','Version','ConcurrencyVersion']) OR
                       NEW."Version" <= OLD."Version" OR NEW."ConcurrencyVersion" = OLD."ConcurrencyVersion" OR
                       (OLD."Status" = 'Validating' AND NEW."Status" = 'PendingConfirmation') OR
                       (OLD."ConfirmedAt" IS NOT NULL AND (NEW."ConfirmedAt" IS DISTINCT FROM OLD."ConfirmedAt" OR NEW."OtpChallengeId" IS DISTINCT FROM OLD."OtpChallengeId")) THEN
                        RAISE EXCEPTION 'Invalid transfer mutation' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OtpIssuedAt",
                schema: "transfers",
                table: "transfers");
        }
    }
}
