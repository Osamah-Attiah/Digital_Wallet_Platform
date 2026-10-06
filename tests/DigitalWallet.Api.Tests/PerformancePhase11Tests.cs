using System.Data.Common;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalWallet.Identity;
using DigitalWallet.Identity.Persistence;
using DigitalWallet.Identity.Services;
using DigitalWallet.Customers.Persistence;
using DigitalWallet.Customers.Domain;
using DigitalWallet.Caching;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace DigitalWallet.Api.Tests;

public sealed class QueryCounter : DbCommandInterceptor
{
    public long Count;
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
    { Interlocked.Increment(ref Count); return ValueTask.FromResult(result); }
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<object> result, CancellationToken ct = default)
    { Interlocked.Increment(ref Count); return ValueTask.FromResult(result); }
}

public sealed class PoolCounter : DbConnectionInterceptor
{
    private int active, peak;
    public int Peak => Volatile.Read(ref peak);
    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData data, CancellationToken ct = default)
    {
        var value = Interlocked.Increment(ref active); int previous;
        do { previous = Volatile.Read(ref peak); if (previous >= value) break; } while (Interlocked.CompareExchange(ref peak, value, previous) != previous);
        return Task.CompletedTask;
    }
    public override Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData data)
    { Interlocked.Decrement(ref active); return Task.CompletedTask; }
}

