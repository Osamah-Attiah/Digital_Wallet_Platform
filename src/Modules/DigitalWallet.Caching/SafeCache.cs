using System.Text.Json;
using DigitalWallet.Caching.Contracts;
using StackExchange.Redis;

namespace DigitalWallet.Caching;

public sealed class SafeCache(RedisRuntime redis, CacheKeys keys, CacheOptions options, CacheTelemetry telemetry, TimeProvider clock) : ISafeCache
{
    public bool Enabled => options.Enabled;
    private readonly SemaphoreSlim[] stripes = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1)).ToArray();
    private readonly SemaphoreSlim loaders = new(8);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Envelope(int SchemaVersion, DateTimeOffset AsOf, DateTimeOffset ExpiresAt, JsonElement Payload, string Signature);
    public TimeSpan Ttl(int seconds) => TimeSpan.FromSeconds(seconds * (1 - options.JitterRatio + Random.Shared.NextDouble() * 2 * options.JitterRatio));
    public Task<CacheRead<PermissionSnapshot>> PermissionsAsync(Guid user, int authorizationVersion, Func<CancellationToken, Task<PermissionSnapshot>> load, CancellationToken ct) =>
        ReadAsync("permissions", $"{user:N}:{authorizationVersion}", options.PermissionTtlSeconds, load, p => p.Codes is { Length: <= 100 } && p.Codes.All(c => c.Length is > 0 and <= 128 && c.All(ch => char.IsAsciiLetterLower(ch) || ch == '.')), ct);
    public Task<CacheRead<SettingSnapshot[]>> SettingsAsync(string committedVersion, Func<CancellationToken, Task<SettingSnapshot[]>> load, CancellationToken ct) =>
        ReadAsync("settings", committedVersion, options.SettingsTtlSeconds, load, p => p.Length <= 2 && p.Select(x => x.Key).Distinct().Count() == p.Length && p.All(x =>
            x.Key == "support.locale" && x.Value is "ar-SA" or "en-US" || x.Key == "dashboard.refreshSeconds" && int.TryParse(x.Value, out var n) && n is >= 15 and <= 300), ct);
    public Task<CacheRead<DashboardSnapshot>> DashboardAsync(Func<CancellationToken, Task<DashboardSnapshot>> load, CancellationToken ct) =>
        ReadAsync("dashboard", "aggregates", options.DashboardTtlSeconds, load, p => p.CustomerCount >= 0 && p.PendingReversals >= 0 && p.PendingMessages >= 0 && p.FailedMessages >= 0 && p.PublishAttempts >= 0 && double.IsFinite(p.OldestUnpublishedSeconds) && p.OldestUnpublishedSeconds >= 0, ct);

    private async Task<CacheRead<T>> ReadAsync<T>(string area, string identity, int seconds, Func<CancellationToken, Task<T>> load, Func<T, bool> valid, CancellationToken ct)
    {
        var key = keys.Key(area, identity);
        if (!options.Enabled) { telemetry.Cache(area, "bypass"); return new(await load(ct), clock.GetUtcNow(), false); }
        var hit = await Get<T>(area, key, seconds, valid, ct); if (hit != null) return hit;
        var gate = stripes[(uint)StringComparer.Ordinal.GetHashCode(key) % (uint)stripes.Length];
        await gate.WaitAsync(ct);
        try
        {
            hit = await Get<T>(area, key, seconds, valid, ct); if (hit != null) return hit;
            var leaseKey = key + ":lease"; var leaseToken = Guid.NewGuid().ToString("N");
            var lease = await redis.RunAsync("lease", db => db.StringSetAsync(leaseKey, leaseToken, TimeSpan.FromSeconds(5), When.NotExists), ct);
            if (lease.Success && !lease.Value)
            {
                for (var i = 0; i < 10; i++)
                {
                    await Task.Delay(25, ct); hit = await Get<T>(area, key, seconds, valid, ct); if (hit != null) return hit;
                }
                // A failed or slow peer cannot strand requests. Per-replica concurrency remains bounded.
                telemetry.Cache(area, "bypass");
            }
            try
            {
                await loaders.WaitAsync(ct);
                T value;
                try { value = await load(ct); } finally { loaders.Release(); }
                var asOf = clock.GetUtcNow(); var expires = asOf + Ttl(seconds);
                if (valid(value) && lease.Success && lease.Value)
                {
                    var payload = JsonSerializer.SerializeToElement(value, Json);
                    var unsigned = Unsigned(1, asOf, expires, payload);
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, asOf, expires, payload, keys.Sign(key, unsigned)), Json);
                    telemetry.Payload(area, bytes.Length);
                    if (bytes.Length <= options.MaxPayloadBytes)
                        await redis.RunAsync("set", db => db.ScriptEvaluateAsync("if redis.call('GET',KEYS[2]) == ARGV[2] then redis.call('PSETEX',KEYS[1],ARGV[3],ARGV[1]); return 1 else return 0 end",
                            [key, leaseKey], [bytes, leaseToken, (long)(expires - asOf).TotalMilliseconds]), ct);
                    else telemetry.Cache(area, "bypass");
                }
                return new(value, asOf, false);
            }
            finally
            {
                if (lease.Success && lease.Value)
                    await redis.RunAsync("lease-release", db => db.ScriptEvaluateAsync("if redis.call('GET',KEYS[1]) == ARGV[1] then return redis.call('DEL',KEYS[1]) else return 0 end", [leaseKey], [leaseToken]), CancellationToken.None);
            }
        }
        finally { gate.Release(); }
    }
    private async Task<CacheRead<T>?> Get<T>(string area, string key, int seconds, Func<T, bool> valid, CancellationToken ct)
    {
        var read = await redis.RunAsync("get", db => db.ScriptEvaluateAsync("if redis.call('STRLEN',KEYS[1]) > tonumber(ARGV[1]) then return {2,''} end; local p = redis.call('GET',KEYS[1]); if not p then return {0,''} else return {1,p} end", [key], [options.MaxPayloadBytes]), ct);
        if (!read.Success) { telemetry.Cache(area, "bypass"); return null; }
        var response = (RedisResult[])read.Value!;
        if ((long)response[0] == 2) { telemetry.Cache(area, "failure"); return null; }
        if ((long)response[0] == 0) { telemetry.Cache(area, "miss"); return null; }
        var raw = (byte[])response[1]!;
        if (raw.Length > options.MaxPayloadBytes) { telemetry.Cache(area, "failure"); return null; }
        try
        {
            var e = JsonSerializer.Deserialize<Envelope>(raw, Json); var now = clock.GetUtcNow();
            if (e == null || e.SchemaVersion != 1 || e.AsOf > now || e.ExpiresAt <= now || e.ExpiresAt <= e.AsOf ||
                e.ExpiresAt - e.AsOf > TimeSpan.FromSeconds(seconds * (1 + options.JitterRatio) + .01) || e.Signature == null ||
                !keys.Verify(key, Unsigned(e.SchemaVersion, e.AsOf, e.ExpiresAt, e.Payload), e.Signature)) throw new JsonException();
            var value = e.Payload.Deserialize<T>(Json);
            if (value == null || !valid(value)) throw new JsonException();
            telemetry.Cache(area, "hit"); return new(value, e.AsOf, true);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException or NullReferenceException or InvalidOperationException)
        { telemetry.Cache(area, "failure"); return null; }
    }
    private static string Unsigned(int schema, DateTimeOffset at, DateTimeOffset expiry, JsonElement payload) =>
        JsonSerializer.Serialize(new { schema, at, expiry, payload }, Json);
}
