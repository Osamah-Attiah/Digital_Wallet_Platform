using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using DigitalWallet.Caching.Contracts;

namespace DigitalWallet.Caching;

public sealed class CacheOptions
{
    public bool Enabled { get; init; }
    public string ConnectionString { get; init; } = "localhost:6379";
    public string Environment { get; init; } = "development";
    public string KeySecret { get; init; } = "";
    public int OperationTimeoutMilliseconds { get; init; } = 200;
    public int BackoffMilliseconds { get; init; } = 2000;
    public int MaxPayloadBytes { get; init; } = 16384;
    public int PermissionTtlSeconds { get; init; } = 30;
    public int SettingsTtlSeconds { get; init; } = 30;
    public int DashboardTtlSeconds { get; init; } = 5;
    public double JitterRatio { get; init; } = .2;
    public int LocalRatePartitions { get; init; } = 4096;
    public void Validate()
    {
        if (Environment.Length is < 1 or > 40 || Environment.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_') ||
            Enabled && (string.IsNullOrWhiteSpace(ConnectionString) || Encoding.UTF8.GetByteCount(KeySecret) < 32) ||
            OperationTimeoutMilliseconds is < 50 or > 1000 || BackoffMilliseconds is < 100 or > 30000 ||
            MaxPayloadBytes is < 512 or > 65536 || PermissionTtlSeconds is < 1 or > 60 || SettingsTtlSeconds is < 1 or > 60 ||
            DashboardTtlSeconds is < 1 or > 15 || JitterRatio is < 0 or > .3 || double.IsNaN(JitterRatio) || LocalRatePartitions is < 16 or > 16384)
            throw new InvalidOperationException("Invalid safe caching configuration.");
    }
}

public sealed class CacheTelemetry : IDisposable
{
    private readonly Meter meter = new("DigitalWallet.Caching", "1.0");
    private readonly Counter<long> outcomes;
    private readonly Counter<long> rates;
    private readonly Histogram<double> duration;
    private readonly Histogram<long> payload;
    private long hits, misses, failures, bypasses;
    private int available;
    public CacheTelemetry(CacheOptions options)
    {
        outcomes = meter.CreateCounter<long>("cache.operations"); rates = meter.CreateCounter<long>("rate.decisions");
        duration = meter.CreateHistogram<double>("cache.operation.duration", "s"); payload = meter.CreateHistogram<long>("cache.payload.bytes", "By");
        meter.CreateObservableGauge("cache.connection.available", () => options.Enabled ? new[] { new Measurement<int>(Volatile.Read(ref available)) } : Array.Empty<Measurement<int>>());
    }
    public (long Hits, long Misses, long Failures, long Bypasses) Snapshot => (Interlocked.Read(ref hits), Interlocked.Read(ref misses), Interlocked.Read(ref failures), Interlocked.Read(ref bypasses));
    public void Cache(string area, string outcome)
    {
        area = Area(area); outcome = outcome is "hit" or "miss" or "failure" or "bypass" ? outcome : "other";
        outcomes.Add(1, new KeyValuePair<string, object?>("area", area), new("outcome", outcome));
        switch (outcome) { case "hit": Interlocked.Increment(ref hits); break; case "miss": Interlocked.Increment(ref misses); break; case "failure": Interlocked.Increment(ref failures); break; case "bypass": Interlocked.Increment(ref bypasses); break; }
    }
    public void Rate(string policy, string outcome) => rates.Add(1, new KeyValuePair<string, object?>("policy", policy is "login" or "otp" or "otp-issue" or "pii" or "reports" or "administration" ? policy : "other"),
        new("outcome", outcome is "allowed" or "rejected" or "fallback-allowed" or "fallback-rejected" ? outcome : "other"));
    public void Duration(string operation, double seconds) => duration.Record(double.IsFinite(seconds) ? Math.Max(0, seconds) : 0,
        new KeyValuePair<string, object?>("operation", operation is "ping" or "get" or "set" or "lease" or "lease-release" or "rate" ? operation : "other"));
    public void Payload(string area, int size) => payload.Record(Math.Max(0, size), new KeyValuePair<string, object?>("area", Area(area)));
    private static string Area(string value) => value is "permissions" or "settings" or "dashboard" or "transport" ? value : "other";
    public void Availability(bool value) => Volatile.Write(ref available, value ? 1 : 0);
    public void Dispose() => meter.Dispose();
}

