using System.Data.Common;
using System.Text.Json;
using DigitalWallet.Messaging.Contracts;
using DigitalWallet.SharedKernel.Transactions;
using Microsoft.EntityFrameworkCore;

namespace DigitalWallet.Messaging;

// Enlists only in an existing financial transaction; no broker or network service is reachable here.
public sealed class OutboxWriter : IAsyncDisposable
{
    private readonly OutboxDbContext _db;
    private OutboxWriter(OutboxDbContext db) => _db = db;
    public static async Task<OutboxWriter> EnlistAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        ParticipantSavepoint.Validate(connection, transaction);
        var db = new OutboxDbContext(new DbContextOptionsBuilder<OutboxDbContext>().UseNpgsql(connection).Options);
        try { await db.Database.UseTransactionAsync(transaction, ct); return new(db); }
        catch { await db.DisposeAsync(); throw; }
    }
    public async Task AppendAsync(EventEnvelope e, CancellationToken ct)
    {
        WalletEvents.Validate(e);
        var json = JsonSerializer.Serialize(e, WalletEvents.Json);
        var trace = TraceHeaders.Capture(e);
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO messaging.outbox ("EventId", "EventType", "EventVersion", "AggregateId", "OccurredAt", "EnvelopeJson", "Attempts", "NextAttemptAt", "TraceParent", "TraceCorrelationId", "TraceCausationId")
            VALUES ({e.EventId}, {e.EventType}, {e.EventVersion}, {e.AggregateId}, {e.OccurredAt}, CAST({json} AS jsonb), 0, {e.OccurredAt}, {trace.TraceParent}, {trace.CorrelationId}, {trace.CausationId})
            ON CONFLICT ("EventId") DO NOTHING
            """, ct);
        var stored = await _db.Messages.AsNoTracking().SingleAsync(x => x.EventId == e.EventId, ct);
        var prior = JsonSerializer.Deserialize<EventEnvelope>(stored.EnvelopeJson, WalletEvents.Json)!;
        // A replay may have a new HTTP correlation. The original immutable envelope wins.
        // jsonb canonicalizes property order; compare typed payloads rather than JSON text.
        var same = e.EventType == WalletEvents.ReportRequested
            ? prior.Payload.Deserialize<ReportRequestedV1>(WalletEvents.Json) == e.Payload.Deserialize<ReportRequestedV1>(WalletEvents.Json)
            : e.EventType == WalletEvents.TransferCompleted
            ? prior.Payload.Deserialize<TransferCompletedV1>(WalletEvents.Json) == e.Payload.Deserialize<TransferCompletedV1>(WalletEvents.Json)
            : prior.Payload.Deserialize<ReversalCompletedV1>(WalletEvents.Json) == e.Payload.Deserialize<ReversalCompletedV1>(WalletEvents.Json);
        if (!same || prior.EventType != e.EventType || prior.EventVersion != e.EventVersion || prior.AggregateId != e.AggregateId || prior.OccurredAt != e.OccurredAt)
            throw new InvalidOperationException("Outbox fact conflicts with committed event.");
    }
    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
