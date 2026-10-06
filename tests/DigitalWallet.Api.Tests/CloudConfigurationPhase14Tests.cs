using DigitalWallet.Api.Hosting;
using Microsoft.Extensions.Configuration;
using Npgsql;
using StackExchange.Redis;
using Xunit;
namespace DigitalWallet.Api.Tests;

public sealed class CloudConfigurationPhase14Tests
{
    [Fact]
    public async Task Synthetic_mailbox_rejects_real_recipients_before_writing()
    {
        var sender = new SyntheticDemoOtpSender(new ConfigurationBuilder().Build());
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(Guid.NewGuid(), "real@example.com", "123456", DigitalWallet.Identity.OtpPurpose.Registration, CancellationToken.None));
    }
    [Theory]
    [InlineData("Api", "wallet_api")]
    [InlineData("Worker", "wallet_worker")]
    [InlineData("Migrator", "wallet_migrator")]
    public void Cloud_credentials_keep_separate_roles_and_require_verified_TLS(string role, string expectedAccount)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wallet-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var name in new[] { "api_db_password", "worker_db_password", "migrator_db_password", "jwt_key", "otp_key", "token_key", "cache_key", "redis_password", "rabbit_password", "rabbit_api_password" })
                File.WriteAllText(Path.Combine(directory, name), "synthetic-test-only-value-with-32-characters");
            var config = new ConfigurationManager();
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Hosting:Role"] = role, ["Hosting:SecretsDirectory"] = directory, ["Hosting:DependencyTls"] = "true" });
            ContainerConfiguration.Load(config);
            var pg = new NpgsqlConnectionStringBuilder(config["Platform:PostgreSql:ConnectionString"]);
            Assert.Equal(expectedAccount, pg.Username);
            Assert.Equal(SslMode.VerifyFull, pg.SslMode);
            Assert.Equal("/run/tls/ca.crt", pg.RootCertificate);
            Assert.Equal(GssEncryptionMode.Disable, pg.GssEncryptionMode);
            Assert.False(pg.IncludeErrorDetail); Assert.False(pg.LogParameters);
            var redis = ConfigurationOptions.Parse(config["Caching:ConnectionString"]!);
            Assert.True(redis.Ssl); Assert.Equal("redis", redis.SslHost); Assert.Equal("wallet_cache", redis.User);
            var rabbit = new Uri(config["Messaging:RabbitUri"]!);
            Assert.Equal("amqps", rabbit.Scheme); Assert.Equal(5671, rabbit.Port);
            Assert.StartsWith(role == "Api" ? "wallet_observer:" : "wallet_worker:", rabbit.UserInfo);
        }
        finally { Directory.Delete(directory, true); }
    }
}
