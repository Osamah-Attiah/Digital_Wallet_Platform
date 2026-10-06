using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using DigitalWallet.Api.Foundation;
using DigitalWallet.Api.Foundation.Configuration;
using DigitalWallet.Api.Foundation.Observability;
using DigitalWallet.SharedKernel.Observability;
using DigitalWallet.Messaging;
using DigitalWallet.Messaging.Contracts;
using DigitalWallet.Ledger.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using Serilog;
using Serilog.Events;
using Serilog.Core;
using Xunit;
namespace DigitalWallet.Api.Tests;

[Collection("Phase12 isolated telemetry")]
public sealed class ObservabilityPhase12Tests
{
    private const string Canary = "canary-secret-private@example.invalid-password-otp-654321";
    [Fact]
    public void Json_logging_drops_bodies_credentials_PII_exceptions_and_unknown_fields()
    {
        var sink = new JsonSink();
        using var log = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        log.ForContext("EventId", 1001).ForContext("Route", "/api/v1/auth/login").ForContext("Password", Canary)
            .ForContext("Authorization", Canary).ForContext("Cookie", Canary).ForContext("RefreshToken", Canary)
            .ForContext("Otp", Canary).ForContext("ConnectionString", Canary).ForContext("Email", Canary)
            .ForContext("UserId", Canary).ForContext("Body", new { secret = Canary }, true)
            .ForContext("Outcome", "Completed").ForContext("ErrorCode", "NONE")
            .Information(new Exception(Canary), "Untrusted message {Canary}", Canary);
        var row = Assert.Single(sink.Rows);
        using var parsed = JsonDocument.Parse(row);
        Assert.Equal("RequestCompleted", parsed.RootElement.GetProperty("EventName").GetString());
        Assert.EndsWith("Z", parsed.RootElement.GetProperty("Timestamp").GetString());
        Assert.DoesNotContain(Canary, row);
        Assert.DoesNotContain("Password", row); Assert.DoesNotContain("Exception", row);
    }
    [Fact]
    public void JSON_log_contains_the_active_W3C_trace_and_safe_correlation()
    {
        using var activity = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();
        var sink = new JsonSink(); using var log = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        log.ForContext("EventId", 1001).ForContext("CorrelationId", "phase12_http").Information("Request completed");
        using var row = JsonDocument.Parse(Assert.Single(sink.Rows));
        Assert.Equal(activity.TraceId.ToHexString(), row.RootElement.GetProperty("TraceId").GetString());
        Assert.Equal(activity.SpanId.ToHexString(), row.RootElement.GetProperty("SpanId").GetString());
        Assert.Equal("phase12_http", row.RootElement.GetProperty("CorrelationId").GetString());
    }
    [Theory]
    [InlineData("\r\nforged entry\u0000")]
    [InlineData("\u2028\u2029\u001b[31m")]
    public void Control_characters_cannot_create_additional_JSON_entries(string controls)
    {
        var sink = new JsonSink(); using var log = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        log.ForContext("CorrelationId", controls).ForContext("Route", "/auth?secret=" + controls).ForContext("ErrorCode", controls).Warning(controls);
        Assert.Single(sink.Rows); Assert.Equal(1, sink.Rows[0].Count(c => c == '\n'));
        using var parsed = JsonDocument.Parse(sink.Rows[0]);
        Assert.Equal("", parsed.RootElement.GetProperty("CorrelationId").GetString());
        Assert.Equal("unmatched", parsed.RootElement.GetProperty("Route").GetString());
    }
    [Fact]
    public void Trace_redaction_strips_sql_url_query_credentials_baggage_and_status_messages()
    {
        using var a = new Activity("sensitive").Start();
        foreach (var key in new[] { "url.full", "url.query", "db.statement", "db.query.text", "server.address", "Authorization", "customer.email", "user.id" }) a.SetTag(key, Canary);
        a.SetBaggage("secret", Canary); a.TraceStateString = "secret=" + Canary;
        a.SetTag("wallet.error.code", Canary); a.SetStatus(ActivityStatusCode.Error, Canary);
        a.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection { { "exception.message", Canary }, { "exception.stacktrace", Canary } }));
        Assert.False(SafeActivityProcessor.Sanitize(a));
        Assert.DoesNotContain(Canary, string.Join(" ", a.TagObjects.Select(x => x.Value)));
        Assert.Empty(a.Baggage); Assert.Null(a.TraceStateString); Assert.Null(a.StatusDescription);
        // Immutable exception events cause the entire dependency span to be rejected at export; owner error spans remain available.
    }
    [Fact]
    public void Trace_context_headers_are_separate_from_the_financial_payload_and_invalid_context_is_ignored()
    {
        using var activity = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();
        var e = WalletEvents.Transfer(Guid.NewGuid(), DateTimeOffset.UtcNow, "correlation_phase12", new(Guid.NewGuid(), Guid.NewGuid(), 100, "SAR"));
        var trace = TraceHeaders.Capture(e); var props = RabbitTransport.Properties(e, trace);
        Assert.Equal(activity.TraceId, TraceHeaders.Read(props.Headers, e).Parent.TraceId);
        Assert.DoesNotContain("traceparent", JsonSerializer.Serialize(e, WalletEvents.Json));
        Assert.Equal(default, new TraceHeaders(Canary, e.CorrelationId, e.CausationId.ToString("D")).Parent);
        Assert.Throws<InvalidEventException>(() => TraceHeaders.Read(new Dictionary<string, object?> { ["x-correlation-id"] = "spoofed" }, e));
        Assert.Throws<InvalidEventException>(() => WalletEvents.Validate(e with { EventId = Guid.NewGuid() }));
    }
    [Fact]
    public void Critical_operations_are_retained_when_normal_trace_sampling_is_zero()
    {
        var sampler = new WalletSampler(0);
        var critical = new OpenTelemetry.Trace.SamplingParameters(default, ActivityTraceId.CreateRandom(), "Confirm", ActivityKind.Internal);
        var inbound = new OpenTelemetry.Trace.SamplingParameters(default, ActivityTraceId.CreateRandom(), "/arbitrary", ActivityKind.Server);
        var routine = new OpenTelemetry.Trace.SamplingParameters(default, ActivityTraceId.CreateRandom(), "redis.operation", ActivityKind.Client);
        Assert.Equal(OpenTelemetry.Trace.SamplingDecision.RecordAndSample, sampler.ShouldSample(critical).Decision);
        Assert.Equal(OpenTelemetry.Trace.SamplingDecision.RecordAndSample, sampler.ShouldSample(inbound).Decision);
        Assert.Equal(OpenTelemetry.Trace.SamplingDecision.Drop, sampler.ShouldSample(routine).Decision);
    }
    [Fact]
    public async Task A_blocked_exporter_cannot_block_request_threads_and_overflow_is_counted()
    {
        using var metrics = new OperationalMetrics(); var blocked = new BlockingExporter();
        using var processor = new BoundedTraceProcessor(new GuardedExporter<Activity>(blocked, metrics), metrics, 16);
        using var listener = new MeterListener(); long dropped = 0;
        listener.InstrumentPublished = (i, l) => { if (i.Name == "wallet.telemetry.dropped") l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<long>((i, n, tags, state) => Interlocked.Add(ref dropped, n)); listener.Start();
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < 1000; i++) { using var a = new Activity("Confirm").Start(); a.ActivityTraceFlags = ActivityTraceFlags.Recorded; processor.OnEnd(a); }
        Assert.True(Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(2));
        Assert.True(dropped > 0); blocked.Release.Set();
        Assert.True(processor.Shutdown(2000));
        await Task.CompletedTask;
    }
    [Fact]
    public void Exporter_failure_and_exception_are_bounded_safe_telemetry()
    {
        using var metrics = new OperationalMetrics(); long failures = 0; long dropped = 0;
        using var listener = new MeterListener(); listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == "DigitalWallet.Api") l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<long>((i, n, tags, state) => { if (i.Name.EndsWith("export.failures")) failures += n; if (i.Name.EndsWith("dropped")) dropped += n; }); listener.Start();
        using var exporter = new GuardedExporter<Activity>(new ThrowingExporter(), metrics); using var a = new Activity("Confirm").Start();
        Assert.Equal(ExportResult.Failure, exporter.Export(new Batch<Activity>(new[] { a }, 1)));
        Assert.Equal(1, failures); Assert.Equal(1, dropped);
    }
    [Fact]
    public void Export_boundary_removes_canaries_and_rejects_unsafe_activity_links()
    {
        using var metrics = new OperationalMetrics(); var captured = new CaptureExporter();
        using var processor = new BoundedTraceProcessor(new GuardedExporter<Activity>(captured, metrics), metrics, 16);
        using var clean = new Activity("Confirm").Start(); clean.ActivityTraceFlags = ActivityTraceFlags.Recorded;
        clean.SetTag("db.query.text", Canary); clean.SetTag("url.full", Canary); clean.SetBaggage("secret", Canary);
        clean.SetStatus(ActivityStatusCode.Error, Canary); clean.Stop(); processor.OnEnd(clean);
        using var linked = new Activity("Confirm").Start(); linked.ActivityTraceFlags = ActivityTraceFlags.Recorded;
        linked.AddLink(new ActivityLink(new(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded, "secret=" + Canary),
            new ActivityTagsCollection { { "secret", Canary } }));
        linked.Stop(); processor.OnEnd(linked);
        Assert.True(processor.Shutdown(2000));
        Assert.DoesNotContain(Canary, Assert.Single(captured.Rows));
    }
    [Fact]
    public void Cache_metric_dimensions_collapse_untrusted_values_to_a_finite_vocabulary()
    {
        using var listener = new MeterListener(); var labels = new List<string>();
        listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == "DigitalWallet.Caching") l.EnableMeasurementEvents(i); };
        void Record(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        { foreach (var tag in tags) { Assert.Contains(tag.Key, new[] { "area", "outcome", "policy", "operation" }); labels.Add(tag.Value?.ToString() ?? ""); } }
        listener.SetMeasurementEventCallback<long>((i, n, tags, state) => Record(tags));
        listener.SetMeasurementEventCallback<double>((i, n, tags, state) => Record(tags)); listener.Start();
        using var cache = new DigitalWallet.Caching.CacheTelemetry(new());
        cache.Cache(Canary, Canary); cache.Rate(Canary, Canary); cache.Duration(Canary, double.NaN); cache.Payload(Canary, 10);
        Assert.NotEmpty(labels); Assert.All(labels, value => Assert.Equal("other", value));
    }
    [Fact]
    public async Task Metrics_contain_only_finite_labels_and_reconciliation_publishes_both_integrity_signals()
    {
        using var metrics = new OperationalMetrics(); using var listener = new MeterListener(); var samples = new List<(string Name, long Value)>();
        var allowed = new[] { "module", "operation", "outcome", "event" };
        listener.InstrumentPublished = (i, l) => { if (i.Meter.Name is "DigitalWallet.Operations" or "DigitalWallet.Api") l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<long>((i, n, tags, state) =>
        { foreach (var t in tags) { Assert.Contains(t.Key, allowed); Assert.DoesNotContain(Canary, t.Value?.ToString() ?? ""); } lock (samples) samples.Add((i.Name, n)); });
        listener.Start();
        await WalletTelemetry.RunAsync(TelemetryModule.Transfers, OperationName.Confirm, () => Task.FromResult(Canary));
        metrics.Reconciled(new(DateTimeOffset.UtcNow, [new("UnbalancedJournal", Guid.NewGuid()), new("BalanceDrift", Guid.NewGuid())]));
        listener.RecordObservableInstruments();
        Assert.Contains(samples, x => x.Name == "wallet.ledger.imbalance" && x.Value == 1);
        Assert.Contains(samples, x => x.Name == "wallet.ledger.projection.drift" && x.Value == 1);
        Assert.Contains(samples, x => x.Name == "wallet.reconciliation.last.success" && x.Value > 0);
    }
    private sealed class JsonSink : ILogEventSink
    { public List<string> Rows { get; } = []; public void Emit(LogEvent ev) { using var w = new StringWriter(); new SafeJsonFormatter("Testing").Format(ev, w); Rows.Add(w.ToString()); } }
    private sealed class BlockingExporter : BaseExporter<Activity>
    { public readonly ManualResetEventSlim Release = new(); public override ExportResult Export(in Batch<Activity> batch) { Release.Wait(TimeSpan.FromSeconds(5)); return ExportResult.Failure; } }
    private sealed class ThrowingExporter : BaseExporter<Activity>
    { public override ExportResult Export(in Batch<Activity> batch) => throw new InvalidOperationException(Canary); }
    private sealed class CaptureExporter : BaseExporter<Activity>
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Rows { get; } = new();
        public override ExportResult Export(in Batch<Activity> batch)
        { foreach (var a in batch) Rows.Enqueue(JsonSerializer.Serialize(new { a.DisplayName, Tags = a.TagObjects.ToArray(), Baggage = a.Baggage.ToArray(), a.TraceStateString, a.StatusDescription, Events = a.Events.ToArray(), Links = a.Links.ToArray() })); return ExportResult.Success; }
    }
}

