using DigitalWallet.Api.Hosting;
using DigitalWallet.Api.Foundation.Configuration;
using DigitalWallet.Messaging;
using DigitalWallet.Reporting;
using DigitalWallet.Wallets.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
namespace DigitalWallet.Api.Tests;

public sealed class PackagingPhase13Tests
{
    private static IConfiguration Config(string role) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Hosting:Role"] = role,
        ["Platform:PostgreSql:ConnectionString"] = "Host=localhost;Database=packaging",
        ["Identity:Jwt:SigningKey"] = "phase13-local-unit-key-with-32-characters",
        ["Identity:Jwt:Issuer"] = "DigitalWallet",
        ["Identity:Jwt:Audience"] = "DigitalWallet.Api"
    }).Build();
    [Theory]
    [InlineData("Api")]
    [InlineData("Migrator")]
    public void Non_worker_hosts_have_no_business_background_workers(string role)
    {
        var services = new ServiceCollection(); services.AddWalletModules(Config(role));
        Assert.DoesNotContain(services, s => s.ServiceType == typeof(IHostedService) && s.ImplementationType?.Namespace?.StartsWith("DigitalWallet", StringComparison.Ordinal) == true);
    }
    [Fact]
    public void Worker_registers_each_business_worker_exactly_once()
    {
        var services = new ServiceCollection(); services.AddWalletModules(Config("Worker"));
        var types = services.Where(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType?.Namespace?.StartsWith("DigitalWallet", StringComparison.Ordinal) == true).Select(s => s.ImplementationType).ToArray();
        Assert.Equal(6, types.Length); Assert.Equal(types.Length, types.Distinct().Count());
        Assert.Contains(typeof(OutboxWorker), types); Assert.Contains(typeof(NotificationDeliveryWorker), types);
        Assert.Contains(typeof(ReportDeliveryWorker), types); Assert.Contains(typeof(MessagingMetricsWorker), types);
        Assert.Contains(typeof(ReportRecoveryWorker), types); Assert.Contains(typeof(WalletProvisioningWorker), types);
    }
    [Fact]
    public void Unknown_host_role_is_rejected() => Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWalletModules(Config("Typo")));
    [Fact]
    public void Production_rejects_development_fixtures_even_with_other_configuration_errors()
    {
        var c = new ConfigurationBuilder().AddConfiguration(Config("Api")).AddInMemoryCollection(new Dictionary<string, string?> { ["DevelopmentFixtures:Enabled"] = "true" }).Build();
        Assert.Contains("Development fixtures are forbidden in Production.", ProductionHardening.Validate(c, true));
    }
    [Theory]
    [InlineData("")]
    [InlineData("incorrect")]
    public void Fixture_key_rejects_missing_or_incorrect_credentials(string supplied) => Assert.False(DevelopmentFixture.ValidKey(supplied, new string('a', 64)));
    [Fact]
    public void Fixture_key_accepts_only_matching_full_credential() => Assert.True(DevelopmentFixture.ValidKey(new string('a', 64), new string('a', 64)));
}
