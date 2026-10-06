using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace DigitalWallet.Messaging.Migrations;
[DbContext(typeof(OutboxDbContext))]
[Migration("20261006090000_OutboxTraceHeaders")]
public sealed class OutboxTraceHeaders : Migration
{
    private const string TraceGuard = """
        CREATE FUNCTION messaging.guard_outbox_trace() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF ROW(NEW."TraceParent", NEW."TraceCorrelationId", NEW."TraceCausationId")
                IS DISTINCT FROM ROW(OLD."TraceParent", OLD."TraceCorrelationId", OLD."TraceCausationId") THEN
                RAISE EXCEPTION 'Outbox trace context is immutable' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END; $$;
        CREATE TRIGGER outbox_trace_guard BEFORE UPDATE ON messaging.outbox
            FOR EACH ROW EXECUTE FUNCTION messaging.guard_outbox_trace();
        """;
    protected override void Up(MigrationBuilder b)
    {
        b.AddColumn<string>("TraceParent", "outbox", schema: "messaging", type: "character varying(55)", maxLength: 55, nullable: true);
        b.AddColumn<string>("TraceCorrelationId", "outbox", schema: "messaging", type: "character varying(128)", maxLength: 128, nullable: true);
        b.AddColumn<string>("TraceCausationId", "outbox", schema: "messaging", type: "character varying(36)", maxLength: 36, nullable: true);
        b.Sql(TraceGuard);
    }
    protected override void Down(MigrationBuilder b)
    {
        b.Sql("DROP TRIGGER outbox_trace_guard ON messaging.outbox; DROP FUNCTION messaging.guard_outbox_trace();");
        b.DropColumn("TraceParent", "outbox", "messaging");
        b.DropColumn("TraceCorrelationId", "outbox", "messaging");
        b.DropColumn("TraceCausationId", "outbox", "messaging");
    }
}
