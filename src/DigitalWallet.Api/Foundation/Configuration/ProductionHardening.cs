using System.Net;
using System.Text;
using System.Text.Json;
using DigitalWallet.Api.Foundation.Observability;
using DigitalWallet.Identity;
using Microsoft.Extensions.Configuration.Json;
using Npgsql;
using StackExchange.Redis;
namespace DigitalWallet.Api.Foundation.Configuration;

public sealed class HttpSecurityOptions
{
    public string[] AdminOrigins { get; init; } = [];
    public string[] KnownProxies { get; init; } = [];
    public long MaxRequestBodyBytes { get; init; } = 65536;
    public int HeaderTimeoutSeconds { get; init; } = 10;
    public string ExternalWorkersDecision { get; init; } = "";
}
public static class ProductionHardening
{
    private static readonly string[] SecretPaths = ["Identity:Jwt:SigningKey", "Identity:Otp:HashKey", "Identity:TokenHashKey", "Caching:KeySecret"];
    public static bool StrongSecret(string? value) => value is { Length: >= 32 and <= 4096 } && Encoding.UTF8.GetByteCount(value) >= 32 &&
        value.Distinct().Count() >= 12 && !Unsafe(value);
    private static bool Unsafe(string? value) => string.IsNullOrWhiteSpace(value) || new[]
        { "development", "guest", "changeme", "change-me", "replace", "placeholder", "password", "example", "default", "test", "postgres", "localhost" }
        .Any(x => value.Contains(x, StringComparison.OrdinalIgnoreCase));
    public static IReadOnlyList<string> Validate(IConfiguration config, bool production)
    {
        var failures = new List<string>();
        var h = config.GetSection("HttpSecurity").Get<HttpSecurityOptions>() ?? new();
        if (h.MaxRequestBodyBytes is < 1024 or > 1048576 || h.HeaderTimeoutSeconds is < 5 or > 30) failures.Add("HttpSecurity limits are invalid.");
        foreach (var ip in h.KnownProxies) if (!IPAddress.TryParse(ip, out var parsed) || parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.IPv6Any)) failures.Add("KnownProxies must contain explicit IP addresses.");
        foreach (var origin in h.AdminOrigins)
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var u) || u.UserInfo.Length > 0 || u.Query.Length > 0 || u.Fragment.Length > 0 || u.AbsolutePath != "/" ||
                origin.Contains('*') || u.Scheme is not ("https" or "http") || production && u.Scheme != "https" || !production && u.Scheme == "http" && !u.IsLoopback)
                failures.Add("AdminOrigins must contain exact trusted origins.");
        try { (config.GetSection("Observability").Get<ObservabilityOptions>() ?? new()).Validate(production); }
        catch { failures.Add("Observability configuration is invalid."); }
        try
        {
            var pg = new NpgsqlConnectionStringBuilder(config["Platform:PostgreSql:ConnectionString"]);
            if (string.IsNullOrWhiteSpace(pg.Host) || string.IsNullOrWhiteSpace(pg.Database) || pg.IncludeErrorDetail || pg.LogParameters) failures.Add("PostgreSQL settings require a host/database and safe error/parameter options.");
        }
        catch { failures.Add("PostgreSQL configuration is invalid."); }
        try { (config.GetSection("Messaging").Get<DigitalWallet.Messaging.MessagingOptions>() ?? new()).Validate(); }
        catch { failures.Add("Messaging configuration is invalid."); }
        try { var c = config.GetSection("Caching").Get<DigitalWallet.Caching.CacheOptions>() ?? new(); c.Validate(); if (c.Enabled) _ = ConfigurationOptions.Parse(c.ConnectionString); }
        catch { failures.Add("Caching configuration is invalid."); }
        if (config.GetValue<bool>("DevelopmentFixtures:Enabled") && production) failures.Add("Development fixtures are forbidden in Production.");
        if (!production) return failures;
        foreach (var key in SecretPaths.Take(3)) if (!StrongSecret(config[key])) failures.Add(key + " must be supplied as a strong production secret.");
        if (SecretPaths.Take(3).Select(k => config[k]).Distinct().Count() != 3) failures.Add("Identity keys must be independent.");
        if (config.GetValue<bool>("Caching:Enabled") && !StrongSecret(config["Caching:KeySecret"])) failures.Add("Caching:KeySecret must be a strong production secret.");
        try
        {
            var pg = new NpgsqlConnectionStringBuilder(config["Platform:PostgreSql:ConnectionString"]);
            if (Unsafe(pg.Username) || Unsafe(pg.Password) || pg.Password!.Length < 16 || string.IsNullOrWhiteSpace(pg.Host) ||
                pg.SslMode != SslMode.VerifyFull || pg.IncludeErrorDetail || pg.LogParameters || pg.Timeout > 10 || pg.CommandTimeout > 60 || pg.MaxPoolSize > 200)
                failures.Add("PostgreSQL requires restricted credentials, VerifyFull TLS, safe details and bounded pool/timeouts.");
        }
        catch { failures.Add("PostgreSQL production configuration is invalid."); }
        if (!Uri.TryCreate(config["Messaging:RabbitUri"], UriKind.Absolute, out var rabbit) || rabbit.Scheme != "amqps" ||
            !rabbit.UserInfo.Contains(':') || rabbit.UserInfo.Split(':').Any(x => Unsafe(Uri.UnescapeDataString(x))) ||
            Uri.UnescapeDataString(rabbit.UserInfo.Split(':').Last()).Length < 16 || rabbit.Query.Length > 0)
            failures.Add("RabbitMQ requires amqps and restricted production credentials.");
        if (config.GetValue<bool>("Caching:Enabled"))
        {
            try
            {
                var redis = ConfigurationOptions.Parse(config["Caching:ConnectionString"] ?? "");
                if (!redis.Ssl || redis.EndPoints.Count == 0 || Unsafe(redis.User) || redis.User == "default" || Unsafe(redis.Password) || redis.Password!.Length < 16)
                    failures.Add("Redis requires TLS and restricted ACL credentials.");
                if (config["Caching:Environment"] is not "production") failures.Add("Redis environment namespace must be production.");
            }
            catch { failures.Add("Redis production configuration is invalid."); }
        }
        var hosts = (config["AllowedHosts"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (hosts.Length == 0 || hosts.Any(x => x.Contains('*') || x.Contains('/') || x.Contains(':') || Uri.CheckHostName(x) == UriHostNameType.Unknown)) failures.Add("AllowedHosts must list exact production hostnames.");
        if (h.AdminOrigins.Length == 0) failures.Add("AdminOrigins must list the production SPA origin.");
        if (config.GetValue<bool>("DetailedErrors") || config.GetValue<bool>("DeveloperExceptionPage:Enabled") || config.GetValue<bool>("IdentityModel:ShowPII")) failures.Add("Development error details must be disabled.");
        if ((config["Hosting:Role"] == "Api" || !config.GetValue("Messaging:WorkersEnabled", true) || !config.GetValue("Reporting:WorkersEnabled", true) ||
             !config.GetValue("Wallets:ProvisioningWorkerEnabled", true) || !config.GetValue("Observability:ReconciliationEnabled", true)) &&
            (h.ExternalWorkersDecision.Length is < 12 or > 160 || h.ExternalWorkersDecision.Any(char.IsControl)))
            failures.Add("Disabled required workers need an explicit operations decision reference.");
        if (config is IConfigurationRoot configuration)
            foreach (var provider in configuration.Providers.Where(p => p is JsonConfigurationProvider or JsonStreamConfigurationProvider))
            {
                foreach (var key in SecretPaths) if (provider.TryGet(key, out var v) && !string.IsNullOrWhiteSpace(v)) failures.Add("Secrets must not be embedded in JSON configuration files.");
                foreach (var key in new[] { "Platform:PostgreSql:ConnectionString", "Messaging:RabbitUri", "Caching:ConnectionString" })
                    if (provider.TryGet(key, out var v) && ContainsCredentials(key, v)) failures.Add("Dependency credentials must not be embedded in JSON configuration files.");
            }
        return failures.Distinct().ToArray();
    }
    private static bool ContainsCredentials(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            if (key == "Messaging:RabbitUri") return Uri.TryCreate(value, UriKind.Absolute, out var u) && u.UserInfo.Length > 0;
            if (key.StartsWith("Caching", StringComparison.Ordinal)) return !string.IsNullOrEmpty(ConfigurationOptions.Parse(value).Password);
            return !string.IsNullOrEmpty(new NpgsqlConnectionStringBuilder(value).Password);
        }
        catch { return true; }
    }
    public static void Enforce(WebApplicationBuilder b)
    {
        var failures = Validate(b.Configuration, b.Environment.IsProduction());
        if (failures.Count > 0) throw new InvalidOperationException("Startup configuration rejected: " + string.Join(" ", failures));
    }
}
