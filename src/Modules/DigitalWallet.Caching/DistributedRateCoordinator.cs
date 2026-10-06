using DigitalWallet.Caching.Contracts;
using StackExchange.Redis;

namespace DigitalWallet.Caching;

public sealed class DistributedRateCoordinator(RedisRuntime redis, CacheKeys keys, CacheOptions options, CacheTelemetry telemetry, TimeProvider clock) : IDistributedRateCoordinator
{
    // Redis TIME owns distributed boundaries; all replicas share the same clock. A rejected attempt never extends expiry.
    public const string Script = """
        local t = redis.call('TIME')
        local now = tonumber(t[1])*1000 + math.floor(tonumber(t[2])/1000)
        local window = tonumber(ARGV[2])
        local bucket = math.floor(now/window)
        local previous = tonumber(redis.call('HGET',KEYS[1],'bucket'))
        local count = tonumber(redis.call('HGET',KEYS[1],'count')) or 0
        if previous ~= bucket then count = 0 end
        local remaining = window - (now % window)
        if count >= tonumber(ARGV[1]) then return {0, remaining} end
        redis.call('HSET',KEYS[1],'bucket',bucket,'count',count+1)
        redis.call('PEXPIRE',KEYS[1],remaining)
        return {1, remaining}
        """;
    private sealed class LocalWindow { public long Start; public int Used; }
    private readonly object sync = new();
    private readonly Dictionary<string, LocalWindow> local = new();
    public async Task<RateDecision> AcquireAsync(RatePolicy policy, string subject, CancellationToken ct)
    {
        if (policy.Name.Length is < 1 or > 50 || policy.Name.Any(c => !char.IsAsciiLetterLower(c) && c != '-') || policy.PermitLimit < 1 || policy.Window < TimeSpan.FromSeconds(1) || policy.Window > TimeSpan.FromHours(1))
            throw new ArgumentException("Invalid rate policy.");
        var key = keys.Key("rate", policy.Name + ":" + subject);
        // Always charge a conservative local budget, including while Redis is healthy. Outage cannot reset it.
        var localDecision = Local(key, policy);
        var result = await redis.RunAsync("rate", db => db.ScriptEvaluateAsync(Script, [key], [policy.PermitLimit, (long)policy.Window.TotalMilliseconds]), ct);
        RateDecision decision;
        if (result.Success)
        {
            var values = (RedisResult[])result.Value!;
            decision = new((long)values[0] == 1, Math.Max(1, (int)Math.Ceiling((long)values[1] / 1000d)), false);
        }
        else decision = localDecision with { Fallback = true };
        telemetry.Rate(policy.Name, decision.Fallback ? decision.Allowed ? "fallback-allowed" : "fallback-rejected" : decision.Allowed ? "allowed" : "rejected");
        return decision;
    }
    private RateDecision Local(string key, RatePolicy policy)
    {
        lock (sync)
        {
            var now = clock.GetTimestamp(); var windowTicks = (long)(policy.Window.TotalSeconds * clock.TimestampFrequency);
            if (!local.TryGetValue(key, out var state))
            {
                if (local.Count >= options.LocalRatePartitions)
                {
                    foreach (var old in local.Where(p => now - p.Value.Start >= TimeSpan.FromHours(1).TotalSeconds * clock.TimestampFrequency).Select(p => p.Key).ToArray()) local.Remove(old);
                    if (local.Count >= options.LocalRatePartitions) return new(false, (int)Math.Ceiling(policy.Window.TotalSeconds), true);
                }
                local[key] = state = new() { Start = now };
            }
            if (now - state.Start >= windowTicks) { state.Start = now; state.Used = 0; }
            var retry = Math.Max(1, (int)Math.Ceiling((windowTicks - (now - state.Start)) / (double)clock.TimestampFrequency));
            var limit = Math.Max(1, policy.PermitLimit / 2);
            if (state.Used >= limit) return new(false, retry, true);
            state.Used++; return new(true, retry, true);
        }
    }
}