public sealed class PerformancePhase11Tests(IdentityDatabaseFixture database, RabbitDatabaseFixture rabbit, RedisDatabaseFixture redis)
    : IClassFixture<IdentityDatabaseFixture>, IClassFixture<RabbitDatabaseFixture>, IClassFixture<RedisDatabaseFixture>
{
    [Theory, Trait("Category", "PerformanceSmoke")]
    [InlineData(false)] [InlineData(true)]
    public async Task Seven_read_paths_have_bounded_queries_and_produce_benchmark_evidence(bool useRedis)
    {
        var queries = new QueryCounter(); var pool = new PoolCounter();
        var config = new Dictionary<string, string?> { ["Messaging:RabbitUri"] = rabbit.ConnectionString,
            ["Caching:Enabled"] = useRedis ? "true" : "false",
            ["Caching:ConnectionString"] = redis.ConnectionString,
            ["Caching:KeySecret"] = "phase11-benchmark-key-shared-between-replicas-001",
            ["Caching:Environment"] = "b" + Guid.NewGuid().ToString("N") };
        await using var factory = new IdentityPostgresFactory(database.ConnectionString, config) { CommandObserver = queries, ConnectionObserver = pool };
        await factory.ResetAsync();
        var client = factory.CreateClient(); var device = new DeviceRequest("benchmark-device", "Benchmark");
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest("benchmark@test.local", "Password!12345", true, "ar-SA", device));
        var challenge = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challengeId").GetGuid();
        var verified = await client.PostAsJsonAsync("/api/v1/auth/register/verify", new VerifyOtpRequest(challenge, factory.Otp.Get(challenge), device));
        var auth = (await verified.Content.ReadFromJsonAsync<AuthResult>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.AccessToken);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var id = await db.Users.Select(x => x.Id).SingleAsync();
            var role = new ApplicationRole { Id = Guid.NewGuid(), Name = "Benchmark", NormalizedName = "BENCHMARK", DisplayName = "Benchmark", ConcurrencyStamp = Guid.NewGuid().ToString() };
            db.Roles.Add(role); db.UserRoles.Add(new() { UserId = id, RoleId = role.Id });
            foreach (var code in new[] { "operations.view", "customer.view", "transaction.view.any", "audit.view", "report.view" }) db.RolePermissions.Add(new() { RoleId = role.Id, PermissionCode = code });
            await db.SaveChangesAsync();
            var customers = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();
            customers.Customers.Add(new Customer { IdentityUserId = id, FirstName = "Benchmark", LastName = "Test", Status = CustomerStatus.Active, KycStatus = KycStatus.Verified });
            await customers.SaveChangesAsync();
            await customers.Database.ExecuteSqlRawAsync("""
                INSERT INTO customers.customers ("Id", "IdentityUserId", "FirstName", "LastName", "PreferredLanguage", "Status", "KycStatus", "CreatedAt", "UpdatedAt", "ConcurrencyVersion")
                SELECT gen_random_uuid(), gen_random_uuid(), 'Synthetic', 'Benchmark', 'ar-SA', 'Active', 'Verified', now() - i * interval '1 second', now(), gen_random_uuid() FROM generate_series(1,10000) i;
                ANALYZE customers.customers;
                """);
        }
        var samples = int.TryParse(Environment.GetEnvironmentVariable("WALLET_PERF_SAMPLES"), out var n) ? n : 12;
        var evidence = new List<object>();
        foreach (var (name, path) in new[] { ("permissions", "/api/v1/admin/session"), ("dashboard", "/api/v1/admin/dashboard"),
            ("customers", "/api/v1/admin/customers/search"), ("audit", "/api/v1/admin/audit-logs"), ("history", "/api/v1/admin/transactions"),
            ("notifications", "/api/v1/notifications"), ("reports", "/api/v1/admin/reports") })
        {
            var metrics = factory.Services.GetRequiredService<CacheTelemetry>(); var cacheBefore = metrics.Snapshot;
            var before = queries.Count; var cold = Stopwatch.StartNew();
            var first = await client.GetAsync(path); Assert.True(first.IsSuccessStatusCode, name + ": " + await first.Content.ReadAsStringAsync()); cold.Stop();
            var coldQueries = queries.Count - before;
            var times = new List<double>(); before = queries.Count;
            var allocated = GC.GetTotalAllocatedBytes(); var cpu = Process.GetCurrentProcess().TotalProcessorTime; var wall = Stopwatch.StartNew();
            for (var i = 0; i < samples; i++) { var timer = Stopwatch.StartNew(); using var r = await client.GetAsync(path); Assert.True(r.IsSuccessStatusCode); times.Add(timer.Elapsed.TotalMilliseconds); }
            wall.Stop(); times.Sort(); var perRequest = (queries.Count - before) / (double)samples;
            Assert.InRange(perRequest, 1, 12); // Stable gate: bounded query work, never a shared-CI timing threshold.
            var cacheAfter = metrics.Snapshot; var hits = cacheAfter.Hits - cacheBefore.Hits; var misses = cacheAfter.Misses - cacheBefore.Misses;
            evidence.Add(new { name, samples, coldMs = cold.Elapsed.TotalMilliseconds, coldQueries, p50 = Percentile(.50), p95 = Percentile(.95), p99 = Percentile(.99),
                throughput = samples / wall.Elapsed.TotalSeconds, queriesPerRequest = perRequest, allocatedBytesPerRequest = (GC.GetTotalAllocatedBytes() - allocated) / samples,
                cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds, redisHits = hits, redisMisses = misses, redisHitRatio = hits + misses == 0 ? 0 : hits / (double)(hits + misses) });
            double Percentile(double p) => times[Math.Min(times.Count - 1, (int)Math.Ceiling(times.Count * p) - 1)];
        }
        var burst = Stopwatch.StartNew();
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => client.GetAsync("/api/v1/admin/customers/search")));
        Assert.All(concurrent, r => { Assert.True(r.IsSuccessStatusCode); r.Dispose(); }); burst.Stop();
        Assert.InRange(pool.Peak, 1, 64);
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using var plan = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) SELECT \"Id\", \"CreatedAt\" FROM customers.customers WHERE \"CreatedAt\" < now() ORDER BY \"CreatedAt\" DESC, \"Id\" DESC LIMIT 21", connection);
        var queryPlan = await plan.ExecuteScalarAsync();
        var mode = useRedis ? "redis" : "uncached";
        var output = (Environment.GetEnvironmentVariable("WALLET_PERF_OUTPUT") ?? "/tmp/wallet-phase11-smoke") + "-" + mode + ".json";
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { mode = config["Caching:Enabled"] == "true" ? "redis" : "baseline", customerRows = 10001, samples,
            pool = new { maxConnections = 64, concurrency = 64, observedPeakOpenConnections = pool.Peak, burstMs = burst.Elapsed.TotalMilliseconds, burstRequestsPerSecond = 64 / burst.Elapsed.TotalSeconds,
                note = "EF logical connection opens observed; physical pool size remains capped at 64. No timeouts in the concurrent burst." }, evidence, customerQueryPlan = JsonDocument.Parse((string)queryPlan!).RootElement }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
