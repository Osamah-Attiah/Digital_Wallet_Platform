using Xunit;

namespace DigitalWallet.Api.Tests;

public sealed partial class TransfersPostgresTests
{
    internal void EnableRedisForPhase11(string connection) => _factory.CachingConfiguration = new Dictionary<string, string?>
    {
        ["Caching:Enabled"] = "true", ["Caching:ConnectionString"] = connection,
        ["Caching:KeySecret"] = "financial-parity-test-key-shared-between-replicas-001",
        ["Caching:Environment"] = "f" + Guid.NewGuid().ToString("N"), ["Caching:BackoffMilliseconds"] = "100"
    };
}

public sealed class RedisFinancialPhase11Tests(IdentityDatabaseFixture database, RedisDatabaseFixture redis, RabbitDatabaseFixture rabbit)
    : IClassFixture<IdentityDatabaseFixture>, IClassFixture<RedisDatabaseFixture>, IClassFixture<RabbitDatabaseFixture>
{
    [Theory, Trait("Category", "Redis")]
    [InlineData(true)] [InlineData(false)]
    public async Task One_hundred_transfers_and_one_hundred_identical_retries_conserve_money_with_Redis_available_or_down(bool available)
    {
        var acceptance = new TransfersPostgresTests(database, rabbit); acceptance.EnableRedisForPhase11(redis.ConnectionString);
        await acceptance.InitializeAsync();
        if (!available) await redis.StopAsync();
        try
        {
            await acceptance.One_hundred_identical_HTTP_requests_create_one_transfer_and_one_journal();
            await acceptance.InitializeAsync();
            await acceptance.One_hundred_distinct_10_SAR_debits_from_100_SAR_complete_exactly_ten();
        }
        finally { try { await acceptance.DisposeAsync(); } finally { if (!available) await redis.StartAsync(); } }
    }
}
