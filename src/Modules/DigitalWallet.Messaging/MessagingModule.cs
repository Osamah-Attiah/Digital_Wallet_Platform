using System.Diagnostics.Metrics;
using DigitalWallet.Messaging.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DigitalWallet.Messaging;

public sealed class MessagingTelemetry : IDisposable
{
    private readonly Meter _meter = new("DigitalWallet.Messaging", "1.0");
    private MessagingStatus _status = new(0, 0, 0, 0, null, false);
    public Counter<long> Redeliveries { get; }
    public Counter<long> ConsumerFailures { get; }
    public Histogram<double> ConsumerDuration { get; }
    public Histogram<double> ConsumerLag { get; }
    private long notificationDepth, reportDepth;
    public Counter<long> PublishAttempts { get; }
    public Counter<long> PublishFailures { get; }
    public Counter<long> DeliveryAttempts { get; }
    public Counter<long> Quarantined { get; }
    public MessagingTelemetry()
    {
        Redeliveries = _meter.CreateCounter<long>("messaging.redeliveries");
        ConsumerFailures = _meter.CreateCounter<long>("messaging.consumer.failures");
        ConsumerDuration = _meter.CreateHistogram<double>("messaging.consumer.duration", "s");
        ConsumerLag = _meter.CreateHistogram<double>("messaging.consumer.lag", "s");
        _meter.CreateObservableGauge("messaging.queue.depth", () => new[] { new Measurement<long>(Interlocked.Read(ref notificationDepth), new KeyValuePair<string, object?>("consumer", "notifications")), new Measurement<long>(Interlocked.Read(ref reportDepth), new KeyValuePair<string, object?>("consumer", "reporting")) });
        PublishAttempts = _meter.CreateCounter<long>("outbox.publish.attempts");
        PublishFailures = _meter.CreateCounter<long>("outbox.publish.failures");
        DeliveryAttempts = _meter.CreateCounter<long>("messaging.delivery.attempts");
        Quarantined = _meter.CreateCounter<long>("messaging.quarantined");
        _meter.CreateObservableGauge("outbox.pending", () => Volatile.Read(ref _status).PendingCount);
        _meter.CreateObservableGauge("outbox.failed", () => Volatile.Read(ref _status).FailedCount);
        _meter.CreateObservableGauge("outbox.oldest_unpublished.seconds", () => Volatile.Read(ref _status).OldestUnpublishedSeconds);
        _meter.CreateObservableGauge("messaging.broker.available", () => Volatile.Read(ref _status).BrokerAvailable ? 1 : 0);
        _meter.CreateObservableGauge("messaging.dlq", () => Volatile.Read(ref _status).DlqCount is { } n
            ? new[] { new Measurement<long>(n) } : Array.Empty<Measurement<long>>());
    }
    public void Depth(uint n, uint r) { Interlocked.Exchange(ref notificationDepth, n); Interlocked.Exchange(ref reportDepth, r); }
    public void Update(MessagingStatus status) => Volatile.Write(ref _status, status);
    public void Dispose() => _meter.Dispose();
}
public sealed class MessagingHealth(OutboxDbContext db, RabbitTransport rabbit, TimeProvider clock, MessagingTelemetry telemetry) : IMessagingMonitor
{
    public async Task<MessagingStatus> GetAsync(CancellationToken ct = default)
    {
        var pending = db.Messages.Where(x => x.PublishedAt == null);
        var count = await pending.LongCountAsync(ct); var failed = await pending.LongCountAsync(x => x.LastError != null, ct);
        var oldest = await pending.Select(x => (DateTimeOffset?)x.OccurredAt).MinAsync(ct);
        var attempts = await db.Messages.Select(x => (long?)x.Attempts).SumAsync(ct) ?? 0;
        uint? dlq = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(3));
            dlq = await rabbit.DeadLetterCountAsync(timeout.Token);
            var depth = await rabbit.DepthAsync(timeout.Token); telemetry.Depth(depth.Notifications, depth.Reports);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { }
        var status = new MessagingStatus(count, failed, attempts, oldest is { } at ? Math.Max(0, (clock.GetUtcNow() - at).TotalSeconds) : 0, dlq, dlq != null);
        telemetry.Update(status); return status;
    }
}
public static class MessagingModule
{
    public static IServiceCollection AddMessagingModule(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(_ =>
        {
            var options = config.GetSection("Messaging").Get<MessagingOptions>() ?? new(); options.Validate(); return options;
        });
        services.AddSingleton<RabbitTransport>();
        services.AddSingleton<IMessagePublisher>(s => s.GetRequiredService<RabbitTransport>());
        services.AddSingleton<MessagingTelemetry>(); services.TryAddSingleton<IMessagingCheckpoint, NoopMessagingCheckpoint>();
        services.AddDbContext<OutboxDbContext>(x => x.UseNpgsql(config["Platform:PostgreSql:ConnectionString"],
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", OutboxDbContext.Schema)));
        services.AddScoped<IMessagingMonitor>(s => s.GetRequiredService<MessagingHealth>());
        services.AddScoped<OutboxPublisher>(); services.AddScoped<MessagingHealth>(); services.AddSingleton<RabbitConsumerPump>();
        if (config["Hosting:Role"] is null or "Combined" or "Worker")
        {
        services.AddHostedService<OutboxWorker>(); services.AddHostedService<NotificationDeliveryWorker>();
        services.AddHostedService<MessagingMetricsWorker>();
        services.AddHostedService<ReportDeliveryWorker>();
        }
        return services;
    }
    public static async Task InitializeMessagingModuleAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OutboxDbContext>().Database.MigrateAsync(ct);
    }
}
public sealed class OutboxWorker(IServiceScopeFactory scopes, MessagingOptions options, ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.WorkersEnabled) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<OutboxPublisher>().PublishOneAsync(ct)) continue;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { logger.LogWarning(new EventId(4001, "OutboxCycleFailed"), "{Module} {Outcome} {ErrorCode} {ActorType}", "Messaging", "Failed", "DEPENDENCY_UNAVAILABLE", "Worker"); }
            try { await Task.Delay(options.PollMilliseconds, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }
}
public sealed class NotificationDeliveryWorker(RabbitTransport rabbit, RabbitConsumerPump pump, MessagingOptions options,
    ILogger<NotificationDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.WorkersEnabled) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var connection = await rabbit.ConnectAsync(ct); await using var channel = await rabbit.OpenAsync(connection, ct);
                while (!ct.IsCancellationRequested)
                    if (!await pump.ProcessOneAsync(channel, ct)) await Task.Delay(options.PollMilliseconds, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { logger.LogWarning(new EventId(4002, "NotificationConnectionLost"), "{Module} {Outcome} {ErrorCode} {ActorType}", "Notifications", "Failed", "DEPENDENCY_UNAVAILABLE", "Worker"); }
            try { await Task.Delay(options.PollMilliseconds, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }
}
public sealed class MessagingMetricsWorker(IServiceScopeFactory scopes, MessagingOptions options, ILogger<MessagingMetricsWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.WorkersEnabled) return;
        while (!ct.IsCancellationRequested)
        {
            try { await using var scope = scopes.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<MessagingHealth>().GetAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { logger.LogWarning(new EventId(4003, "MessagingMetricsFailed"), "{Module} {Outcome} {ErrorCode} {ActorType}", "Messaging", "Failed", "DEPENDENCY_UNAVAILABLE", "Worker"); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }
}

public sealed class ReportDeliveryWorker(RabbitTransport rabbit,RabbitConsumerPump pump,MessagingOptions options,ILogger<ReportDeliveryWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!options.WorkersEnabled)return;
        while(!ct.IsCancellationRequested)
        {
            try
            {
                await using var connection=await rabbit.ConnectAsync(ct);await using var channel=await rabbit.OpenAsync(connection,ct);
                while(!ct.IsCancellationRequested)if(!await pump.ProcessOneAsync(channel,ct,reports:true))await Task.Delay(options.PollMilliseconds,ct);
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch { logger.LogWarning(new EventId(4004, "ReportConnectionLost"), "{Module} {Outcome} {ErrorCode} {ActorType}", "Reporting", "Failed", "DEPENDENCY_UNAVAILABLE", "Worker"); }
            try{await Task.Delay(options.PollMilliseconds,ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
        }
    }
}
