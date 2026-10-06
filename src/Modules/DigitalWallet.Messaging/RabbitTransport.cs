using System.Diagnostics;
using DigitalWallet.SharedKernel.Observability;
using System.Text.Json;
using DigitalWallet.Messaging.Contracts;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace DigitalWallet.Messaging;

public sealed class RabbitTransport(MessagingOptions options) : IMessagePublisher
{
    public string Exchange => options.Prefix + ".events";
    public string Queue => options.Prefix + ".notifications";
    public string ReportQueue => options.Prefix + ".reports";
    public string ReportDeadQueue => ReportQueue + ".dlq";
    public string ReportRetryQueue(int attempt) => ReportQueue + ".retry." + attempt;
    public string DeadQueue => Queue + ".dlq";
    public string RetryQueue(int attempt) => Queue + ".retry." + attempt;
    public async Task<IConnection> ConnectAsync(CancellationToken ct = default) => await new ConnectionFactory
    {
        Uri = new Uri(options.RabbitUri), AutomaticRecoveryEnabled = false,
        RequestedConnectionTimeout = TimeSpan.FromSeconds(options.PublishTimeoutSeconds),
        ContinuationTimeout = TimeSpan.FromSeconds(options.PublishTimeoutSeconds)
    }.CreateConnectionAsync(ct);
    public async Task<IChannel> OpenAsync(IConnection connection, CancellationToken ct = default)
    {
        var channel = await connection.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        try { await DeclareAsync(channel, ct); return channel; }
        catch { await channel.DisposeAsync(); throw; }
    }
    public async Task DeclareAsync(IChannel channel, CancellationToken ct = default)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);
        await channel.QueueDeclareAsync(ReportQueue,true,false,false,new Dictionary<string,object?>{["x-queue-type"]="quorum",["x-delivery-limit"]=-1},cancellationToken:ct);
        await channel.QueueBindAsync(ReportQueue,Exchange,WalletEvents.ReportRequested,cancellationToken:ct);
        await channel.QueueDeclareAsync(ReportDeadQueue,true,false,false,new Dictionary<string,object?>{["x-queue-type"]="quorum"},cancellationToken:ct);
        for(var i=1;i<options.MaxConsumerAttempts;i++)
            await channel.QueueDeclareAsync(ReportRetryQueue(i),true,false,false,new Dictionary<string,object?>{
                ["x-queue-type"]="quorum",["x-message-ttl"]=(int)options.Backoff(i).TotalMilliseconds,
                ["x-dead-letter-exchange"]=Exchange,["x-dead-letter-routing-key"]=WalletEvents.ReportRequested,
                ["x-dead-letter-strategy"]="at-least-once",["x-overflow"]="reject-publish"},cancellationToken:ct);

        await channel.QueueDeclareAsync(Queue, true, false, false, new Dictionary<string, object?>
        { ["x-queue-type"] = "quorum", ["x-delivery-limit"] = -1 }, cancellationToken: ct);
        // Bind all versions/types: unknown contracts must reach quarantine, never vanish as unroutable.
        await channel.QueueBindAsync(Queue, Exchange, "#", cancellationToken: ct);
        await channel.QueueDeclareAsync(DeadQueue, true, false, false, new Dictionary<string, object?>
        { ["x-queue-type"] = "quorum" }, cancellationToken: ct);
        for (var i = 1; i < options.MaxConsumerAttempts; i++)
            await channel.QueueDeclareAsync(RetryQueue(i), true, false, false, new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum", ["x-message-ttl"] = (int)options.Backoff(i).TotalMilliseconds,
                ["x-dead-letter-exchange"] = Exchange, ["x-dead-letter-routing-key"] = "retry",
                ["x-dead-letter-strategy"] = "at-least-once", ["x-overflow"] = "reject-publish"
            }, cancellationToken: ct);
    }
    public Task PublishAsync(EventEnvelope envelope, CancellationToken ct) => PublishAsync(envelope, TraceHeaders.Capture(envelope), ct);
    public async Task PublishAsync(EventEnvelope envelope, TraceHeaders headers, CancellationToken ct)
    {
        using var span = WalletTelemetry.Start(TelemetryModule.Messaging, OperationName.Publish, ActivityKind.Producer, headers.Parent);
        await using var connection = await ConnectAsync(ct);
        await using var channel = await OpenAsync(connection, ct);
        await channel.BasicPublishAsync(Exchange, envelope.EventType, mandatory: true,
            Properties(envelope, new(Activity.Current?.Id ?? headers.TraceParent, envelope.CorrelationId, envelope.CausationId.ToString("D"))), JsonSerializer.SerializeToUtf8Bytes(envelope, WalletEvents.Json), ct);
    }
    public static BasicProperties Properties(EventEnvelope e, TraceHeaders? trace = null) => new()
    {
        Headers = (trace ?? TraceHeaders.Capture(e)).ToRabbit(),
        Persistent = true, ContentType = "application/json", MessageId = e.EventId.ToString("D"),
        Type = e.EventType, CorrelationId = e.CorrelationId, Timestamp = new AmqpTimestamp(e.OccurredAt.ToUnixTimeSeconds())
    };
    private Task<IChannel> OpenMonitorAsync(IConnection connection, CancellationToken ct) => options.MonitorDeclaresTopology ? OpenAsync(connection, ct) : connection.CreateChannelAsync(cancellationToken: ct);
    public async Task<(uint Notifications, uint Reports)> DepthAsync(CancellationToken ct)
    {
        await using var c = await ConnectAsync(ct); await using var ch = await OpenMonitorAsync(c, ct);
        return (await ch.MessageCountAsync(Queue, ct), await ch.MessageCountAsync(ReportQueue, ct));
    }
    public async Task<uint> DeadLetterCountAsync(CancellationToken ct = default)
    {
        await using var connection = await ConnectAsync(ct); await using var channel = await OpenMonitorAsync(connection, ct);
        var notifications = await channel.MessageCountAsync(DeadQueue, ct);
        var reports = await channel.MessageCountAsync(ReportDeadQueue, ct);
        return checked(notifications + reports);
    }
}