public sealed record RedisAttempt<T>(bool Success, T? Value);
public sealed class RedisRuntime : IDisposable
{
    private static readonly ActivitySource Source = new("DigitalWallet.Caching", "1.0.0");
    private readonly CacheOptions options;
    private readonly CacheTelemetry telemetry;
    private readonly Lazy<Task<ConnectionMultiplexer>> connection;
    private long retryAt;
    public RedisRuntime(CacheOptions options, CacheTelemetry telemetry)
    {
        this.options = options; this.telemetry = telemetry;
        connection = new(async () =>
        {
            var config = ConfigurationOptions.Parse(options.ConnectionString);
            config.AbortOnConnectFail = false; config.ConnectTimeout = options.OperationTimeoutMilliseconds;
            config.AsyncTimeout = options.OperationTimeoutMilliseconds; config.SyncTimeout = options.OperationTimeoutMilliseconds;
            config.ConnectRetry = 0; config.BacklogPolicy = BacklogPolicy.FailFast; config.ReconnectRetryPolicy = new ExponentialRetry(500);
            // No client name containing host, actor, resource or session identifiers.
            config.ClientName = "digitalwallet-cache"; config.AllowAdmin = false; config.ConfigurationChannel = "";
            var mux = await ConnectionMultiplexer.ConnectAsync(config);
            mux.ConnectionFailed += (_, _) => telemetry.Availability(false);
            mux.ConnectionRestored += (_, _) => telemetry.Availability(mux.IsConnected);
            return mux;
        });
    }
    public bool Available => options.Enabled && connection.IsValueCreated && connection.Value.IsCompletedSuccessfully && connection.Value.Result.IsConnected;
    public async Task<RedisAttempt<T>> RunAsync<T>(string operation, Func<IDatabase, Task<T>> work, CancellationToken ct)
    {
        if (!options.Enabled || Stopwatch.GetTimestamp() < Volatile.Read(ref retryAt)) return new(false, default);
        var start = Stopwatch.GetTimestamp();
        using var span = Source.StartActivity("redis.operation", ActivityKind.Client);
        span?.SetTag("db.system.name", "redis");
        try
        {
            var mux = await connection.Value.WaitAsync(TimeSpan.FromMilliseconds(options.OperationTimeoutMilliseconds), ct);
            var value = await work(mux.GetDatabase()).WaitAsync(TimeSpan.FromMilliseconds(options.OperationTimeoutMilliseconds), ct);
            telemetry.Availability(true); return new(true, value);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is RedisException or TimeoutException or System.Net.Sockets.SocketException)
        {
            Volatile.Write(ref retryAt, Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * options.BackoffMilliseconds / 1000d));
            span?.SetStatus(ActivityStatusCode.Error); telemetry.Availability(false); telemetry.Cache("transport", "failure"); return new(false, default);
        }
        finally { telemetry.Duration(operation, Stopwatch.GetElapsedTime(start).TotalSeconds); }
    }
    public async Task<bool> ProbeAsync(CancellationToken ct) => (await RunAsync("ping", async db => { await db.PingAsync(); return true; }, ct)).Success;
    public void Dispose()
    {
        if (!connection.IsValueCreated) return;
        _ = connection.Value.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
    }
}

public sealed class CacheKeys(CacheOptions options)
{
    private readonly byte[] secret = Encoding.UTF8.GetBytes(options.KeySecret.Length >= 32 ? options.KeySecret : "disabled-cache-no-persistent-data-key");
    public string Prefix => $"digitalwallet:{options.Environment}:v1:";
    public string Key(string area, string identity) => Prefix + area + ":" + Hash(area + ":" + identity);
    public string Hash(string text) => Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(text)));
    public string Sign(string key, string payload) => Hash(key + "\n" + payload);
    public bool Verify(string key, string payload, string signature)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Sign(key, payload)), Convert.FromHexString(signature)); }
        catch (FormatException) { return false; }
    }
}

public static class CachingModule
{
    public static IServiceCollection AddCachingModule(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(_ => { var options = config.GetSection("Caching").Get<CacheOptions>() ?? new(); options.Validate(); return options; });
        services.AddSingleton<CacheTelemetry>(); services.AddSingleton<CacheKeys>(); services.AddSingleton<RedisRuntime>();
        services.AddSingleton<ISafeCache, SafeCache>(); services.AddSingleton<IDistributedRateCoordinator, DistributedRateCoordinator>();
        return services;
    }
}
