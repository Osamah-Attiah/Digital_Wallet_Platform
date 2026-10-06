using System.Diagnostics;
using DigitalWallet.SharedKernel.Observability;
using System.Text.Json;
using DigitalWallet.Messaging.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace DigitalWallet.Messaging;

public sealed class MessagingOptions
{
    public bool MonitorDeclaresTopology { get; init; } = true;
    public bool WorkersEnabled { get; set; } = true;
    public string RabbitUri { get; set; } = "amqp://guest:guest@localhost:5672/";
    public string Prefix { get; set; } = "digitalwallet.v1";
    public int LeaseSeconds { get; set; } = 60;
    public int PublishTimeoutSeconds { get; set; } = 15;
    public int PollMilliseconds { get; set; } = 500;
    public int RetryBaseMilliseconds { get; set; } = 1000;
    public int MaxConsumerAttempts { get; set; } = 3;
    public TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(Math.Min(300_000, RetryBaseMilliseconds * Math.Pow(2, Math.Min(18, attempt - 1))));
    public void Validate()
    {
        if (LeaseSeconds < 2 || PublishTimeoutSeconds < 1 || LeaseSeconds <= PublishTimeoutSeconds || PollMilliseconds < 10 ||
            RetryBaseMilliseconds < 50 || MaxConsumerAttempts < 1 || MaxConsumerAttempts > 8 ||
            Prefix.Length is < 1 or > 80 || Prefix.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) ||
            !Uri.TryCreate(RabbitUri, UriKind.Absolute, out var uri) || uri.Scheme is not ("amqp" or "amqps"))
            throw new InvalidOperationException("Invalid messaging configuration.");
    }
}
public interface IMessagePublisher
{
    Task PublishAsync(EventEnvelope envelope, CancellationToken ct);
    Task PublishAsync(EventEnvelope envelope, TraceHeaders headers, CancellationToken ct) => PublishAsync(envelope, ct);
}
public interface IMessagingCheckpoint { Task ReachedAsync(string point, Guid eventId, CancellationToken ct); }
public sealed class NoopMessagingCheckpoint : IMessagingCheckpoint
{ public Task ReachedAsync(string point, Guid eventId, CancellationToken ct) => Task.CompletedTask; }
// Fault injection models abrupt process loss: leave the lease/unacknowledged delivery intact.
public sealed class MessagingCrashException : Exception;

public sealed class OutboxPublisher(OutboxDbContext db, IMessagePublisher broker, MessagingOptions options,
    TimeProvider clock, IMessagingCheckpoint checkpoint, MessagingTelemetry telemetry)
{
    public async Task<bool> PublishOneAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow(); var token = Guid.NewGuid();
        // This short database transaction ends before any RabbitMQ call. Fenced leases survive worker death.
        await db.Database.OpenConnectionAsync(ct);
        OutboxMessage? message;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                WITH candidate AS (
                    SELECT "EventId" FROM messaging.outbox
                    WHERE "PublishedAt" IS NULL AND "NextAttemptAt" <= @now AND ("LeaseUntil" IS NULL OR "LeaseUntil" <= @now)
                    ORDER BY "NextAttemptAt", "EventId" FOR UPDATE SKIP LOCKED LIMIT 1
                )
                UPDATE messaging.outbox o SET "LeaseToken" = @token, "LeaseUntil" = @until, "Attempts" = "Attempts" + 1
                FROM candidate c WHERE o."EventId" = c."EventId" RETURNING o."EventId"
                """, (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)transaction.GetDbTransaction());
            cmd.Parameters.AddWithValue("now", now); cmd.Parameters.AddWithValue("token", token);
            cmd.Parameters.AddWithValue("until", now.AddSeconds(options.LeaseSeconds));
            var id = await cmd.ExecuteScalarAsync(ct);
            message = id is Guid eventId ? await db.Messages.AsNoTracking().SingleAsync(x => x.EventId == eventId, ct) : null;
            await transaction.CommitAsync(ct);
        }
        finally { await db.Database.CloseConnectionAsync(); }
        if (message is null) return false;
        var headers = new TraceHeaders(message.TraceParent, message.TraceCorrelationId ?? "", message.TraceCausationId ?? "");
        using var span = WalletTelemetry.Start(TelemetryModule.Messaging, OperationName.OutboxPublish, ActivityKind.Producer, headers.Parent);
        Activity.Current?.SetTag("correlation.id", message.TraceCorrelationId);
        Activity.Current?.SetTag("causation.id", message.TraceCausationId);
        telemetry.PublishAttempts.Add(1);
        try
        {
            await checkpoint.ReachedAsync("AfterClaim", message.EventId, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.PublishTimeoutSeconds));
            var e = JsonSerializer.Deserialize<EventEnvelope>(message.EnvelopeJson, WalletEvents.Json)!;
            await broker.PublishAsync(e, new TraceHeaders(Activity.Current?.Id ?? headers.TraceParent, e.CorrelationId, e.CausationId.ToString("D")), timeout.Token); // returns only after a confirm; mandatory rejects unroutable messages.
            await checkpoint.ReachedAsync("AfterConfirm", e.EventId, ct);
            await db.Messages.Where(x => x.EventId == e.EventId && x.LeaseToken == token && x.PublishedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.PublishedAt, clock.GetUtcNow())
                    .SetProperty(x => x.LeaseToken, (Guid?)null).SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(x => x.LastError, (string?)null), ct);
        }
        catch (MessagingCrashException) { throw; }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            span.Complete(OperationOutcome.Failed); telemetry.PublishFailures.Add(1);
            // Do not persist exception messages: connection errors can contain credentials.
            await db.Messages.Where(x => x.EventId == message.EventId && x.LeaseToken == token && x.PublishedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAttemptAt, clock.GetUtcNow().Add(options.Backoff(message.Attempts)))
                    .SetProperty(x => x.LastError, ex.GetType().Name).SetProperty(x => x.LeaseToken, (Guid?)null)
                    .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null), ct);
        }
        return true;
    }
}
