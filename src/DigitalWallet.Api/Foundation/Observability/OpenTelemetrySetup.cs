using System.Diagnostics;
using System.Threading.Channels;
using OpenTelemetry;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Exporter;
using DigitalWallet.SharedKernel.Observability;
using Serilog;
using Serilog.Sinks.Async;

namespace DigitalWallet.Api.Foundation.Observability;

// Preserve critical operations even when the incoming sampled bit is off. Security evidence also has unsampled logs/counters.
public sealed class WalletSampler(double ratio) : Sampler
{
    private readonly TraceIdRatioBasedSampler regular = new(ratio);
    public override SamplingResult ShouldSample(in SamplingParameters p) =>
        p.Kind == ActivityKind.Server || Enum.TryParse<OperationName>(p.Name, out _) ? new(SamplingDecision.RecordAndSample) : regular.ShouldSample(p);
}
public sealed class SafeActivityProcessor : BaseProcessor<Activity>
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    { "http.route", "http.request.method", "http.response.status_code", "http.method", "http.status_code", "db.system", "db.system.name", "db.operation.name",
        "wallet.module", "wallet.operation", "wallet.outcome", "wallet.error.code", "correlation.id", "causation.id", "messaging.system", "messaging.operation.name", "messaging.redelivered" };
    public static bool Sanitize(Activity a)
    {
        foreach (var tag in a.TagObjects.ToArray())
        {
            if (!Allowed.Contains(tag.Key)) a.SetTag(tag.Key, null);
            else if (tag.Key is "http.request.method" or "http.method") a.SetTag(tag.Key, tag.Value?.ToString() is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS" ? tag.Value : "OTHER");
            else if (tag.Value is string value)
                a.SetTag(tag.Key, tag.Key == "http.route" ? SafeTelemetry.Route(value) : tag.Key == "wallet.error.code" ? SafeTelemetry.Error(value) : SafeTelemetry.Identifier(value));
        }
        a.TraceStateString = null;
        foreach (var baggage in a.Baggage.ToArray()) a.SetBaggage(baggage.Key, null);
        if (a.Status == ActivityStatusCode.Error) a.SetStatus(ActivityStatusCode.Error);
        // Activity event dictionaries may originate in dependencies. Reject unmodifiable event data before export.
        foreach (var ev in a.Events)
        {
            if (ev.Name is not ("exception" or "db.client.read_first_response")) return false;
            if (ev.Tags is ActivityTagsCollection tags) tags.Clear();
            else if (ev.Tags.Any()) return false;
        }
        if (a.Links.Any(link => link.Tags?.Any() == true || !string.IsNullOrEmpty(link.Context.TraceState))) return false;
        if (a.Source.Name == "Npgsql") a.DisplayName = "postgresql.operation";
        else if (a.Kind == ActivityKind.Server) a.DisplayName = SafeTelemetry.Route(a.GetTagItem("http.route") as string);
        else if (a.Source.Name.StartsWith("System.Net.Http", StringComparison.Ordinal)) a.DisplayName = "http.client";
        return true;
    }
    public override void OnEnd(Activity activity) => Sanitize(activity);
}

public sealed class GuardedExporter<T>(BaseExporter<T> inner, OperationalMetrics metrics) : BaseExporter<T> where T : class
{
    public override ExportResult Export(in Batch<T> batch)
    {
        try
        {
            var result = inner.Export(batch);
            if (result == ExportResult.Failure) { metrics.ExportFailures.Add(1); metrics.Dropped.Add(batch.Count); }
            return result;
        }
        catch { metrics.ExportFailures.Add(1); metrics.Dropped.Add(batch.Count); return ExportResult.Failure; }
    }
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}

