using System.Xml.Linq;
using Xunit;

namespace DigitalWallet.ArchitectureTests;

public sealed class RedisBoundaryTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Digital_Wallet_Platform.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository missing.");
    }
    [Fact]
    public void Redis_client_is_only_available_to_the_caching_implementation()
    {
        foreach (var p in Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.csproj", SearchOption.AllDirectories))
        {
            if (Path.GetFileNameWithoutExtension(p) == "DigitalWallet.Caching") continue;
            Assert.DoesNotContain(XDocument.Load(p).Descendants("PackageReference"), x => (x.Attribute("Include")?.Value ?? "").Contains("Redis", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(XDocument.Load(p).Descendants("ProjectReference"), x => (x.Attribute("Include")?.Value ?? "").EndsWith("DigitalWallet.Caching.csproj", StringComparison.Ordinal) && Path.GetFileNameWithoutExtension(p) != "DigitalWallet.Api");
        }
    }
    [Theory]
    [InlineData("Modules/DigitalWallet.Ledger")]
    [InlineData("Modules/DigitalWallet.RiskLimits")]
    [InlineData("Modules/DigitalWallet.Transfers")]
    [InlineData("Modules/DigitalWallet.Transactions")]
    [InlineData("Modules/DigitalWallet.Reporting")]
    [InlineData("Modules/DigitalWallet.Messaging")]
    [InlineData("DigitalWallet.Api/Composition")]
    public void Financial_decisions_posting_and_job_outbox_transactions_cannot_use_cache(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root(), "src", folder), "*.cs", SearchOption.AllDirectories).Where(x => !x.Contains("/obj/") && !x.Contains("/bin/")))
        {
            var source = File.ReadAllText(file);
            foreach (var forbidden in new[] { "StackExchange.Redis", "DigitalWallet.Caching", "ISafeCache", "IDistributedCache", "IDistributedRateCoordinator", "IMemoryCache" }) Assert.DoesNotContain(forbidden, source);
        }
    }
    [Fact]
    public void Cache_contract_is_closed_and_cannot_serialize_financial_or_sensitive_objects()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "src/Modules/DigitalWallet.Caching.Contracts/SafeCaching.cs"));
        foreach (var forbidden in new[] { "Task<T>", "SetAsync<T>", "Balance", "Otp", "RefreshToken", "ReportReceipt", "TransferReceipt", "LimitUsage", "Idempotency" }) Assert.DoesNotContain(forbidden, source);
        foreach (var method in new[] { "PermissionsAsync", "SettingsAsync", "DashboardAsync" }) Assert.Contains(method, source);
    }
    [Fact]
    public void Caching_does_not_have_database_access_or_key_enumeration()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root(), "src/Modules/DigitalWallet.Caching"), "*.cs"))
        {
            var source = File.ReadAllText(file);
            foreach (var forbidden in new[] { "Npgsql", "EntityFrameworkCore", ".Keys(", "FLUSHDB", "FLUSHALL", "redis.call('KEYS'", "redis.call('SCAN'" }) Assert.DoesNotContain(forbidden, source);
        }
    }
}
