using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DigitalWallet.Reporting.Migrations
{
    /// <inheritdoc />
    public partial class InitialReporting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "reporting");

            migrationBuilder.CreateTable(
                name: "jobs",
                schema: "reporting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    From = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    To = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IncludesPii = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    RowCount = table.Column<int>(type: "integer", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.Id);
                    table.CheckConstraint("ck_report_bounds", "\"From\" < \"To\" AND \"To\" - \"From\" <= INTERVAL '31 days' AND \"Attempts\" >= 0");
                    table.CheckConstraint("ck_report_state", "\"Status\" IN ('Queued','Running','Completed','Failed','Expired')");
                });

            migrationBuilder.CreateTable(
                name: "inbox",
                schema: "reporting",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox", x => x.EventId);
                    table.ForeignKey(
                        name: "FK_inbox_jobs_JobId",
                        column: x => x.JobId,
                        principalSchema: "reporting",
                        principalTable: "jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_inbox_JobId",
                schema: "reporting",
                table: "inbox",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_jobs_OwnerId_CreatedAt_Id",
                schema: "reporting",
                table: "jobs",
                columns: new[] { "OwnerId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_OwnerId_IdempotencyKey",
                schema: "reporting",
                table: "jobs",
                columns: new[] { "OwnerId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_jobs_Status_LeaseUntil",
                schema: "reporting",
                table: "jobs",
                columns: new[] { "Status", "LeaseUntil" });
            migrationBuilder.Sql("""
                CREATE FUNCTION reporting.guard_job() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Report jobs cannot be deleted' USING ERRCODE='23514'; END IF;
                  IF ROW(NEW."Id",NEW."OwnerId",NEW."IdempotencyKey",NEW."Fingerprint",NEW."Type",NEW."From",NEW."To",NEW."IncludesPii",NEW."CreatedAt",NEW."ExpiresAt",NEW."CorrelationId") IS DISTINCT FROM
                     ROW(OLD."Id",OLD."OwnerId",OLD."IdempotencyKey",OLD."Fingerprint",OLD."Type",OLD."From",OLD."To",OLD."IncludesPii",OLD."CreatedAt",OLD."ExpiresAt",OLD."CorrelationId") OR NEW."Attempts"<OLD."Attempts" THEN
                    RAISE EXCEPTION 'Report specification is immutable' USING ERRCODE='23514'; END IF;
                  IF OLD."Status" IN ('Completed','Failed','Expired') AND NEW."Status"<>OLD."Status" AND NEW."Status"<>'Expired' THEN
                    RAISE EXCEPTION 'Report outcome is final' USING ERRCODE='23514'; END IF;
                  IF OLD."Status"='Running' AND NEW."Status"='Queued' THEN RAISE EXCEPTION 'Report state cannot regress' USING ERRCODE='23514'; END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER report_job_guard BEFORE UPDATE OR DELETE ON reporting.jobs FOR EACH ROW EXECUTE FUNCTION reporting.guard_job();
                CREATE FUNCTION reporting.guard_inbox() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Report inbox is append-only' USING ERRCODE='23514'; END $$;
                CREATE TRIGGER report_inbox_guard BEFORE UPDATE OR DELETE ON reporting.inbox FOR EACH ROW EXECUTE FUNCTION reporting.guard_inbox();
                CREATE TRIGGER report_inbox_truncate_guard BEFORE TRUNCATE ON reporting.inbox FOR EACH STATEMENT EXECUTE FUNCTION reporting.guard_inbox();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION reporting.guard_job() CASCADE; DROP FUNCTION reporting.guard_inbox() CASCADE;");
            migrationBuilder.DropTable(
                name: "inbox",
                schema: "reporting");

            migrationBuilder.DropTable(
                name: "jobs",
                schema: "reporting");
        }
    }
}