// TryWrite never blocks a request/transaction. This pump is the only trace path that calls a network exporter.
public sealed class BoundedTraceProcessor : BaseProcessor<Activity>
{
    private readonly Channel<Activity> queue;
    private readonly BaseExporter<Activity> exporter;
    private readonly OperationalMetrics metrics;
    private readonly Task pump;
    public BoundedTraceProcessor(BaseExporter<Activity> exporter, OperationalMetrics metrics, int capacity)
    {
        this.exporter = exporter; this.metrics = metrics;
        queue = Channel.CreateBounded<Activity>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        pump = Task.Run(async () =>
        {
            await foreach (var a in queue.Reader.ReadAllAsync())
            {
                var items = new List<Activity>(128) { a };
                while (items.Count < 128 && queue.Reader.TryRead(out var more)) items.Add(more);
                using var suppression = SuppressInstrumentationScope.Begin();
                exporter.Export(new Batch<Activity>(items.ToArray(), items.Count));
            }
        });
    }
    public override void OnEnd(Activity a)
    {
        if (!a.Recorded) return;
        if (!SafeActivityProcessor.Sanitize(a) || !queue.Writer.TryWrite(a)) metrics.Dropped.Add(1);
    }
    protected override bool OnShutdown(int timeoutMilliseconds)
    { queue.Writer.TryComplete(); return pump.Wait(Math.Clamp(timeoutMilliseconds, 0, 2000)); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { queue.Writer.TryComplete(); _ = pump.ContinueWith(_ => exporter.Dispose(), TaskScheduler.Default); }
        base.Dispose(disposing);
    }
}
public sealed class LogQueueMonitor(OperationalMetrics metrics) : IAsyncLogEventSinkMonitor
{
    private Timer? timer;
    private long prior;
    public void StartMonitoring(IAsyncLogEventSinkInspector inspector) => timer = new Timer(_ =>
    { var n = inspector.DroppedMessagesCount; var delta = n - Interlocked.Exchange(ref prior, n); if (delta > 0) metrics.Dropped.Add(delta); }, null, 1000, 1000);
    public void StopMonitoring(IAsyncLogEventSinkInspector inspector) => timer?.Dispose();
}
public static class OpenTelemetrySetup
{
    public static void AddWalletObservability(this WebApplicationBuilder b)
    {
        Activity.DefaultIdFormat = ActivityIdFormat.W3C; Activity.ForceDefaultIdFormat = true;
        b.Services.AddSingleton(_ => { var o = b.Configuration.GetSection("Observability").Get<ObservabilityOptions>() ?? new(); o.Validate(b.Environment.IsProduction()); return o; }); b.Services.AddSingleton<OperationalMetrics>();
        b.Services.AddHostedService<SecurityLogBridge>();
        if (DigitalWallet.Api.Hosting.WalletComposition.RunsWorkers(b.Configuration)) b.Services.AddHostedService<ReconciliationWorker>();
        b.Logging.ClearProviders();
        b.Services.AddSerilog((services, log) => log.MinimumLevel.Information().MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Npgsql", Serilog.Events.LogEventLevel.Warning).Enrich.FromLogContext()
            .WriteTo.Async(w => w.Console(new SafeJsonFormatter(b.Environment.EnvironmentName)), bufferSize: 2048,
                blockWhenFull: false, monitor: new LogQueueMonitor(services.GetRequiredService<OperationalMetrics>())));
        b.Services.AddOpenTelemetry().ConfigureResource(r => r.Clear().AddService(b.Configuration["Hosting:ServiceName"] ?? "digitalwallet.api", serviceVersion: b.Configuration["Release:Version"] ?? "13.0.0")
            .AddAttributes(new[] { new KeyValuePair<string, object>("deployment.environment.name", b.Environment.EnvironmentName) }))
            .WithTracing(t => t.AddSource("DigitalWallet.*", "Npgsql")
                .AddAspNetCoreInstrumentation(a => a.RecordException = false)
                .AddHttpClientInstrumentation(h => h.RecordException = false)
                .AddProcessor(new SafeActivityProcessor()))
            .WithMetrics(m =>
            {
                m.AddMeter("DigitalWallet.*");
                m.AddView("wallet.api.duration", new ExplicitBucketHistogramConfiguration { Boundaries = [ .005, .01, .025, .05, .1, .25, .5, 1, 2, 5 ], TagKeys = ["route", "status_family"] });
                m.AddView(i => new MetricStreamConfiguration { TagKeys = ["route", "status_family", "error_code", "module", "operation", "outcome", "event", "area", "policy", "consumer", "state"] });
            });
        b.Services.ConfigureOpenTelemetryTracerProvider((provider, configured) =>
        {
            var o = provider.GetRequiredService<ObservabilityOptions>();
            configured.SetSampler(new WalletSampler(o.SampleRatio));
            if (o.ExportEnabled)
                configured.AddProcessor(new BoundedTraceProcessor(new GuardedExporter<Activity>(new OtlpTraceExporter(Exporter(o, "traces")),
                    provider.GetRequiredService<OperationalMetrics>()), provider.GetRequiredService<OperationalMetrics>(), o.QueueCapacity));
        });
        b.Services.ConfigureOpenTelemetryMeterProvider((provider, configured) =>
        {
            var o = provider.GetRequiredService<ObservabilityOptions>();
            if (o.ExportEnabled) configured.AddReader(new PeriodicExportingMetricReader(new GuardedExporter<Metric>(new OtlpMetricExporter(Exporter(o, "metrics")),
                provider.GetRequiredService<OperationalMetrics>()), o.MetricIntervalMilliseconds, o.ExportTimeoutMilliseconds));
        });
        b.Services.AddHostedService<PostgresMetricsBridge>();
    }
    private static OtlpExporterOptions Exporter(ObservabilityOptions o, string signal) => new()
    { Endpoint = new Uri(o.Endpoint.TrimEnd('/') + "/v1/" + signal), Protocol = OtlpExportProtocol.HttpProtobuf,
      TimeoutMilliseconds = o.ExportTimeoutMilliseconds, Headers = "" };
}
