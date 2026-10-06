using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalWallet.Administration;
using DigitalWallet.Caching;
using DigitalWallet.Caching.Contracts;
using DigitalWallet.Identity;
using DigitalWallet.Identity.Authorization;
using DigitalWallet.Identity.Contracts;
using DigitalWallet.Identity.Persistence;
using DigitalWallet.Identity.Services;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace DigitalWallet.Api.Tests;

public sealed class RedisDatabaseFixture : IAsyncLifetime
{
    private readonly IContainer container = new ContainerBuilder("redis:7.4.7-alpine").WithPortBinding(FreePort(), 6379)
        .WithCommand("redis-server", "--save", "", "--appendonly", "no")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    public string ConnectionString => $"{container.Hostname}:{container.GetMappedPublicPort(6379)}";
    public Task InitializeAsync() => container.StartAsync();
    public Task StopAsync() => container.StopAsync();
    public Task StartAsync() => container.StartAsync();
    public async Task DisposeAsync() => await container.DisposeAsync();
}

public sealed class RedisPhase11Tests(RedisDatabaseFixture redis) : IClassFixture<RedisDatabaseFixture>
{
    private sealed class Harness : IDisposable
    {
        public CacheOptions Options { get; }
        public CacheTelemetry Metrics { get; }
        public CacheKeys Keys { get; }
        public RedisRuntime Runtime { get; }
        public SafeCache Cache { get; }
        public DistributedRateCoordinator Rates { get; }
        public Harness(string connection, TimeProvider? time = null, string? environment = null, int payload = 16384, int partitions = 4096)
        {
            Options = new() { Enabled = true, ConnectionString = connection, KeySecret = "integration-hmac-key-shared-between-api-replicas-001", Environment = environment ?? "t" + Guid.NewGuid().ToString("N"),
                BackoffMilliseconds = 100, PermissionTtlSeconds = 1, SettingsTtlSeconds = 1, DashboardTtlSeconds = 1, MaxPayloadBytes = payload, LocalRatePartitions = partitions };
            Options.Validate(); Metrics = new(Options); Keys = new(Options); Runtime = new(Options, Metrics); Cache = new(Runtime, Keys, Options, Metrics, time ?? TimeProvider.System); Rates = new(Runtime, Keys, Options, Metrics, time ?? TimeProvider.System);
        }
        public void Dispose() { Runtime.Dispose(); Metrics.Dispose(); }
    }
    private static Task<PermissionSnapshot> Permission(CancellationToken ct) => Task.FromResult(new PermissionSnapshot(["customer.view"]));
    private static Task<DashboardSnapshot> Dashboard(CancellationToken ct) => Task.FromResult(new DashboardSnapshot(2, 1, 0, 0, 0, 0, null, false));
    private static async Task Ready(Harness h)
    {
        for (var i = 0; i < 200; i++) { if (await h.Runtime.ProbeAsync(default)) return; await Task.Delay(100); }
        Assert.True(h.Runtime.Available);
    }

