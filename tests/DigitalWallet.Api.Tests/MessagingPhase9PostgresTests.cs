using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalWallet.Messaging;
using DigitalWallet.Messaging.Contracts;
using DigitalWallet.Notifications;
using DigitalWallet.Transactions.Application;
using DigitalWallet.Transfers.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Xunit;

namespace DigitalWallet.Api.Tests;

public sealed partial class TransfersPostgresTests
{
    private Task<T> OutboxDb<T>(Func<OutboxDbContext, Task<T>> action) => Scope(sp => action(sp.GetRequiredService<OutboxDbContext>()));
    private Task<T> NotificationsDb<T>(Func<NotificationsDbContext, Task<T>> action) => Scope(sp => action(sp.GetRequiredService<NotificationsDbContext>()));
    private Task<bool> PublishEvent() => Scope(sp => sp.GetRequiredService<OutboxPublisher>().PublishOneAsync());
    private RabbitTransport Rabbit => _factory.Services.GetRequiredService<RabbitTransport>();
    private async Task<bool> ConsumeEvent()
    {
        await using var connection = await Rabbit.ConnectAsync(); await using var channel = await Rabbit.OpenAsync(connection);
        return await _factory.Services.GetRequiredService<RabbitConsumerPump>().ProcessOneAsync(channel);
    }
    private static async Task Eventually(Func<Task<bool>> predicate, int timeoutMilliseconds = 12000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTime.UtcNow < until) { if (await predicate()) return; await Task.Delay(50); }
        Assert.True(await predicate(), "Expected condition did not become true before the deadline.");
    }
    private async Task ConsumeEventually() => await Eventually(ConsumeEvent);
    private Task<OutboxMessage> OnlyMessage() => OutboxDb(db => db.Messages.AsNoTracking().SingleAsync());

    [Fact]
    public async Task Phase9_migrations_are_repeatable_and_models_match_PostgreSQL()
    {
        await _factory.Services.InitializeMessagingModuleAsync(); await _factory.Services.InitializeNotificationsModuleAsync();
        await OutboxDb(async db => { Assert.False(db.Database.HasPendingModelChanges()); Assert.Empty(await db.Database.GetPendingMigrationsAsync()); return true; });
        await NotificationsDb(async db => { Assert.False(db.Database.HasPendingModelChanges()); Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.All(db.Model.GetEntityTypes(), e => Assert.Equal("notifications", e.GetSchema())); return true; });
    }
    [Fact]
    public async Task Completed_transfer_has_one_safe_versioned_event_and_repeated_idempotent_request_has_no_duplicate()
    {
        var s = await SetupAsync(); var quote = await Quote(s); var t = await Read<TransferReceipt>(await CreateResponse(s.A, quote.Id, "phase9", "Private secret note"), HttpStatusCode.Created);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/transfers/{t.Id}/confirm") { Content = JsonContent.Create(new { code = await Code(t.Id) }) };
        request.Headers.Add("X-Correlation-ID", "phase9_transfer");
        await Read<TransferReceipt>(await s.A.Client.SendAsync(request));
        var row = await OnlyMessage(); var e = JsonSerializer.Deserialize<EventEnvelope>(row.EnvelopeJson, WalletEvents.Json)!;
        Assert.Equal(t.Id, e.AggregateId); Assert.Equal(t.Id, e.CausationId); Assert.Equal("phase9_transfer", e.CorrelationId);
        Assert.Equal(1, e.EventVersion); Assert.Equal(WalletEvents.TransferCompleted, e.EventType); Assert.Equal(TimeSpan.Zero, e.OccurredAt.Offset);
        Assert.Equal(new[] { "amountMinor", "currency", "recipientCustomerId", "senderCustomerId" }, e.Payload.EnumerateObject().Select(x => x.Name).Order());
        foreach (var sensitive in new[] { "otp", "token", "password", "email", "phone", "name", "note", "challenge", "Private", s.A.Email, s.B.Email }) Assert.DoesNotContain(sensitive, row.EnvelopeJson, StringComparison.OrdinalIgnoreCase);
        await Read<TransferReceipt>(await Confirm(s.A, t.Id));
        await Read<TransferReceipt>(await CreateResponse(s.A, quote.Id, "phase9", "Private secret note"), HttpStatusCode.Created);
        Assert.Equal(row.EventId, (await OnlyMessage()).EventId); Assert.Equal(row.EnvelopeJson, (await OnlyMessage()).EnvelopeJson);
        Assert.Equal(1, await Journals());
    }
    [Theory]
    [InlineData("AfterLedger")]
    [InlineData("AfterHistory")]
    [InlineData("AfterOutbox")]
    public async Task Transfer_rollback_never_leaves_an_outbox_fact(string point)
    {
        var s = await SetupAsync(); var t = await Intent(s); _factory.TransferFaults.Fail(point);
        Assert.Equal(HttpStatusCode.InternalServerError, (await Confirm(s.A, t.Id)).StatusCode);
        Assert.Equal(0, await OutboxDb(db => db.Messages.CountAsync())); await NoFinancialEffect(t.Id, 10000, s.A);
        await Read<TransferReceipt>(await Confirm(s.A, t.Id)); Assert.Single(await OutboxDb(db => db.Messages.ToListAsync()));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_outbox_insert_failure_rolls_back_transfer_or_reversal(bool reversal)
    {
        Setup s; Guid id; Func<Task<HttpResponseMessage>> operation;
        if (reversal)
        {
            var w = await Workflow(); s = w.Setup; id = w.Request.Id; var proof = await Proof(w.Checker, id);
            operation = () => Approve(w.Checker, id, proof);
        }
        else { s = await SetupAsync(); var t = await Intent(s); id = t.Id; operation = () => Confirm(s.A, id); }
        await using var connection = new NpgsqlConnection(_connection); await connection.OpenAsync();
        await using (var setup = new NpgsqlCommand("""
            CREATE FUNCTION messaging.test_fail_insert() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Injected outbox write failure'; END; $$;
            CREATE TRIGGER test_fail_insert BEFORE INSERT ON messaging.outbox FOR EACH ROW EXECUTE FUNCTION messaging.test_fail_insert();
            """, connection)) await setup.ExecuteNonQueryAsync();
        try
        {
            Assert.Equal(HttpStatusCode.InternalServerError, (await operation()).StatusCode);
            Assert.Equal(reversal ? 9000 : 10000, await Balance(s.A)); Assert.Equal(reversal ? 1000 : 0, await Balance(s.B));
            Assert.Equal(reversal ? 1 : 0, await OutboxDb(db => db.Messages.CountAsync()));
            Assert.Equal(reversal ? 1 : 0, await HistoryDb(db => db.Transactions.CountAsync()));
            if (!reversal) await NoFinancialEffect(id, 10000, s.A);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand("DROP TRIGGER test_fail_insert ON messaging.outbox; DROP FUNCTION messaging.test_fail_insert();", connection); await cleanup.ExecuteNonQueryAsync();
        }
        await operation(); Assert.Equal(reversal ? 10000 : 9000, await Balance(s.A));
        Assert.Equal(reversal ? 2 : 1, await OutboxDb(db => db.Messages.CountAsync()));
    }
    [Fact]
    public async Task Reversal_outbox_rollback_replay_and_notifications_preserve_maker_checker_and_money()
    {
        var w = await Workflow(); var proof = await Proof(w.Checker, w.Request.Id);
        _factory.ReversalFaults.Fail("AfterOutbox"); Assert.Equal(HttpStatusCode.InternalServerError, (await Approve(w.Checker, w.Request.Id, proof)).StatusCode);
        Assert.Equal(1, await OutboxDb(db => db.Messages.CountAsync())); Assert.Equal(9000, await Balance(w.Setup.A));
        await Read<ReversalReceipt>(await Approve(w.Checker, w.Request.Id, proof));
        await Read<ReversalReceipt>(await Approve(w.Checker, w.Request.Id, proof)); Assert.Equal(2, await OutboxDb(db => db.Messages.CountAsync()));
        while (await PublishEvent()) { }
        await ConsumeEventually(); await ConsumeEventually();
        var a = await Read<NotificationPage>(await w.Setup.A.Client.GetAsync("/api/v1/notifications"));
        var b = await Read<NotificationPage>(await w.Setup.B.Client.GetAsync("/api/v1/notifications"));
        Assert.Equal(2, a.Items.Count); Assert.Equal(2, b.Items.Count);
        Assert.Equal("Incoming", a.Items.Single(x => x.Kind == WalletEvents.ReversalCompleted).Direction);
        Assert.Equal("Outgoing", b.Items.Single(x => x.Kind == WalletEvents.ReversalCompleted).Direction);
        Assert.Equal(0, await NotificationsDb(db => db.Notifications.CountAsync(x => x.CustomerId == w.Checker.CustomerId || x.CustomerId == w.Maker.CustomerId)));
        Assert.Equal(10000, await Balance(w.Setup.A)); Assert.Equal(0, await Balance(w.Setup.B));
    }
    [Fact]
    public async Task RabbitMQ_outage_keeps_committed_money_and_pending_event_with_exponential_backoff()
    {
        var s = await SetupAsync(); await Complete(s); var row = await OnlyMessage();
        await using (var unavailable = new IdentityPostgresFactory(_connection, new Dictionary<string, string?>
        { ["Messaging:RabbitUri"] = "amqp://guest:guest@127.0.0.1:1/", ["Messaging:PublishTimeoutSeconds"] = "1", ["Messaging:LeaseSeconds"] = "4", ["Messaging:RetryBaseMilliseconds"] = "150" }))
        {
            unavailable.Time.SetUtcNow(_factory.Time.GetUtcNow());
            await using var scope = unavailable.Services.CreateAsyncScope(); var publisher = scope.ServiceProvider.GetRequiredService<OutboxPublisher>();
            Assert.True(await publisher.PublishOneAsync()); var failed = await OnlyMessage();
            Assert.Null(failed.PublishedAt); Assert.NotNull(failed.LastError); Assert.Equal(1, failed.Attempts); Assert.Null(failed.LeaseToken);
            Assert.InRange((failed.NextAttemptAt - unavailable.Time.GetUtcNow()).TotalMilliseconds, 149, 151);
            Assert.False(await publisher.PublishOneAsync()); unavailable.Time.Advance(TimeSpan.FromMilliseconds(151));
            Assert.True(await publisher.PublishOneAsync()); failed = await OnlyMessage(); Assert.Equal(2, failed.Attempts);
            Assert.InRange((failed.NextAttemptAt - unavailable.Time.GetUtcNow()).TotalMilliseconds, 299, 301);
            var status = await scope.ServiceProvider.GetRequiredService<MessagingHealth>().GetAsync(); Assert.False(status.BrokerAvailable); Assert.Null(status.DlqCount); Assert.Equal(1, status.PendingCount); Assert.Equal(1, status.FailedCount);
            _factory.Time.SetUtcNow(unavailable.Time.GetUtcNow().AddMilliseconds(301));
        }
        Assert.Equal(9000, await Balance(s.A)); Assert.Equal(1000, await Balance(s.B));
        Assert.True(await PublishEvent()); Assert.Equal(row.EventId, (await OnlyMessage()).EventId); Assert.NotNull((await OnlyMessage()).PublishedAt);
        await ConsumeEventually(); Assert.Equal(2, await NotificationsDb(db => db.Notifications.CountAsync()));
    }
    [Theory]
    [InlineData("AfterClaim")]
    [InlineData("AfterConfirm")]
    public async Task Publisher_crash_and_worker_restart_reclaim_lease_with_same_event_id_and_single_logical_notification(string point)
    {
        var s = await SetupAsync(); await Complete(s); var original = await OnlyMessage();
        _factory.MessagingFaults.Fail(point, crash: true);
        await Assert.ThrowsAsync<MessagingCrashException>(PublishEvent);
        var leased = await OnlyMessage(); Assert.NotNull(leased.LeaseToken); Assert.Null(leased.PublishedAt); Assert.False(await PublishEvent());
        if (point == "AfterConfirm") { await ConsumeEventually(); Assert.Equal(2, await NotificationsDb(db => db.Notifications.CountAsync())); }
        _factory.MessagingFaults.Reset(); _factory.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.True(await PublishEvent()); await ConsumeEventually();
        var published = await OnlyMessage(); Assert.Equal(original.EventId, published.EventId); Assert.Equal(original.EnvelopeJson, published.EnvelopeJson);
        Assert.Equal(2, published.Attempts); Assert.NotNull(published.PublishedAt);
        Assert.Equal(1, await NotificationsDb(db => db.Inbox.CountAsync())); Assert.Equal(2, await NotificationsDb(db => db.Notifications.CountAsync()));
        Assert.Equal(9000, await Balance(s.A));
    }
    [Fact]
    public async Task Stopped_real_RabbitMQ_container_cannot_block_money_and_durable_delivery_survives_broker_restart()
    {
        var s = await SetupAsync(); var pending = await Intent(s);
        await _rabbitFixture.StopAsync();
        try
        {
            await Read<TransferReceipt>(await Confirm(s.A, pending.Id)); Assert.True(await PublishEvent());
            Assert.Null((await OnlyMessage()).PublishedAt); Assert.NotNull((await OnlyMessage()).LastError);
            Assert.Equal(9000, await Balance(s.A)); Assert.Equal(1000, await Balance(s.B));
        }
        finally { await _rabbitFixture.StartAsync(); }
        _factory.Services.GetRequiredService<MessagingOptions>().RabbitUri = _rabbitFixture.ConnectionString;
        _factory.Time.Advance(TimeSpan.FromSeconds(1)); Assert.True(await PublishEvent()); Assert.NotNull((await OnlyMessage()).PublishedAt);
        await _rabbitFixture.StopAsync(); await _rabbitFixture.StartAsync();
        _factory.Services.GetRequiredService<MessagingOptions>().RabbitUri = _rabbitFixture.ConnectionString;
        await ConsumeEventually(); Assert.Equal(2, await NotificationsDb(db => db.Notifications.CountAsync()));
        Assert.Equal(1, await NotificationsDb(db => db.Inbox.CountAsync()));
    }
    [Fact]
    public async Task Consumer_crash_after_notification_commit_before_ack_redelivers_without_duplicate()
    {
        var s = await SetupAsync(); await Complete(s); await PublishEvent();
        _factory.MessagingFaults.Fail("AfterConsumerCommit", crash: true);
        await Assert.ThrowsAsync<MessagingCrashException>(async () => { await Eventually(ConsumeEvent); });
        Assert.Equal(2, await NotificationsDb(db => db.Notifications.CountAsync()));
        _factory.MessagingFaults.Reset(); await ConsumeEventually();
        Assert.Equal(1, await NotificationsDb(db => db.Inbox.CountAsync())); Assert.Equal(2, await NotificationsDb(db => db.Notifications.CountAsync()));
        Assert.False(await ConsumeEvent());
    }
    [Fact]
    public async Task Many_concurrent_publishers_claim_each_event_once_and_inbox_handles_concurrent_duplicates()
    {
        var s = await SetupAsync(); for (var i = 0; i < 16; i++) await Complete(s, 100);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ => { while (await PublishEvent()) { } }));
        var messages = await OutboxDb(db => db.Messages.AsNoTracking().ToListAsync());
        Assert.Equal(16, messages.Count); Assert.All(messages, m => { Assert.Equal(1, m.Attempts); Assert.NotNull(m.PublishedAt); Assert.Null(m.LeaseToken); });
        await using var connection = await Rabbit.ConnectAsync(); await using var channel = await Rabbit.OpenAsync(connection);
        Assert.Equal(16u, await channel.MessageCountAsync(Rabbit.Queue));
        foreach (var m in messages) await ConsumeEventually();
        var envelope = JsonSerializer.Deserialize<EventEnvelope>(messages[0].EnvelopeJson, WalletEvents.Json)!;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Scope(async sp => { await sp.GetRequiredService<IEventConsumer>().ConsumeAsync(envelope, default); return true; })));
        Assert.Equal(16, await NotificationsDb(db => db.Inbox.CountAsync())); Assert.Equal(32, await NotificationsDb(db => db.Notifications.CountAsync()));
    }
    [Fact]
    public async Task Claim_skips_locked_rows_and_outbox_facts_cannot_be_changed_after_creation()
    {
        var s = await SetupAsync(); await Complete(s); await Complete(s);
        var messages = await OutboxDb(db => db.Messages.AsNoTracking().OrderBy(x => x.NextAttemptAt).ThenBy(x => x.EventId).ToListAsync());
        await using var connection = new NpgsqlConnection(_connection); await connection.OpenAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var locked = new NpgsqlCommand("SELECT \"EventId\" FROM messaging.outbox WHERE \"EventId\" = @id FOR UPDATE", connection, transaction);
            locked.Parameters.AddWithValue("id", messages[0].EventId); await locked.ExecuteScalarAsync();
            Assert.True(await PublishEvent());
            Assert.NotNull(await OutboxDb(db => db.Messages.Where(x => x.EventId == messages[1].EventId).Select(x => x.PublishedAt).SingleAsync()));
            Assert.False(await PublishEvent()); await transaction.CommitAsync();
        }
        Assert.True(await PublishEvent());
        foreach (var sql in new[] { "UPDATE messaging.outbox SET \"EnvelopeJson\" = '{}'", "UPDATE messaging.outbox SET \"EventId\" = gen_random_uuid()", "UPDATE messaging.outbox SET \"PublishedAt\" = NULL", "UPDATE messaging.outbox SET \"Attempts\" = 0" })
        {
            await using var mutate = new NpgsqlCommand(sql, connection);
            Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => mutate.ExecuteNonQueryAsync())).SqlState);
        }
    }
    [Fact]
    public async Task Mandatory_publish_waits_for_confirm_and_rejects_unroutable_messages()
    {
        await using var connection = await Rabbit.ConnectAsync(); await using var channel = await Rabbit.OpenAsync(connection);
        var e = WalletEvents.Transfer(Guid.NewGuid(), DateTimeOffset.UtcNow, "confirms", new(Guid.NewGuid(), Guid.NewGuid(), 10, "SAR"));
        var ex = await Assert.ThrowsAnyAsync<PublishException>(async () => await channel.BasicPublishAsync("", "missing." + Guid.NewGuid().ToString("N"), true,
            RabbitTransport.Properties(e), JsonSerializer.SerializeToUtf8Bytes(e, WalletEvents.Json)));
        Assert.True(ex.IsReturn);
    }
    [Fact]
    public async Task Notification_effect_and_inbox_roll_back_together_then_retry_queue_recovers()
    {
        var s = await SetupAsync(); await Complete(s); await PublishEvent();
        _factory.MessagingFaults.Fail("BeforeNotificationCommit"); await ConsumeEventually();
        Assert.Equal(0, await NotificationsDb(db => db.Notifications.CountAsync())); Assert.Equal(0, await NotificationsDb(db => db.Inbox.CountAsync()));
        await ConsumeEventually(); Assert.Equal(2, await NotificationsDb(db => db.Notifications.CountAsync())); Assert.Equal(1, await NotificationsDb(db => db.Inbox.CountAsync()));
        Assert.Equal(0u, await Rabbit.DeadLetterCountAsync());
    }
    [Fact]
    public async Task Poison_event_exhausts_bounded_retries_and_is_confirmed_into_durable_DLQ()
    {
        var s = await SetupAsync(); await Complete(s); await PublishEvent(); _factory.MessagingFaults.Fail("BeforeNotificationCommit", 100);
        await ConsumeEventually(); await ConsumeEventually(); await ConsumeEventually();
        Assert.Equal(1u, await Rabbit.DeadLetterCountAsync()); Assert.Equal(0, await NotificationsDb(db => db.Notifications.CountAsync()));
        Assert.Equal(0, await NotificationsDb(db => db.Inbox.CountAsync()));
        await using var connection = await Rabbit.ConnectAsync(); await using var channel = await Rabbit.OpenAsync(connection);
        var dead = await channel.BasicGetAsync(Rabbit.DeadQueue, false); Assert.NotNull(dead);
        Assert.Equal(4, dead.BasicProperties.Headers!["x-delivery-attempt"]); Assert.True(dead.BasicProperties.Persistent);
        Assert.Equal((await OnlyMessage()).EventId.ToString("D"), dead.BasicProperties.MessageId);
        await channel.BasicAckAsync(dead.DeliveryTag, false);
    }
    [Theory]
    [InlineData(2, WalletEvents.TransferCompleted)]
    [InlineData(1, "FutureEvent")]
    public async Task Unknown_contract_is_quarantined_without_inbox_or_notification(int version, string type)
    {
        var s = await SetupAsync(); await Complete(s); var e = JsonSerializer.Deserialize<EventEnvelope>((await OnlyMessage()).EnvelopeJson, WalletEvents.Json)!;
        await Rabbit.PublishAsync(e with { EventVersion = version, EventType = type }, default); await ConsumeEventually();
        Assert.Equal(1u, await Rabbit.DeadLetterCountAsync()); Assert.Equal(0, await NotificationsDb(db => db.Inbox.CountAsync()));
        Assert.Equal(0, await NotificationsDb(db => db.Notifications.CountAsync()));
    }
    [Theory]
    [InlineData("otp")]
    [InlineData("token")]
    [InlineData("email")]
    public async Task Extra_sensitive_payload_fields_are_rejected_and_quarantined(string key)
    {
        var e = WalletEvents.Transfer(Guid.NewGuid(), DateTimeOffset.UtcNow, "privacy", new(Guid.NewGuid(), Guid.NewGuid(), 10, "SAR"));
        var fields = e.Payload.Deserialize<Dictionary<string, JsonElement>>()!; fields[key] = JsonSerializer.SerializeToElement("private");
        e = e with { Payload = JsonSerializer.SerializeToElement(fields) };
        Assert.Throws<InvalidEventException>(() => WalletEvents.Validate(e));
        await Rabbit.PublishAsync(e, default); await ConsumeEventually(); Assert.Equal(1u, await Rabbit.DeadLetterCountAsync());
    }
    [Fact]
    public async Task Notifications_enforce_BOLA_for_detail_mark_read_preferences_and_cursor()
    {
        var s = await SetupAsync(); await Complete(s); await PublishEvent(); await ConsumeEventually();
        var a = await Read<NotificationPage>(await s.A.Client.GetAsync("/api/v1/notifications")); var id = Assert.Single(a.Items).Id;
        Assert.Null(a.Items[0].ReadAt); await Error(await s.B.Client.GetAsync($"/api/v1/notifications/{id}"), "NOTIFICATION_NOT_FOUND", HttpStatusCode.NotFound);
        await Error(await s.B.Client.PostAsync($"/api/v1/notifications/{id}/read", null), "NOTIFICATION_NOT_FOUND", HttpStatusCode.NotFound);
        var read = await Read<NotificationReceipt>(await s.A.Client.PostAsync($"/api/v1/notifications/{id}/read", null)); Assert.NotNull(read.ReadAt);
        _factory.Time.Advance(TimeSpan.FromSeconds(10)); Assert.Equal(read, await Read<NotificationReceipt>(await s.A.Client.PostAsync($"/api/v1/notifications/{id}/read", null)));
        await Read<PreferenceRequest>(await s.A.Client.PutAsJsonAsync("/api/v1/notifications/preferences", new { customerId = s.B.CustomerId, inAppEnabled = false, transfersEnabled = false, reversalsEnabled = false }));
        Assert.Equal(new(true, true, true), await Read<PreferenceRequest>(await s.B.Client.GetAsync("/api/v1/notifications/preferences")));
        using var anonymous = _factory.CreateClient(); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/notifications/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/v1/notifications/{id}/read", null)).StatusCode);
    }
    [Fact]
    public async Task Notification_keyset_pagination_handles_equal_timestamps_new_inserts_and_foreign_cursors()
    {
        var s = await SetupAsync(); for (var i = 0; i < 5; i++) await Complete(s, 100);
        while (await PublishEvent()) { } for (var i = 0; i < 5; i++) await ConsumeEventually();
        var page = await Read<NotificationPage>(await s.A.Client.GetAsync("/api/v1/notifications?pageSize=2")); Assert.NotNull(page.NextCursor);
        await Error(await s.B.Client.GetAsync("/api/v1/notifications?cursor=" + Uri.EscapeDataString(page.NextCursor!)), "INVALID_CURSOR", HttpStatusCode.BadRequest);
        var ids = page.Items.Select(x => x.Id).ToList(); _factory.Time.Advance(TimeSpan.FromSeconds(1));
        await Complete(s, 100); await PublishEvent(); await ConsumeEventually();
        while (page.NextCursor != null)
        {
            page = await Read<NotificationPage>(await s.A.Client.GetAsync("/api/v1/notifications?pageSize=2&cursor=" + Uri.EscapeDataString(page.NextCursor)));
            ids.AddRange(page.Items.Select(x => x.Id));
        }
        Assert.Equal(5, ids.Count); Assert.Equal(5, ids.Distinct().Count());
        Assert.Equal(6, (await Read<NotificationPage>(await s.A.Client.GetAsync("/api/v1/notifications"))).Items.Count);
        await Error(await s.A.Client.GetAsync("/api/v1/notifications?cursor=invalid"), "INVALID_CURSOR", HttpStatusCode.BadRequest);
        await Error(await s.A.Client.GetAsync("/api/v1/notifications?pageSize=101"), "VALIDATION_ERROR", HttpStatusCode.BadRequest);
    }
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Notification_preferences_suppress_future_effects_and_replay_does_not_override_them(bool inApp, bool transfers)
    {
        var s = await SetupAsync(); await Read<PreferenceRequest>(await s.A.Client.PutAsJsonAsync("/api/v1/notifications/preferences", new PreferenceRequest(inApp, transfers, true)));
        await Complete(s); await PublishEvent(); await ConsumeEventually();
        Assert.Empty((await Read<NotificationPage>(await s.A.Client.GetAsync("/api/v1/notifications"))).Items);
        Assert.Single((await Read<NotificationPage>(await s.B.Client.GetAsync("/api/v1/notifications"))).Items);
        await Read<PreferenceRequest>(await s.A.Client.PutAsJsonAsync("/api/v1/notifications/preferences", new PreferenceRequest(true, true, true)));
        await Rabbit.PublishAsync(JsonSerializer.Deserialize<EventEnvelope>((await OnlyMessage()).EnvelopeJson, WalletEvents.Json)!, default); await ConsumeEventually();
        Assert.Equal(1, await NotificationsDb(db => db.Notifications.CountAsync())); Assert.Equal(1, await NotificationsDb(db => db.Inbox.CountAsync()));
        Assert.Equal("StubOnly", await _factory.Services.GetRequiredService<IExternalNotificationChannel>().DeliverAsync(Guid.NewGuid(), default));
    }
    [Fact]
    public async Task Reversal_can_arrive_before_transfer_and_reversal_preferences_are_applied_without_financial_dependencies()
    {
        var w = await Workflow(); var proof = await Proof(w.Checker, w.Request.Id);
        await Read<ReversalReceipt>(await Approve(w.Checker, w.Request.Id, proof));
        await Read<PreferenceRequest>(await w.Setup.A.Client.PutAsJsonAsync("/api/v1/notifications/preferences", new PreferenceRequest(true, true, false)));
        var messages = await OutboxDb(db => db.Messages.AsNoTracking().OrderBy(x => x.EventType).ToListAsync());
        Assert.Equal(WalletEvents.ReversalCompleted, messages[0].EventType);
        foreach (var message in messages)
        {
            var envelope = JsonSerializer.Deserialize<EventEnvelope>(message.EnvelopeJson, WalletEvents.Json)!;
            foreach (var sensitive in new[] { "otp", "token", "challenge", "reason", "note", "name", "email" }) Assert.DoesNotContain(sensitive, message.EnvelopeJson, StringComparison.OrdinalIgnoreCase);
            await Rabbit.PublishAsync(envelope, default); await ConsumeEventually();
        }
        var a = await Read<NotificationPage>(await w.Setup.A.Client.GetAsync("/api/v1/notifications"));
        Assert.Equal(WalletEvents.TransferCompleted, Assert.Single(a.Items).Kind);
        Assert.Equal(2, (await Read<NotificationPage>(await w.Setup.B.Client.GetAsync("/api/v1/notifications"))).Items.Count);
        Assert.Equal(2, await NotificationsDb(db => db.Inbox.CountAsync()));
    }
    [Fact]
    public async Task Messaging_health_reports_pending_age_attempts_DLQ_and_requires_staff_permission()
    {
        var s = await SetupAsync(); await Complete(s); _factory.Time.Advance(TimeSpan.FromSeconds(20));
        var before = await Scope(sp => sp.GetRequiredService<MessagingHealth>().GetAsync()); Assert.Equal(1, before.PendingCount); Assert.InRange(before.OldestUnpublishedSeconds, 19.9, 20.1); Assert.True(before.BrokerAvailable);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.A.Client.GetAsync("/api/v1/admin/operations/messaging")).StatusCode);
        await Grant(s.A, "transaction.view.any"); await PublishEvent();
        var after = await Read<MessagingStatus>(await s.A.Client.GetAsync("/api/v1/admin/operations/messaging")); Assert.Equal(0, after.PendingCount); Assert.Equal(0, after.FailedCount); Assert.Equal(1, after.PublishAttempts); Assert.Equal(0u, after.DlqCount);
    }
    [Fact]
    public async Task Hosted_workers_resume_existing_pending_rows_after_application_restart()
    {
        var s = await SetupAsync(); await Complete(s); var settings = _factory.Services.GetRequiredService<MessagingOptions>();
        var config = new Dictionary<string, string?> { ["Messaging:WorkersEnabled"] = "true", ["Messaging:RabbitUri"] = settings.RabbitUri,
            ["Messaging:Prefix"] = settings.Prefix, ["Messaging:LeaseSeconds"] = "4", ["Messaging:PublishTimeoutSeconds"] = "2", ["Messaging:RetryBaseMilliseconds"] = "150", ["Messaging:PollMilliseconds"] = "50" };
        await using (var restarted = new IdentityPostgresFactory(_connection, config))
        {
            using var client = restarted.CreateClient();
            await Eventually(async () => await NotificationsDb(db => db.Notifications.CountAsync()) == 2);
        }
        Assert.NotNull((await OnlyMessage()).PublishedAt); Assert.Equal(1, await NotificationsDb(db => db.Inbox.CountAsync()));
    }
}
