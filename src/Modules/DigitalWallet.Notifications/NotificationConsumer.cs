using DigitalWallet.SharedKernel.Observability;
using DigitalWallet.Messaging.Contracts;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace DigitalWallet.Notifications;

public interface INotificationCheckpoint { Task ReachedAsync(string point, Guid eventId, CancellationToken ct); }
public sealed class NoopNotificationCheckpoint : INotificationCheckpoint
{ public Task ReachedAsync(string point, Guid eventId, CancellationToken ct) => Task.CompletedTask; }
public sealed class NotificationConsumer(NotificationsDbContext db, TimeProvider clock, INotificationCheckpoint checkpoint) : IEventConsumer
{
    public const string ConsumerName = "notifications.inapp.v1";
    public string Name => ConsumerName;
    public Task ConsumeAsync(EventEnvelope e, CancellationToken ct) => WalletTelemetry.RunAsync(TelemetryModule.Notifications, OperationName.Consume, () => ConsumeAsyncCore(e, ct));
    private async Task ConsumeAsyncCore(EventEnvelope e, CancellationToken ct)
    {
        WalletEvents.Validate(e);
        if(e.EventType == WalletEvents.ReportRequested) return;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notifications.inbox ("ConsumerName", "EventId", "ProcessedAt")
            VALUES ({Name}, {e.EventId}, {clock.GetUtcNow()}) ON CONFLICT ("ConsumerName", "EventId") DO NOTHING
            """, ct);
        if (inserted == 0) { await transaction.CommitAsync(ct); return; }
        Guid sender, recipient; long amount; string currency;
        if (e.EventType == WalletEvents.TransferCompleted)
        {
            var p = e.Payload.Deserialize<TransferCompletedV1>(WalletEvents.Json)!;
            (sender, recipient, amount, currency) = (p.SenderCustomerId, p.RecipientCustomerId, p.AmountMinor, p.Currency);
        }
        else
        {
            var p = e.Payload.Deserialize<ReversalCompletedV1>(WalletEvents.Json)!;
            (sender, recipient, amount, currency) = (p.SenderCustomerId, p.RecipientCustomerId, p.AmountMinor, p.Currency);
        }
        foreach (var (customer, direction) in new[] { (sender, "Outgoing"), (recipient, "Incoming") })
        {
            var p = await db.Preferences.AsNoTracking().SingleOrDefaultAsync(x => x.CustomerId == customer, ct);
            if (p is not null && (!p.InAppEnabled || (e.EventType == WalletEvents.TransferCompleted ? !p.TransfersEnabled : !p.ReversalsEnabled))) continue;
            db.Notifications.Add(new InAppNotification { Id = Guid.NewGuid(), CustomerId = customer, EventId = e.EventId,
                AggregateId = e.AggregateId, Kind = e.EventType, Direction = direction, AmountMinor = amount, Currency = currency,
                CreatedAt = Microseconds(clock.GetUtcNow()) });
        }
        await db.SaveChangesAsync(ct);
        await checkpoint.ReachedAsync("BeforeNotificationCommit", e.EventId, ct);
        await transaction.CommitAsync(ct); // Inbox AND effects commit here; RabbitMQ acknowledgement follows outside.
    }
    private static DateTimeOffset Microseconds(DateTimeOffset time) => time.AddTicks(-(time.Ticks % 10));
}

// External channels deliberately have no transport or provider implementation in phase nine.
public interface IExternalNotificationChannel { Task<string> DeliverAsync(Guid notificationId, CancellationToken ct); }
public sealed class ExternalNotificationStub : IExternalNotificationChannel
{ public Task<string> DeliverAsync(Guid notificationId, CancellationToken ct) => Task.FromResult("StubOnly"); }
