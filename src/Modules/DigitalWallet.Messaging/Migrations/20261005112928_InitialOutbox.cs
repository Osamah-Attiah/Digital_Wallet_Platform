using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DigitalWallet.Messaging.Migrations
{
    /// <inheritdoc />
    public partial class InitialOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "messaging");

            migrationBuilder.CreateTable(
                name: "outbox",
                schema: "messaging",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    EventVersion = table.Column<int>(type: "integer", nullable: false),
                    AggregateId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EnvelopeJson = table.Column<string>(type: "jsonb", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastError = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox", x => x.EventId);
                    table.CheckConstraint("ck_outbox_attempts", "\"Attempts\" >= 0 AND \"EventVersion\" > 0");
                    table.CheckConstraint("ck_outbox_lease", "(\"LeaseToken\" IS NULL) = (\"LeaseUntil\" IS NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_AggregateId_EventType_EventVersion",
                schema: "messaging",
                table: "outbox",
                columns: new[] { "AggregateId", "EventType", "EventVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_NextAttemptAt_LeaseUntil",
                schema: "messaging",
                table: "outbox",
                columns: new[] { "NextAttemptAt", "LeaseUntil" },
                filter: "\"PublishedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_OccurredAt",
                schema: "messaging",
                table: "outbox",
                column: "OccurredAt",
                filter: "\"PublishedAt\" IS NULL");
            migrationBuilder.Sql("""
                CREATE FUNCTION messaging.guard_outbox_fact() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF ROW(NEW."EventId", NEW."EventType", NEW."EventVersion", NEW."AggregateId", NEW."OccurredAt", NEW."EnvelopeJson")
                        IS DISTINCT FROM ROW(OLD."EventId", OLD."EventType", OLD."EventVersion", OLD."AggregateId", OLD."OccurredAt", OLD."EnvelopeJson")
                        OR NEW."Attempts" < OLD."Attempts"
                        OR (OLD."PublishedAt" IS NOT NULL AND NEW."PublishedAt" IS DISTINCT FROM OLD."PublishedAt") THEN
                        RAISE EXCEPTION 'Outbox facts and published markers are immutable' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END; $$;
                CREATE TRIGGER outbox_fact_guard BEFORE UPDATE ON messaging.outbox
                    FOR EACH ROW EXECUTE FUNCTION messaging.guard_outbox_fact();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox",
                schema: "messaging");
            migrationBuilder.Sql("DROP FUNCTION messaging.guard_outbox_fact();");
        }
    }
}
