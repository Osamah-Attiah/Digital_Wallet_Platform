using DigitalWallet.Api.Hosting;
using DigitalWallet.Api.Foundation;
using DigitalWallet.Api.Foundation.Configuration;
using DigitalWallet.Api.Foundation.Observability;

if (args.Contains("--healthcheck", StringComparer.Ordinal)) { Environment.Exit(await ContainerProbe.RunAsync(args)); return; }
var builder = WebApplication.CreateBuilder(args);
builder.Configuration["Hosting:Role"] = "Worker";
builder.Configuration["Hosting:ServiceName"] = "digitalwallet.worker";
ContainerConfiguration.Load(builder.Configuration);
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(25));
builder.AddWalletObservability();
builder.Services.AddWalletModules(builder.Configuration);
ProductionHardening.Enforce(builder);
var app = builder.Build();
app.MapGet("/health/live", () => Results.Ok(new { status = "alive", role = "worker" })).AllowAnonymous();
app.MapGet("/health/ready", async (IConfiguration config, DigitalWallet.Caching.RedisRuntime redis, DigitalWallet.Caching.CacheOptions options, CancellationToken ct) => await DependencyHealth.CheckAsync(config, redis, options, ct)).AllowAnonymous();
await app.RunAsync();
