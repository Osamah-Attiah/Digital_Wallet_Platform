using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using DigitalWallet.Messaging;
using DigitalWallet.Messaging.Contracts;
using DigitalWallet.Transfers.Contracts;
using DigitalWallet.SharedKernel.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace DigitalWallet.Api.Tests;
public sealed partial class TransfersPostgresTests
{
    [Fact]
    public async Task Phase12_HTTP_outbox_RabbitMQ_consumer_redelivery_preserves_trace_and_event_identity()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name.StartsWith("DigitalWallet.") || s.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> o) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => spans.Enqueue(a)
        };
        ActivitySource.AddActivityListener(listener);
        var s = await SetupAsync(); var t = await Intent(s);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/transfers/{t.Id}/confirm") { Content = JsonContent.Create(new { code = await Code(t.Id) }) };
        request.Headers.Add("X-Correlation-ID", "phase12_financial");
        request.Headers.Add("traceparent", "00-abc12345678901234567890123456789-1234567890123456-00");
        var response = await s.A.Client.SendAsync(request); await Read<TransferReceipt>(response);
        var row = await OnlyMessage(); var parent = new TraceHeaders(row.TraceParent, row.TraceCorrelationId!, row.TraceCausationId!).Parent;
        Assert.Equal("abc12345678901234567890123456789", parent.TraceId.ToHexString());
        Assert.Equal("phase12_financial", row.TraceCorrelationId);
        Assert.DoesNotContain("trace", row.EnvelopeJson, StringComparison.OrdinalIgnoreCase);
        await PublishEvent();
        _factory.MessagingFaults.Fail("AfterConsumerCommit", crash: true);
        await Assert.ThrowsAsync<MessagingCrashException>(async () => await Eventually(ConsumeEvent));
        await ConsumeEventually();
        Assert.Equal(row.EventId, (await OnlyMessage()).EventId);
        Assert.Equal(2, await NotificationsDb(db => db.Notifications.CountAsync()));
        var consumers = spans.Where(a => a.Source.Name == "DigitalWallet.Messaging" && a.DisplayName == "Consume" && a.TraceId == parent.TraceId).ToArray();
        Assert.True(consumers.Length >= 2); Assert.Equal(consumers.Length, consumers.Select(a => a.SpanId).Distinct().Count());
        Assert.All(consumers, a => Assert.Equal("phase12_financial", a.GetTagItem("correlation.id")));
        Assert.Contains(spans, a => a.Source.Name == "DigitalWallet.Ledger" && a.TraceId == parent.TraceId && a.DisplayName == "Post");
        Assert.Equal(1, await Journals());
    }
    [Fact]
    public async Task Phase12_unreachable_OTLP_collector_cannot_change_a_financial_commit()
    {
        var s = await SetupAsync(); var t = await Intent(s);
        await using var exporting = new IdentityPostgresFactory(_connection, new Dictionary<string, string?>
        {
            ["Observability:ExportEnabled"] = "true", ["Observability:Endpoint"] = "http://127.0.0.1:1",
            ["Observability:ExportTimeoutMilliseconds"] = "100", ["Observability:MetricIntervalMilliseconds"] = "1000",
            ["Messaging:WorkersEnabled"] = "false"
        });
        using var client = exporting.CreateClient(); client.DefaultRequestHeaders.Authorization = s.A.Client.DefaultRequestHeaders.Authorization;
        var response = await client.PostAsJsonAsync($"/api/v1/transfers/{t.Id}/confirm", new { code = await Code(t.Id) });
        var receipt = await Read<TransferReceipt>(response); Assert.Equal("Completed", receipt.Status);
        Assert.Equal(1, await Journals()); Assert.Single(await OutboxDb(db => db.Messages.ToListAsync()));
        var reconciliation = await Scope(sp => sp.GetRequiredService<DigitalWallet.Ledger.Contracts.ILedgerReconciliation>().RunAsync());
        Assert.Empty(reconciliation.Findings); Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }
}
