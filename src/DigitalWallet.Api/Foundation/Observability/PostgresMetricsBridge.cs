using System.Diagnostics.Metrics;
namespace DigitalWallet.Api.Foundation.Observability;
// Reads Npgsql's native measurements but re-emits only aggregate counts with finite state labels.
public sealed class PostgresMetricsBridge : IHostedService, IDisposable
{
    private readonly Meter meter = new("DigitalWallet.PostgreSql", "1.0.0");
    private readonly MeterListener listener = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Instrument, string), long> maxima = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Instrument, string, string), long> counts = new();
    private readonly Histogram<double> duration;
    private Timer? poll;
    private long poolMax, poolUsed, poolIdle;
    public PostgresMetricsBridge()
    {
        duration = meter.CreateHistogram<double>("wallet.postgres.duration", "s");
        meter.CreateObservableGauge("wallet.postgres.pool.max", () => Interlocked.Read(ref poolMax), "{connection}");
        meter.CreateObservableGauge("wallet.postgres.pool.used", () => Interlocked.Read(ref poolUsed), "{connection}");
        meter.CreateObservableGauge("wallet.postgres.pool.idle", () => Interlocked.Read(ref poolIdle), "{connection}");
        listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == "Npgsql") l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<int>((i, n, tags, state) => Record(i, n, tags));
        listener.SetMeasurementEventCallback<long>((i, n, tags, state) => Record(i, n, tags));
        listener.SetMeasurementEventCallback<double>((i, n, tags, state) => { if (i.Name == "db.client.operation.duration") duration.Record(n); });
    }
    private void Record(Instrument i, long n, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        string pool = "writer";
        foreach (var tag in tags) if (tag.Key == "db.client.connection.pool.name" && tag.Value is string name)
            pool = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)));
        if (maxima.Count > 256 && !maxima.ContainsKey((i, pool))) return;
        if (i.Name == "db.client.connection.max") maxima[(i, pool)] = n;
        if (i.Name == "db.client.connection.count") foreach (var tag in tags)
            if (tag.Key == "state" && tag.Value?.ToString() is "used" or "idle") counts[(i, pool, tag.Value.ToString()!)] = n;
    }
    public Task StartAsync(CancellationToken ct) { listener.Start(); poll = new Timer(_ => { try { maxima.Clear(); counts.Clear(); listener.RecordObservableInstruments();
            Interlocked.Exchange(ref poolMax, maxima.Values.Sum());
            Interlocked.Exchange(ref poolUsed, counts.Where(x => x.Key.Item3 == "used").Sum(x => x.Value));
            Interlocked.Exchange(ref poolIdle, counts.Where(x => x.Key.Item3 == "idle").Sum(x => x.Value)); } catch { } }, null, 0, 10000); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) { poll?.Dispose(); listener.Dispose(); return Task.CompletedTask; }
    public void Dispose() { poll?.Dispose(); listener.Dispose(); meter.Dispose(); }
}
