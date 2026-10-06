using Npgsql;
namespace DigitalWallet.Api.Hosting;

public static class ContainerConfiguration
{
    public static void Load(ConfigurationManager configuration)
    {
        if (configuration["Hosting:SecretsDirectory"] is not { Length: > 0 } directory) return;
        string Read(string name) => File.ReadAllText(Path.Combine(directory, name)).Trim();
        var role = configuration["Hosting:Role"] ?? throw new InvalidOperationException("A container host role is required.");
        var account = role switch { "Api" => "wallet_api", "Worker" => "wallet_worker", "Migrator" => "wallet_migrator", _ => throw new InvalidOperationException("Invalid container role.") };
        var tls = configuration.GetValue<bool>("Hosting:DependencyTls");
        var values = new Dictionary<string, string?>
        {
            ["Platform:PostgreSql:ConnectionString"] = new NpgsqlConnectionStringBuilder
            {
                Host = "postgres",
                Database = configuration["Migration:Database"] ?? "digital_wallet",
                Username = account,
                Password = Read(role.ToLowerInvariant() + "_db_password"),
                Timeout = 5,
                CommandTimeout = 30,
                MaxPoolSize = 32,
                SslMode = tls ? SslMode.VerifyFull : SslMode.Disable,
                RootCertificate = tls ? "/run/tls/ca.crt" : null,
                // These accounts use SCRAM passwords; Kerberos/GSSAPI is not an authentication mechanism here.
                GssEncryptionMode = GssEncryptionMode.Disable,
                IncludeErrorDetail = false,
                LogParameters = false
            }.ConnectionString,
            ["Identity:Jwt:SigningKey"] = Read("jwt_key"),
            ["Identity:Otp:HashKey"] = Read("otp_key"),
            ["Identity:TokenHashKey"] = Read("token_key"),
            ["Caching:KeySecret"] = Read("cache_key"),
            ["Caching:ConnectionString"] = (tls ? "redis:6380,ssl=true,sslHost=redis," : "redis:6379,") + "user=wallet_cache,password=" + Read("redis_password") + ",abortConnect=false",
            ["Messaging:RabbitUri"] = (tls ? "amqps://" : "amqp://") + (role == "Api" ? "wallet_observer" : "wallet_worker") + ":" + Uri.EscapeDataString(Read(role == "Api" ? "rabbit_api_password" : "rabbit_password")) + (tls ? "@rabbitmq:5671/" : "@rabbitmq:5672/"),
        };
        if (configuration.GetValue<bool>("DevelopmentFixtures:Enabled")) values["DevelopmentFixtures:Key"] = Read("fixture_key");
        configuration.AddInMemoryCollection(values);
    }
}

public static class ContainerProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            var response = await client.GetAsync(args.LastOrDefault(a => a.StartsWith("http://", StringComparison.Ordinal)) ?? "http://127.0.0.1:8080/health/ready");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch { return 1; }
    }
}