// One in-flight delivery per pump/channel, manual ack only after consumer DB commit.
public sealed class RabbitConsumerPump(RabbitTransport transport, IServiceScopeFactory scopes, MessagingOptions options,
    IMessagingCheckpoint checkpoint, MessagingTelemetry telemetry)
{
    public async Task<bool> ProcessOneAsync(IChannel channel, CancellationToken ct = default, bool reports = false)
    {
        var delivery = await channel.BasicGetAsync(reports ? transport.ReportQueue : transport.Queue, autoAck: false, ct);
        if (delivery is null) return false;
        telemetry.DeliveryAttempts.Add(1);
        if (delivery.Redelivered || delivery.BasicProperties.Headers?.ContainsKey("x-delivery-attempt") == true) telemetry.Redeliveries.Add(1);
        var start = Stopwatch.GetTimestamp();
        EventEnvelope? envelope = null;
        string? failure = null; var permanent = false;
        try
        {
            if (delivery.Body.Length > 16_384) throw new InvalidEventException();
            envelope = JsonSerializer.Deserialize<EventEnvelope>(delivery.Body.Span, WalletEvents.Json) ?? throw new InvalidEventException();
            WalletEvents.Validate(envelope);
            if (delivery.BasicProperties.MessageId != envelope.EventId.ToString("D")) throw new InvalidEventException();
            var headers = TraceHeaders.Read(delivery.BasicProperties.Headers, envelope);
            using var span = WalletTelemetry.Start(TelemetryModule.Messaging, OperationName.Consume, ActivityKind.Consumer, headers.Parent);
            Activity.Current?.SetTag("correlation.id", headers.CorrelationId);
            Activity.Current?.SetTag("causation.id", headers.CausationId);
            Activity.Current?.SetTag("messaging.system", "rabbitmq");
            Activity.Current?.SetTag("messaging.redelivered", delivery.Redelivered);
            try
            {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetServices<IEventConsumer>().Single(x => x.Name == (reports ? "reporting.csv.v1" : "notifications.inapp.v1")).ConsumeAsync(envelope, ct);
            await checkpoint.ReachedAsync("AfterConsumerCommit", envelope.EventId, ct);
            }
            catch { span.Complete(OperationOutcome.Failed); throw; }
        }
        catch (Exception ex) when (ex is MessagingCrashException || ex.GetType().Name == "ReportCrashException") { throw; }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            permanent = ex is UnsupportedEventException or InvalidEventException or JsonException;
            failure = permanent ? "INVALID_EVENT" : "CONSUMER_FAILED";
            telemetry.ConsumerFailures.Add(1, new KeyValuePair<string, object?>("consumer", reports ? "reporting" : "notifications"));
            if (permanent) WalletTelemetry.Security(SecurityEvent.IntegrationRejected, TelemetryModule.Messaging);
        }
        if (failure is not null)
        {
            var attempt = 1;
            if (delivery.BasicProperties.Headers?.TryGetValue("x-delivery-attempt", out var value) == true && value is int n && n > 0)
                attempt = n;
            var dead = permanent || attempt >= options.MaxConsumerAttempts;
            var properties = new BasicProperties(delivery.BasicProperties) { Persistent = true, Expiration = null, UserId = null };
            properties.Headers = envelope == null ? new Dictionary<string, object?>() : new TraceHeaders(
                TraceHeaders.Read(null, envelope).TraceParent, envelope.CorrelationId, envelope.CausationId.ToString("D")).ToRabbit();
            if (delivery.BasicProperties.Headers?.TryGetValue("traceparent", out var originalTrace) == true && (originalTrace is string { Length: <= 55 } || originalTrace is byte[] { Length: <= 55 })) properties.Headers["traceparent"] = originalTrace;
            properties.Headers = new Dictionary<string, object?>(properties.Headers)
            { ["x-delivery-attempt"] = attempt + 1, ["x-failure-code"] = failure };
            // A confirm BEFORE ack avoids losing poison/retry deliveries. A crash here can duplicate them.
            await channel.BasicPublishAsync("", reports ? (dead ? transport.ReportDeadQueue : transport.ReportRetryQueue(attempt)) : (dead ? transport.DeadQueue : transport.RetryQueue(attempt)), true,
                properties, delivery.Body, ct);
            if (dead) { telemetry.Quarantined.Add(1); WalletTelemetry.Security(SecurityEvent.DeadLettered, TelemetryModule.Messaging); }
        }
        telemetry.ConsumerDuration.Record(Stopwatch.GetElapsedTime(start).TotalSeconds, new KeyValuePair<string, object?>("consumer", reports ? "reporting" : "notifications"));
        telemetry.ConsumerLag.Record(envelope == null ? 0 : Math.Max(0, (DateTimeOffset.UtcNow - envelope.OccurredAt).TotalSeconds), new KeyValuePair<string, object?>("consumer", reports ? "reporting" : "notifications"));
        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
        return true;
    }
}
