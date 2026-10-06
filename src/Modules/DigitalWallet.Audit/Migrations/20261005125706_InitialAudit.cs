using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DigitalWallet.Audit.Migrations
{
    /// <inheritdoc />
    public partial class InitialAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.CreateTable(
                name: "logs",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ResourceType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ResourceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Result = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Changes = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_logs", x => x.Id);
                    table.CheckConstraint("ck_audit_result", "\"Result\" IN ('Success','Denied','Failed')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_logs_Action_OccurredAt",
                schema: "audit",
                table: "logs",
                columns: new[] { "Action", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_logs_ActorId_OccurredAt_Id",
                schema: "audit",
                table: "logs",
                columns: new[] { "ActorId", "OccurredAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_logs_OccurredAt_Id",
                schema: "audit",
                table: "logs",
                columns: new[] { "OccurredAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_logs_ResourceType_ResourceId_OccurredAt",
                schema: "audit",
                table: "logs",
                columns: new[] { "ResourceType", "ResourceId", "OccurredAt" });
            migrationBuilder.Sql("""
                CREATE FUNCTION audit.reject_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Audit is append-only' USING ERRCODE='23514'; END $$;
                CREATE TRIGGER audit_no_update_delete BEFORE UPDATE OR DELETE ON audit.logs FOR EACH ROW EXECUTE FUNCTION audit.reject_mutation();
                CREATE TRIGGER audit_no_truncate BEFORE TRUNCATE ON audit.logs FOR EACH STATEMENT EXECUTE FUNCTION audit.reject_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION audit.reject_mutation() CASCADE");
            migrationBuilder.DropTable(
                name: "logs",
                schema: "audit");
        }
    }
}