    [Fact, Trait("Category", "Redis")]
    public async Task Miss_hit_and_versioned_keys_use_only_opaque_identifiers()
    {
        using var h = new Harness(redis.ConnectionString); await Ready(h); var actor = Guid.NewGuid(); var loads = 0;
        Task<PermissionSnapshot> Load(CancellationToken ct) { loads++; return Permission(ct); }
        Assert.False((await h.Cache.PermissionsAsync(actor, 1, Load, default)).FromCache);
        Assert.True((await h.Cache.PermissionsAsync(actor, 1, Load, default)).FromCache);
        Assert.False((await h.Cache.PermissionsAsync(actor, 2, Load, default)).FromCache); Assert.Equal(2, loads);
        Assert.StartsWith($"digitalwallet:{h.Options.Environment}:v1:permissions:", h.Keys.Key("permissions", $"{actor:N}:1"));
        Assert.DoesNotContain(actor.ToString("N"), h.Keys.Key("permissions", $"{actor:N}:1")); Assert.True(h.Metrics.Snapshot.Hits > 0);
    }
    [Fact, Trait("Category", "Redis")]
    public async Task Real_expiry_and_jitter_are_bounded_and_do_not_extend_on_reads()
    {
        using var h = new Harness(redis.ConnectionString); await Ready(h);
        var ttls = Enumerable.Range(0, 100).Select(_ => h.Cache.Ttl(30).TotalSeconds).ToArray();
        Assert.All(ttls, t => Assert.InRange(t, 24, 36)); Assert.True(ttls.Distinct().Count() > 1);
        var actor = Guid.NewGuid(); await h.Cache.PermissionsAsync(actor, 1, Permission, default);
        using var raw = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString); var key = h.Keys.Key("permissions", $"{actor:N}:1");
        var first = await raw.GetDatabase().KeyTimeToLiveAsync(key); Assert.InRange(first!.Value.TotalMilliseconds, 1, 1200);
        await h.Cache.PermissionsAsync(actor, 1, Permission, default); var second = await raw.GetDatabase().KeyTimeToLiveAsync(key); Assert.True(second <= first);
        await Task.Delay(1300); Assert.False((await h.Cache.PermissionsAsync(actor, 1, Permission, default)).FromCache);
    }
    [Theory, Trait("Category", "Redis")]
    [InlineData("not-json")]
    [InlineData("{\"schemaVersion\":99,\"payload\":{\"codes\":[\"customer.pii.view\"]}}")]
    [InlineData("{\"schemaVersion\":1,\"signature\":\"invalid\",\"payload\":{\"codes\":[\"customer.pii.view\"]}}")]
    public async Task Corrupted_unknown_and_unsigned_payloads_are_ignored(string corrupt)
    {
        using var h = new Harness(redis.ConnectionString); await Ready(h); var actor = Guid.NewGuid();
        using var raw = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        await raw.GetDatabase().StringSetAsync(h.Keys.Key("permissions", $"{actor:N}:1"), corrupt, TimeSpan.FromMinutes(1));
        var result = await h.Cache.PermissionsAsync(actor, 1, Permission, default);
        Assert.False(result.FromCache); Assert.Equal(new[] { "customer.view" }, result.Value.Codes); Assert.True(h.Metrics.Snapshot.Failures > 0);
    }
    [Fact, Trait("Category", "Redis")]
    public async Task A_valid_time_frame_with_missing_payload_is_still_ignored_safely()
    {
        using var h = new Harness(redis.ConnectionString); await Ready(h); var actor = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        using var raw = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        await raw.GetDatabase().StringSetAsync(h.Keys.Key("permissions", $"{actor:N}:1"), JsonSerializer.Serialize(new { schemaVersion = 1, asOf = now, expiresAt = now.AddSeconds(1), signature = "00" }), TimeSpan.FromSeconds(1));
        var value = await h.Cache.PermissionsAsync(actor, 1, Permission, default); Assert.False(value.FromCache); Assert.Contains("customer.view", value.Value.Codes);
    }
    [Fact, Trait("Category", "Redis")]
    public async Task Payload_copied_under_new_authorization_version_cannot_grant_old_permissions()
    {
        using var h = new Harness(redis.ConnectionString); await Ready(h); var actor = Guid.NewGuid();
        await h.Cache.PermissionsAsync(actor, 1, Permission, default);
        using var raw = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString); var db = raw.GetDatabase();
        await db.StringSetAsync(h.Keys.Key("permissions", $"{actor:N}:2"), await db.StringGetAsync(h.Keys.Key("permissions", $"{actor:N}:1")), TimeSpan.FromMinutes(1));
        var result = await h.Cache.PermissionsAsync(actor, 2, _ => Task.FromResult(new PermissionSnapshot([])), default);
        Assert.False(result.FromCache); Assert.Empty(result.Value.Codes);
    }
    [Fact, Trait("Category", "Redis")]
    public async Task One_hundred_concurrent_misses_have_one_owner_load_per_replica()
    {
        using var h = new Harness(redis.ConnectionString); await Ready(h); var calls = 0; var actor = Guid.NewGuid();
        async Task<PermissionSnapshot> Load(CancellationToken ct) { Interlocked.Increment(ref calls); await Task.Delay(30, ct); return await Permission(ct); }
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => h.Cache.PermissionsAsync(actor, 1, Load, default)));
        Assert.Equal(1, calls); Assert.All(results, x => Assert.Contains("customer.view", x.Value.Codes));
    }
    [Fact, Trait("Category", "Redis")]
    public async Task Two_replicas_coordinate_cache_misses_with_an_atomic_lease()
    {
        var env = "t" + Guid.NewGuid().ToString("N"); using var one = new Harness(redis.ConnectionString, environment: env); using var two = new Harness(redis.ConnectionString, environment: env);
        await Ready(one); await Ready(two); var calls = 0; var actor = Guid.NewGuid();
        async Task<PermissionSnapshot> Load(CancellationToken ct) { Interlocked.Increment(ref calls); await Task.Delay(40, ct); return await Permission(ct); }
        await Task.WhenAll(one.Cache.PermissionsAsync(actor, 1, Load, default), two.Cache.PermissionsAsync(actor, 1, Load, default)); Assert.Equal(1, calls);
    }
    [Fact, Trait("Category", "Redis")]
    public async Task A_lost_lease_cannot_publish_or_delete_the_new_owners_lease()
    {
        using var h = new Harness(redis.ConnectionString); await Ready(h); var actor = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<PermissionSnapshot> Load(CancellationToken ct) { entered.SetResult(); await finish.Task.WaitAsync(ct); return await Permission(ct); }
        var pending = h.Cache.PermissionsAsync(actor, 1, Load, default); await entered.Task;
        using var raw = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString); var key = h.Keys.Key("permissions", $"{actor:N}:1");
        await raw.GetDatabase().StringSetAsync(key + ":lease", "new-owner", TimeSpan.FromSeconds(5)); finish.SetResult();
        Assert.False((await pending).FromCache); Assert.False(await raw.GetDatabase().KeyExistsAsync(key));
        Assert.Equal("new-owner", (string?)await raw.GetDatabase().StringGetAsync(key + ":lease"));
    }
    [Fact, Trait("Category", "Redis")]
    public async Task Oversized_payloads_and_disallowed_settings_are_never_written()
    {
        using var h = new Harness(redis.ConnectionString, payload: 512); await Ready(h); var actor = Guid.NewGuid();
        var large = new PermissionSnapshot(Enumerable.Repeat(new string('a', 128), 100).ToArray());
        await h.Cache.PermissionsAsync(actor, 1, _ => Task.FromResult(large), default);
        await h.Cache.SettingsAsync("secret-version", _ => Task.FromResult(new[] { new SettingSnapshot("connectionString", "secret", Guid.NewGuid()) }), default);
        using var raw = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        Assert.False(await raw.GetDatabase().KeyExistsAsync(h.Keys.Key("permissions", $"{actor:N}:1")));
        Assert.False(await raw.GetDatabase().KeyExistsAsync(h.Keys.Key("settings", "secret-version")));
    }
    [Fact, Trait("Category", "Redis")]
    public async Task Keys_and_values_do_not_contain_PII_tokens_or_OTP()
    {
        using var h = new Harness(redis.ConnectionString); await Ready(h); var actor = Guid.NewGuid();
        await h.Cache.PermissionsAsync(actor, 1, Permission, default);
        await h.Rates.AcquireAsync(new("login", 5, TimeSpan.FromMinutes(1)), "private@example.com", default);
        using var raw = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var permissionKey = h.Keys.Key("permissions", $"{actor:N}:1"); var payload = (string)(await raw.GetDatabase().StringGetAsync(permissionKey))!;
        var rateKey = h.Keys.Key("rate", "login:private@example.com");
        foreach (var secret in new[] { actor.ToString(), actor.ToString("N"), "private@example.com", "+966500000001", "Password!12345", "refreshToken", "accessToken", "otpCode" })
        { Assert.DoesNotContain(secret, permissionKey + rateKey + payload, StringComparison.OrdinalIgnoreCase); }
    }
    [Fact, Trait("Category", "Redis")]
    public async Task Restricted_ACL_can_cache_and_coordinate_without_admin_or_enumeration_commands()
    {
        var env = "t" + Guid.NewGuid().ToString("N"); var user = "cache_" + Guid.NewGuid().ToString("N");
        using var raw = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        await raw.GetDatabase().ExecuteAsync("ACL", "SETUSER", user, "reset", "on", ">integration-only-redis-password", $"~digitalwallet:{env}:v1:*",
            "+get", "+strlen", "+set", "+psetex", "+del", "+hget", "+hset", "+pexpire", "+time", "+ping", "+echo", "+eval", "+evalsha", "+script|load",
            "+client|setname", "+client|setinfo", "+client|id", "+info");
        try
        {
            using var h = new Harness(redis.ConnectionString + $",user={user},password=integration-only-redis-password", environment: env); await Ready(h);
            var actor = Guid.NewGuid(); await h.Cache.PermissionsAsync(actor, 1, Permission, default); Assert.True((await h.Cache.PermissionsAsync(actor, 1, Permission, default)).FromCache);
            Assert.False((await h.Rates.AcquireAsync(new("login", 5, TimeSpan.FromMinutes(1)), "peer", default)).Fallback);
            var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString + $",user={user},password=integration-only-redis-password");
            using (connection)
            {
                await Assert.ThrowsAsync<RedisServerException>(() => connection.GetDatabase().ExecuteAsync("KEYS", "*"));
                await Assert.ThrowsAsync<RedisServerException>(() => connection.GetDatabase().ExecuteAsync("FLUSHDB"));
                await Assert.ThrowsAsync<RedisServerException>(() => connection.GetDatabase().StringGetAsync("another-platform:key"));
            }
        }
        finally { await raw.GetDatabase().ExecuteAsync("ACL", "DELUSER", user); }
    }
    [Fact, Trait("Category", "Redis")]
    public async Task Outage_before_and_during_read_then_restart_without_keys_preserves_owner_result()
    {
        using var h = new Harness(redis.ConnectionString); await Ready(h); var actor = Guid.NewGuid();
        await h.Cache.PermissionsAsync(actor, 1, Permission, default);
        await redis.StopAsync();
        try
        {
            var before = await h.Cache.PermissionsAsync(actor, 1, Permission, default); Assert.False(before.FromCache); Assert.Contains("customer.view", before.Value.Codes);
            Assert.False(await h.Runtime.ProbeAsync(default));
        }
        finally { await redis.StartAsync(); }
        Assert.Equal(h.Options.ConnectionString, redis.ConnectionString);
        using (var probe = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString)) { await probe.GetDatabase().PingAsync(); }
        await Ready(h); Assert.False((await h.Cache.PermissionsAsync(actor, 1, Permission, default)).FromCache);
        async Task<DashboardSnapshot> StopDuringLoad(CancellationToken ct) { await redis.StopAsync(); return await Dashboard(ct); }
        try { var during = await h.Cache.DashboardAsync(StopDuringLoad, default); Assert.False(during.FromCache); Assert.Equal(2, during.Value.CustomerCount); }
        finally { await redis.StartAsync(); }
        await Ready(h); Assert.False((await h.Cache.DashboardAsync(Dashboard, default)).FromCache);
    }
    [Fact, Trait("Category", "Redis")]
    public async Task Cold_API_start_without_Redis_degrades_then_recovers_the_same_connection()
    {
        using var h = new Harness(redis.ConnectionString); var actor = Guid.NewGuid(); await redis.StopAsync();
        try { Assert.False((await h.Cache.PermissionsAsync(actor, 1, Permission, default)).FromCache); }
        finally { await redis.StartAsync(); }
        await Ready(h); await h.Cache.PermissionsAsync(actor, 1, Permission, default);
        Assert.True((await h.Cache.PermissionsAsync(actor, 1, Permission, default)).FromCache);
    }
    [Fact, Trait("Category", "Redis")]
    public async Task Dashboard_exposes_snapshot_time_then_expiry_forces_owner_read()
    {
        using var h = new Harness(redis.ConnectionString); await Ready(h);
        var cold = await h.Cache.DashboardAsync(Dashboard, default); var warm = await h.Cache.DashboardAsync(_ => throw new InvalidOperationException(), default);
        Assert.True(warm.FromCache); Assert.Equal(cold.AsOf, warm.AsOf);
        await Task.Delay(1300); var fresh = await h.Cache.DashboardAsync(Dashboard, default); Assert.False(fresh.FromCache); Assert.True(fresh.AsOf > cold.AsOf);
    }
    [Fact, Trait("Category", "Redis")]
    public async Task Atomic_rate_decisions_across_two_replicas_allow_exactly_the_budget()
    {
        var env = "t" + Guid.NewGuid().ToString("N"); using var one = new Harness(redis.ConnectionString, environment: env); using var two = new Harness(redis.ConnectionString, environment: env);
        await Ready(one); await Ready(two); var policy = new RatePolicy("reports", 10, TimeSpan.FromHours(1));
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(i => (i % 2 == 0 ? one : two).Rates.AcquireAsync(policy, "same-actor", default)));
        Assert.Equal(10, results.Count(x => x.Allowed)); Assert.All(results, x => { Assert.False(x.Fallback); Assert.InRange(x.RetryAfterSeconds, 1, 3600); });
    }
    [Fact, Trait("Category", "Redis")]
    public async Task Server_clock_boundaries_expire_the_counter_without_client_clock_drift()
    {
        using var h = new Harness(redis.ConnectionString); await Ready(h); var policy = new RatePolicy("login", 2, TimeSpan.FromSeconds(1));
        using var raw = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString); var db = raw.GetDatabase();
        var serverTime = (RedisResult[])(await db.ExecuteAsync("TIME"))!;
        var micro = (long)serverTime[1]; await Task.Delay((int)((1000000 - micro) / 1000) + 30);
        Assert.True((await h.Rates.AcquireAsync(policy, "peer", default)).Allowed); Assert.True((await h.Rates.AcquireAsync(policy, "peer", default)).Allowed);
        Assert.False((await h.Rates.AcquireAsync(policy, "peer", default)).Allowed);
        var ttl = await db.KeyTimeToLiveAsync(h.Keys.Key("rate", "login:peer")); Assert.InRange(ttl!.Value.TotalMilliseconds, 1, 1000);
        await Task.Delay(1100); Assert.True((await h.Rates.AcquireAsync(policy, "peer", default)).Allowed);
    }
    [Fact, Trait("Category", "Redis")]
    public async Task Conservative_fallback_keeps_healthy_attempts_and_caps_partition_memory()
    {
        using var h = new Harness(redis.ConnectionString, partitions: 16); await Ready(h); var policy = new RatePolicy("login", 4, TimeSpan.FromMinutes(1));
        Assert.True((await h.Rates.AcquireAsync(policy, "peer", default)).Allowed); Assert.True((await h.Rates.AcquireAsync(policy, "peer", default)).Allowed);
        await redis.StopAsync();
        try
        {
            var denied = await h.Rates.AcquireAsync(policy, "peer", default); Assert.False(denied.Allowed); Assert.True(denied.Fallback);
            for (var i = 0; i < 15; i++) await h.Rates.AcquireAsync(policy, "new" + i, default);
            Assert.False((await h.Rates.AcquireAsync(policy, "over-capacity", default)).Allowed);
        }
        finally { await redis.StartAsync(); }
    }
    private sealed class RateClock : TimeProvider
    {
        private long tick;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => tick;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch; // Wall-clock changes cannot reset the budget.
        public void Advance(int seconds) => tick += seconds * 1000L;
    }
    [Fact, Trait("Category", "Redis")]
    public async Task Local_fallback_expiry_uses_monotonic_clock_boundaries()
    {
        var time = new RateClock(); using var h = new Harness("127.0.0.1:1", time); var policy = new RatePolicy("login", 4, TimeSpan.FromMinutes(1));
        Assert.True((await h.Rates.AcquireAsync(policy, "peer", default)).Allowed);
        Assert.True((await h.Rates.AcquireAsync(policy, "peer", default)).Allowed);
        time.Advance(59); var blocked = await h.Rates.AcquireAsync(policy, "peer", default); Assert.False(blocked.Allowed); Assert.Equal(1, blocked.RetryAfterSeconds); Assert.True(blocked.Fallback);
        time.Advance(1); Assert.True((await h.Rates.AcquireAsync(policy, "peer", default)).Allowed);
    }
    [Fact, Trait("Category", "Redis")]
    public async Task Oversized_remote_values_are_rejected_before_transfer_to_the_client()
    {
        using var h = new Harness(redis.ConnectionString, payload: 512); await Ready(h); var actor = Guid.NewGuid();
        using var raw = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        await raw.GetDatabase().StringSetAsync(h.Keys.Key("permissions", $"{actor:N}:1"), new string('x', 1000000), TimeSpan.FromMinutes(1));
        var value = await h.Cache.PermissionsAsync(actor, 1, Permission, default); Assert.False(value.FromCache); Assert.Contains("customer.view", value.Value.Codes); Assert.True(h.Metrics.Snapshot.Failures > 0);
    }
    [Fact, Trait("Category", "Redis")]
    public async Task Telemetry_uses_only_bounded_nonpersonal_labels()
    {
        var tags = new List<KeyValuePair<string, object?>>(); using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == "DigitalWallet.Caching") l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<long>((_, _, labels, _) => { lock (tags) tags.AddRange(labels.ToArray()); });
        listener.SetMeasurementEventCallback<double>((_, _, labels, _) => { lock (tags) tags.AddRange(labels.ToArray()); }); listener.Start();
        using var h = new Harness(redis.ConnectionString); await Ready(h); var actor = Guid.NewGuid(); await h.Cache.PermissionsAsync(actor, 1, Permission, default);
        await h.Rates.AcquireAsync(new("login", 5, TimeSpan.FromMinutes(1)), "private@example.com", default); listener.RecordObservableInstruments();
        Assert.NotEmpty(tags); Assert.All(tags, x => { Assert.Contains(x.Key, new[] { "area", "outcome", "operation", "policy" }); Assert.DoesNotContain(actor.ToString(), x.Value?.ToString() ?? ""); Assert.DoesNotContain('@', x.Value?.ToString() ?? ""); });
    }
}