[Collection("Phase12 isolated telemetry")]
public sealed class HttpPhase12Tests
{
    private static WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseEnvironment("Development"); b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        { ["HttpSecurity:AdminOrigins:0"] = "https://admin.example.invalid", ["Observability:ReconciliationEnabled"] = "false", ["Messaging:WorkersEnabled"] = "false", ["Reporting:WorkersEnabled"] = "false", ["Wallets:ProvisioningWorkerEnabled"] = "false" }));
    });
    [Theory]
    [InlineData("https://admin.example.invalid", true)]
    [InlineData("https://evil.example.invalid", false)]
    [InlineData("https://admin.example.invalid.evil", false)]
    public async Task Cors_matches_exact_origins(string origin, bool allowed)
    {
        await using var factory = Factory(); using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/login"); request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST"); request.Headers.Add("Access-Control-Request-Headers", "content-type,authorization");
        var response = await client.SendAsync(request);
        Assert.Equal(allowed, response.Headers.Contains("Access-Control-Allow-Origin"));
        if (allowed) Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }
    [Fact]
    public async Task Response_trace_correlation_security_headers_and_health_are_safe()
    {
        await using var factory = Factory(); using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health?token=secret-canary");
        request.Headers.Add("X-Correlation-ID", "phase12_http"); request.Headers.Add("traceparent", "00-1234567890abcdef1234567890abcdef-1234567890abcdef-00");
        var response = await client.SendAsync(request); var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("phase12_http", response.Headers.GetValues("X-Correlation-ID").Single());
        Assert.Equal("1234567890abcdef1234567890abcdef", response.Headers.GetValues("X-Trace-ID").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(response.Headers.Contains("Referrer-Policy")); Assert.True(response.Headers.Contains("Permissions-Policy"));
        Assert.DoesNotContain("secret", body); Assert.DoesNotContain("ConnectionString", body);
        var error = await client.GetAsync("/missing?token=secret-canary"); using var json = JsonDocument.Parse(await error.Content.ReadAsStringAsync());
        Assert.Equal(error.Headers.GetValues("X-Trace-ID").Single(), json.RootElement.GetProperty("traceId").GetString());
        Assert.False(json.RootElement.TryGetProperty("detail", out _));
    }
    [Fact]
    public async Task Oversized_body_is_rejected_and_forwarded_headers_from_unknown_peers_are_ignored()
    {
        await using var factory = Factory(); using var client = factory.CreateClient();
        var response = await client.PostAsync("/api/v1/auth/login", new StringContent(new string('x', 65537)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health"); request.Headers.Add("X-Forwarded-Proto", "https"); request.Headers.Add("X-Forwarded-For", "198.51.100.5");
        var healthy = await client.SendAsync(request); Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        Assert.False(healthy.Headers.Contains("Strict-Transport-Security"));
    }
    [Fact]
    public async Task Production_redirects_HTTP_and_adds_HSTS_only_to_HTTPS()
    {
        await using var factory = ProductionFactory();
        using var plain = factory.CreateClient(new() { BaseAddress = new("http://api.example.invalid"), AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Forwarded-Proto", "https");
        var redirect = await plain.SendAsync(request);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, redirect.StatusCode);
        Assert.Equal("https://api.example.invalid/health", redirect.Headers.Location?.ToString());
        Assert.False(redirect.Headers.Contains("Strict-Transport-Security"));
        using var secure = factory.CreateClient(new() { BaseAddress = new("https://api.example.invalid") });
        var response = await secure.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("max-age=15552000", response.Headers.GetValues("Strict-Transport-Security").Single());
    }
    [Theory]
    [InlineData("Identity:Jwt:SigningKey", "development-only-signing-key-abcdefghijklmnopqrstuvwxyz")]
    [InlineData("HttpSecurity:AdminOrigins:0", "*")]
    public void Actual_Production_startup_fails_before_serving_unsafe_configuration(string key, string value)
    {
        using var factory = ProductionFactory(new() { [key] = value });
        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Startup configuration rejected", failure.ToString());
        Assert.DoesNotContain(value, failure.Message);
    }
    private static WebApplicationFactory<Program> ProductionFactory(Dictionary<string, string?>? overrides = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production"); b.ConfigureAppConfiguration((_, c) =>
            {
                var values = ProductionConfigurationPhase12Tests.Safe();
                values["Caching:Enabled"] = "false";
                foreach (var worker in new[] { "Messaging:WorkersEnabled", "Reporting:WorkersEnabled", "Wallets:ProvisioningWorkerEnabled", "Observability:ReconciliationEnabled" }) values[worker] = "false";
                values["HttpSecurity:ExternalWorkersDecision"] = "PHASE12 isolated HTTP startup acceptance";
                if (overrides is not null) foreach (var pair in overrides) values[pair.Key] = pair.Value;
                c.AddInMemoryCollection(values);
            });
        });
}

[Collection("Phase12 isolated telemetry")]
public sealed class ProductionConfigurationPhase12Tests
{
    public static Dictionary<string, string?> Safe() => new()
    {
        ["Identity:Jwt:SigningKey"] = "aB9!kD2#qR7$mT4%wY6&zE3*sU8+hP5=",
        ["Identity:Otp:HashKey"] = "cF1!lG4#rH8$mJ2%nK5&pL7*sM3+tN6=",
        ["Identity:TokenHashKey"] = "dQ2!vW5#xE9$rT3%yU6&iO1*pA4+sS8=",
        ["Platform:PostgreSql:ConnectionString"] = "Host=db.example.invalid;Database=wallet;Username=wallet_api;Password=R7!mQ3@pV8#nZ2$r;SSL Mode=VerifyFull;Timeout=5;Command Timeout=30",
        ["Messaging:RabbitUri"] = "amqps://wallet_broker:R7%21mQ3%40pV8%23nZ2%24r@broker.example.invalid:5671/",
        ["Caching:Enabled"] = "true", ["Caching:Environment"] = "production",
        ["Caching:ConnectionString"] = "cache.example.invalid:6380,ssl=true,user=wallet_cache,password=R7!mQ3@pV8#nZ2$r",
        ["Caching:KeySecret"] = "gR4!bT7#fY1$mU5%hI8&jO2*kP6+lA3=",
        ["AllowedHosts"] = "api.example.invalid", ["HttpSecurity:AdminOrigins:0"] = "https://admin.example.invalid"
    };
    [Theory]
    [InlineData("Identity:Jwt:SigningKey", "development-only-signing-key-abcdefghijklmnopqrstuvwxyz")]
    [InlineData("Identity:Jwt:SigningKey", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("Identity:Otp:HashKey", "development-only-otp-default-key")]
    [InlineData("Identity:TokenHashKey", "default-token-key-abcdefghijklmnopqrstuvwxyz")]
    [InlineData("Caching:KeySecret", "")]
    [InlineData("Caching:ConnectionString", "localhost:6379")]
    [InlineData("Caching:ConnectionString", "cache.example.invalid:6380,ssl=true,user=guest,password=guest")]
    [InlineData("Platform:PostgreSql:ConnectionString", "Host=db;Database=wallet;Username=postgres;Password=postgres")]
    [InlineData("Platform:PostgreSql:ConnectionString", "Host=db;Database=wallet;Username=wallet_api;Password=R7!mQ3@pV8#nZ2$r;SSL Mode=VerifyFull;Include Error Detail=true")]
    [InlineData("Messaging:RabbitUri", "amqp://guest:guest@localhost/")]
    [InlineData("HttpSecurity:AdminOrigins:0", "*")]
    [InlineData("HttpSecurity:AdminOrigins:0", "https://admin.example.invalid/path")]
    [InlineData("AllowedHosts", "*")]
    [InlineData("AllowedHosts", "")]
    [InlineData("DetailedErrors", "true")]
    [InlineData("IdentityModel:ShowPII", "true")]
    [InlineData("DeveloperExceptionPage:Enabled", "true")]
    [InlineData("Messaging:WorkersEnabled", "false")]
    [InlineData("Reporting:WorkersEnabled", "false")]
    [InlineData("Wallets:ProvisioningWorkerEnabled", "false")]
    [InlineData("Observability:ReconciliationEnabled", "false")]
    [InlineData("HttpSecurity:KnownProxies:0", "0.0.0.0")]
    [InlineData("HttpSecurity:MaxRequestBodyBytes", "999999999")]
    public void Production_rejects_unsafe_configuration_without_echoing_values(string key, string value)
    {
        var values = Safe(); values[key] = value;
        var failures = ProductionHardening.Validate(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), true);
        Assert.NotEmpty(failures);
        Assert.DoesNotContain("R7!mQ3", string.Join(" ", failures));
    }
    [Theory]
    [InlineData("http://collector.example.invalid")]
    [InlineData("https://user:secret@collector.example.invalid")]
    [InlineData("https://collector.example.invalid?token=secret")]
    [InlineData("invalid")]
    public void Production_rejects_insecure_export_endpoint(string endpoint)
    {
        var values = Safe(); values["Observability:ExportEnabled"] = "true"; values["Observability:Endpoint"] = endpoint;
        Assert.NotEmpty(ProductionHardening.Validate(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), true));
    }
    [Fact]
    public void Production_accepts_independent_secrets_from_a_provider_and_documented_worker_decisions()
    {
        var values = Safe(); Assert.Empty(ProductionHardening.Validate(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), true));
        values["Messaging:WorkersEnabled"] = "false"; values["HttpSecurity:ExternalWorkersDecision"] = "OPS-1200 external publisher deployment";
        Assert.Empty(ProductionHardening.Validate(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), true));
    }
    [Fact]
    public void Production_rejects_secret_values_in_JSON_even_when_environment_overrides_them()
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"Identity\":{\"Jwt\":{\"SigningKey\":\"file-secret-canary\"}}}"));
        // AddJsonStream is also a JSON provider and must not be treated as a production secret provider.
        var config = new ConfigurationBuilder().AddJsonStream(stream).AddInMemoryCollection(Safe()).Build();
        Assert.Contains(ProductionHardening.Validate(config, true), x => x.Contains("JSON configuration"));
    }
}

[CollectionDefinition("Phase12 isolated telemetry", DisableParallelization = true)]
public sealed class Phase12TelemetryCollection;