public sealed class RedisSecurityPhase11Tests(IdentityDatabaseFixture database, RedisDatabaseFixture redis, RabbitDatabaseFixture rabbit)
    : IClassFixture<IdentityDatabaseFixture>, IClassFixture<RedisDatabaseFixture>, IClassFixture<RabbitDatabaseFixture>, IAsyncLifetime
{
    private IdentityPostgresFactory factory = null!;
    private Dictionary<string, string?> Config() => new() { ["Caching:Enabled"] = "true", ["Caching:ConnectionString"] = redis.ConnectionString,
        ["Caching:Environment"] = "i" + Guid.NewGuid().ToString("N"), ["Caching:KeySecret"] = "security-integration-key-shared-between-replicas-001", ["Caching:BackoffMilliseconds"] = "100",
        ["Messaging:RabbitUri"] = rabbit.ConnectionString };
    public async Task InitializeAsync() { factory = new(database.ConnectionString, Config()); await factory.ResetAsync(); }
    public async Task DisposeAsync() => await factory.DisposeAsync();
    private async Task<T> Scope<T>(Func<IServiceProvider, Task<T>> work) { await using var s = factory.Services.CreateAsyncScope(); return await work(s.ServiceProvider); }
    private sealed record User(Guid Id, Guid Role, AuthResult Auth, HttpClient Client);
    private async Task<User> Create(params string[] permissions)
    {
        var client = factory.CreateClient(); var email = Guid.NewGuid().ToString("N") + "@test.local"; var device = new DeviceRequest(Guid.NewGuid().ToString("N"), "Test");
        var r = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, "Password!12345", true, "ar-SA", device)); Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var challenge = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challengeId").GetGuid();
        var v = await client.PostAsJsonAsync("/api/v1/auth/register/verify", new VerifyOtpRequest(challenge, factory.Otp.Get(challenge), device)); Assert.Equal(HttpStatusCode.OK, v.StatusCode);
        var auth = (await v.Content.ReadFromJsonAsync<AuthResult>())!; client.DefaultRequestHeaders.Authorization = new("Bearer", auth.AccessToken);
        return await Scope(async sp =>
        {
            var db = sp.GetRequiredService<IdentityDbContext>(); var id = await db.RefreshTokens.Where(x => x.FamilyId == auth.SessionId).Select(x => x.UserId).SingleAsync();
            var role = new ApplicationRole { Id = Guid.NewGuid(), Name = Guid.NewGuid().ToString("N"), NormalizedName = Guid.NewGuid().ToString("N"), DisplayName = "Staff", ConcurrencyStamp = Guid.NewGuid().ToString() };
            db.Roles.Add(role); db.UserRoles.Add(new() { UserId = id, RoleId = role.Id }); foreach (var p in permissions) db.RolePermissions.Add(new() { RoleId = role.Id, PermissionCode = p }); await db.SaveChangesAsync();
            return new User(id, role.Id, auth, client);
        });
    }
    [Theory, Trait("Category", "Redis")]
    [InlineData(false)] [InlineData(true)]
    public async Task Role_commit_increases_version_and_immediately_revokes_permission_and_session(bool outage)
    {
        var target = await Create("customer.view"); var admin = await Create("customer.view", "role.manage", "permission.assign");
        Assert.Contains("customer.view", await Scope(sp => sp.GetRequiredService<IStaffAccess>().PermissionsAsync(target.Id, default)));
        var key = factory.Services.GetRequiredService<CacheKeys>().Key("permissions", $"{target.Id:N}:1");
        if (outage) await redis.StopAsync();
        try
        {
            await Scope(async sp => { var access = sp.GetRequiredService<IStaffAccess>(); var role = (await access.RolesAsync(default)).Single(x => x.Id == target.Role); await access.ChangeRoleAsync(admin.Id, target.Role, new("Staff", [], role.ConcurrencyVersion, "Review"), default); return true; });
            Assert.Equal(2, await Scope(sp => sp.GetRequiredService<IdentityDbContext>().Users.Where(x => x.Id == target.Id).Select(x => x.AuthorizationVersion).SingleAsync()));
            Assert.DoesNotContain("customer.view", await Scope(sp => sp.GetRequiredService<IStaffAccess>().PermissionsAsync(target.Id, default)));
            Assert.Equal(HttpStatusCode.Unauthorized, (await target.Client.GetAsync("/api/v1/admin/session")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await target.Client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(target.Auth.RefreshToken))).StatusCode);
            if (!outage) { using var raw = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString); Assert.True(await raw.GetDatabase().KeyExistsAsync(key)); }
        }
        finally { if (outage) await redis.StartAsync(); }
    }
    [Theory, Trait("Category", "Redis")]
    [InlineData("authorization")] [InlineData("security")]
    public async Task Database_version_change_alone_invalidates_access_and_refresh_despite_cache_hit(string version)
    {
        var user = await Create("customer.view"); await Scope(sp => sp.GetRequiredService<IStaffAccess>().PermissionsAsync(user.Id, default));
        await Scope(async sp => { var db = sp.GetRequiredService<IdentityDbContext>(); var u = await db.Users.SingleAsync(x => x.Id == user.Id); if (version == "authorization") u.AuthorizationVersion++; else u.SecurityStamp = Guid.NewGuid().ToString(); await db.SaveChangesAsync(); return true; });
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Client.GetAsync("/api/v1/admin/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(user.Auth.RefreshToken))).StatusCode);
    }
    [Fact, Trait("Category", "Redis")]
    public async Task Staff_role_assignment_leaves_old_cache_unused_and_revokes_the_session()
    {
        var target = await Create("customer.view"); var admin = await Create("customer.view", "staff.manage", "permission.assign");
        await Scope(sp => sp.GetRequiredService<IStaffAccess>().PermissionsAsync(target.Id, default));
        Assert.Equal(2, await Scope(sp => sp.GetRequiredService<IStaffAccess>().AssignAsync(admin.Id, target.Id, new([], 1, "Review"), default)));
        Assert.DoesNotContain("customer.view", await Scope(sp => sp.GetRequiredService<IStaffAccess>().PermissionsAsync(target.Id, default)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await target.Client.GetAsync("/api/v1/admin/session")).StatusCode);
    }
    [Fact, Trait("Category", "Redis")]
    public async Task Report_and_PII_authorization_always_query_database_even_with_old_permission_payload()
    {
        var user = await Create("report.generate", "customer.pii.view"); await Scope(sp => sp.GetRequiredService<IStaffAccess>().PermissionsAsync(user.Id, default));
        await Scope(async sp => { var db = sp.GetRequiredService<IdentityDbContext>(); await db.RolePermissions.Where(x => x.RoleId == user.Role).ExecuteDeleteAsync(); return true; });
        Assert.False(await Scope(sp => sp.GetRequiredService<IPermissionResolver>().HasAsync(user.Id, "customer.pii.view", default)));
        Assert.False(await Scope(sp => sp.GetRequiredService<IPermissionResolver>().HasAsync(user.Id, "report.generate", default)));
        Assert.False(await Scope(sp => sp.GetRequiredService<IStaffAccess>().HasAsync(user.Id, "report.generate", default)));
    }
    [Fact, Trait("Category", "Redis")]
    public async Task Every_permission_required_by_PII_reveal_is_authoritative()
    {
        var user = await Create("customer.view", "customer.pii.view"); await Scope(sp => sp.GetRequiredService<IStaffAccess>().PermissionsAsync(user.Id, default));
        await Scope(async sp => { await sp.GetRequiredService<IdentityDbContext>().RolePermissions.Where(x => x.RoleId == user.Role && x.PermissionCode == "customer.view").ExecuteDeleteAsync(); return true; });
        Assert.Equal(HttpStatusCode.Forbidden, (await user.Client.PostAsJsonAsync($"/api/v1/admin/customers/{Guid.NewGuid()}/pii", new { reason = "Review" })).StatusCode);
    }
    [Fact, Trait("Category", "Redis")]
    public async Task Settings_versions_publish_only_committed_values_and_rollback_keeps_old_key()
    {
        var old = await Scope(sp => sp.GetRequiredService<AdministrativeReads>().SettingsAsync(default));
        await Scope(async sp => { var db = sp.GetRequiredService<AdministrationDbContext>(); await using var tx = await db.Database.BeginTransactionAsync(); var row = await db.Settings.SingleAsync(x => x.Key == "support.locale"); row.Value = "en-US"; row.ConcurrencyVersion = Guid.NewGuid(); await db.SaveChangesAsync(); var local = await sp.GetRequiredService<AdministrativeReads>().SettingsAsync(default); Assert.Equal("en-US", local.Single(x => x.Key == row.Key).Value); await tx.RollbackAsync(); return true; });
        Assert.Equal(old.Single(x => x.Key == "support.locale"), (await Scope(sp => sp.GetRequiredService<AdministrativeReads>().SettingsAsync(default))).Single(x => x.Key == "support.locale"));
        await Scope(async sp => { var db = sp.GetRequiredService<AdministrationDbContext>(); var row = await db.Settings.SingleAsync(x => x.Key == "support.locale"); row.Value = "en-US"; row.ConcurrencyVersion = Guid.NewGuid(); await db.SaveChangesAsync(); return true; });
        Assert.Equal("en-US", (await Scope(sp => sp.GetRequiredService<AdministrativeReads>().SettingsAsync(default))).Single(x => x.Key == "support.locale").Value);
    }
    [Fact, Trait("Category", "Redis")]
    public async Task Permission_resolution_inside_owner_transaction_bypasses_redis()
    {
        var user = await Create("customer.view"); var metrics = factory.Services.GetRequiredService<CacheTelemetry>(); var before = metrics.Snapshot;
        await Scope(async sp => { var db = sp.GetRequiredService<IdentityDbContext>(); await using var tx = await db.Database.BeginTransactionAsync(); Assert.Contains("customer.view", await sp.GetRequiredService<IStaffAccess>().PermissionsAsync(user.Id, default)); await tx.RollbackAsync(); return true; });
        Assert.Equal(before, metrics.Snapshot);
    }
    [Fact, Trait("Category", "Redis")]
    public async Task Dashboard_HTTP_marks_cached_data_and_retains_asOf()
    {
        var user = await Create("operations.view", "customer.view", "transaction.view.any");
        var cold = (await user.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/dashboard")); var warm = await user.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/dashboard");
        Assert.Equal(cold.GetProperty("asOf").GetString(), warm.GetProperty("asOf").GetString()); Assert.False(cold.GetProperty("isStale").GetBoolean()); Assert.True(warm.GetProperty("isStale").GetBoolean()); Assert.True(warm.GetProperty("informationalOnly").GetBoolean());
    }
    [Fact, Trait("Category", "Redis")]
    public async Task Two_API_replicas_share_login_budget_and_return_RetryAfter()
    {
        var config = Config(); await using var first = new IdentityPostgresFactory(database.ConnectionString, config); await using var second = new IdentityPostgresFactory(database.ConnectionString, config);
        var one = first.CreateClient(); var two = second.CreateClient(); var request = new LoginRequest("missing@example.com", "Password!12345", new("Device", "Test"));
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await (i % 2 == 0 ? one : two).PostAsJsonAsync("/api/v1/auth/login", request)).StatusCode);
        var response = await two.PostAsJsonAsync("/api/v1/auth/login", request); Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode); Assert.NotNull(response.Headers.RetryAfter);
    }
    [Fact, Trait("Category", "Redis")]
    public async Task Redis_outage_is_degraded_and_postgres_outage_is_not_ready()
    {
        var client = factory.CreateClient(); Assert.Equal("healthy", (await client.GetFromJsonAsync<JsonElement>("/health/ready")).GetProperty("status").GetString());
        await redis.StopAsync();
        try { var response = await client.GetAsync("/health/ready"); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("degraded", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString()); }
        finally { await redis.StartAsync(); }
        var config = Config(); config["Platform:PostgreSql:ConnectionString"] = "Host=127.0.0.1;Port=1;Database=missing;Username=missing;Timeout=1";
        await using var failed = new IdentityPostgresFactory(database.ConnectionString, config); var unavailable = await failed.CreateClient().GetAsync("/health/ready"); Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
    }
}
