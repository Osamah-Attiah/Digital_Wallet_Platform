using DigitalWallet.Api.Hosting;
using DigitalWallet.Caching;
using DigitalWallet.Administration;
using DigitalWallet.Audit;
using DigitalWallet.Reporting;
using DigitalWallet.Reporting.Contracts;
using DigitalWallet.Api.Foundation;
using DigitalWallet.Api.Composition;
using DigitalWallet.Transfers;
using DigitalWallet.Transactions;
using DigitalWallet.Transactions.Application;
using DigitalWallet.Transactions.Endpoints;
using DigitalWallet.Transfers.Application;
using DigitalWallet.Transfers.Endpoints;
using DigitalWallet.Api.Foundation.Configuration;
using DigitalWallet.Api.Foundation.Errors;
using DigitalWallet.Api.Foundation.Observability;
using DigitalWallet.SharedKernel.Correlation;
using DigitalWallet.Identity;
using DigitalWallet.Customers;
using DigitalWallet.Wallets;
using DigitalWallet.Ledger;
using DigitalWallet.Ledger.Endpoints;
using DigitalWallet.Beneficiaries;
using DigitalWallet.RiskLimits;
using DigitalWallet.RiskLimits.Endpoints;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using System.Text.Json;
using DigitalWallet.Messaging;
using DigitalWallet.Notifications;

if (args.Contains("--fixture-code", StringComparer.Ordinal))
{
    if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") is not ("Development" or "Testing")) throw new InvalidOperationException("Fixture client requires a test environment.");
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    client.DefaultRequestHeaders.Add("X-Fixture-Key", File.ReadAllText("/run/secrets/fixture_key").Trim());
    Console.WriteLine(await client.GetStringAsync("http://notification-sink:8080/otp?id=" + Uri.EscapeDataString(args.Last())));
    return;
}
if (args.Contains("--healthcheck", StringComparer.Ordinal))
{
    Environment.Exit(await DigitalWallet.Api.Hosting.ContainerProbe.RunAsync(args));
    return;
}
var builder = WebApplication.CreateBuilder(args);
DigitalWallet.Api.Hosting.ContainerConfiguration.Load(builder.Configuration);
if (args.Contains("--migrate", StringComparer.Ordinal))
    throw new InvalidOperationException("Use the independent DigitalWallet.Migrator host.");
builder.AddWalletHttpSecurity();
builder.AddWalletObservability();

builder.Services
    .AddOptions<PlatformOptions>()
    .Bind(builder.Configuration.GetRequiredSection(PlatformOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<PlatformOptions>, PlatformOptionsValidator>();

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context => ProblemDetailsFactory.Enrich(context.HttpContext, context.ProblemDetails);
});
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddWalletModules(builder.Configuration);
builder.Services.AddDevelopmentOtp(builder.Configuration, builder.Environment);
builder.Services.AddSyntheticDemoOtp(builder.Configuration);
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(25));
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("reversal-otp", context => RateLimitPartition.GetFixedWindowLimiter($"reversal-otp:{context.User.FindFirst("sub")?.Value}", _ => new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));
    options.AddPolicy("reversal-approve", context => RateLimitPartition.GetFixedWindowLimiter($"reversal-approve:{context.User.FindFirst("sub")?.Value}", _ => new FixedWindowRateLimiterOptions { PermitLimit = 200, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("transfer-otp", context => RateLimitPartition.GetFixedWindowLimiter($"transfer-otp:{context.User.FindFirst("sub")?.Value}", _ => new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));
    options.AddPolicy("transfer-confirm", context => RateLimitPartition.GetFixedWindowLimiter($"transfer-confirm:{context.User.FindFirst("sub")?.Value}", _ => new FixedWindowRateLimiterOptions { PermitLimit = 200, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, ct) => { DigitalWallet.SharedKernel.Observability.WalletTelemetry.Security(DigitalWallet.SharedKernel.Observability.SecurityEvent.RateRejected, DigitalWallet.SharedKernel.Observability.TelemetryModule.Api); context.HttpContext.Response.Headers.RetryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)).ToString() : "60"; return ValueTask.CompletedTask; };
    options.AddPolicy("identity-login", context => RateLimitPartition.GetFixedWindowLimiter($"login:{context.Connection.RemoteIpAddress}", _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("identity-register", context => RateLimitPartition.GetFixedWindowLimiter($"register:{context.Connection.RemoteIpAddress}", _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));
    options.AddPolicy("identity-otp", context => RateLimitPartition.GetFixedWindowLimiter($"otp:{context.Connection.RemoteIpAddress}", _ => new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));
    options.AddPolicy("identity-refresh", context => RateLimitPartition.GetFixedWindowLimiter($"refresh:{context.Connection.RemoteIpAddress}", _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("identity-password", context => RateLimitPartition.GetFixedWindowLimiter($"password:{context.Connection.RemoteIpAddress}", _ => new FixedWindowRateLimiterOptions { PermitLimit = 3, Window = TimeSpan.FromMinutes(10), QueueLimit = 0 }));
    options.AddPolicy("beneficiary-lookup", context => RateLimitPartition.GetFixedWindowLimiter($"beneficiary-lookup:{context.User.FindFirst("sub")?.Value ?? context.Connection.RemoteIpAddress?.ToString()}", _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));
    options.AddPolicy("beneficiary-otp", context => RateLimitPartition.GetFixedWindowLimiter($"beneficiary-otp:{context.User.FindFirst("sub")?.Value ?? context.Connection.RemoteIpAddress?.ToString()}", _ => new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));
});

var app = builder.Build();
ProductionHardening.Enforce(builder);
DevelopmentFixture.Configure(app);
_ = app.Services.GetRequiredService<CacheOptions>();

app.UseForwardedHeaders();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<RequestTelemetryMiddleware>();
app.UseExceptionHandler();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseStatusCodePages(async statusCodeContext =>
{
    var response = statusCodeContext.HttpContext.Response;
    if (response.HasStarted || response.StatusCode < StatusCodes.Status400BadRequest)
    {
        return;
    }

    var problemDetails = ProblemDetailsFactory.Create(
        statusCodeContext.HttpContext,
        response.StatusCode,
        "HTTP_ERROR",
        "The request could not be completed.");

    response.ContentType = "application/problem+json";
    await JsonSerializer.SerializeAsync(
        response.Body,
        problemDetails,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (app.Environment.IsProduction())
{
    app.UseHsts();
    // Docker probes use loopback HTTP. Only readiness/liveness may bypass redirection there.
    app.UseWhen(context => !(context.Connection.RemoteIpAddress is { } peer && System.Net.IPAddress.IsLoopback(peer)
        && context.Request.Path is { Value: "/health/ready" or "/health/live" }), branch => branch.UseHttpsRedirection());
}
app.UseCors("AdminSpa");
app.UseRequestTimeouts();
app.UseAuthentication();
app.UseMiddleware<DistributedRateMiddleware>();
app.UseRateLimiter();
app.UseMiddleware<AdminDeniedAuditMiddleware>();
app.UseAuthorization();
var routes = app.MapGroup("").AddEndpointFilter<AdminAuditFilter>();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();
app.MapGet("/health/ready", DependencyHealth.CheckAsync).AllowAnonymous();
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" })).AllowAnonymous();
routes.MapIdentityEndpoints();
routes.MapCustomersEndpoints();
routes.MapWalletsEndpoints(app.Environment);
routes.MapLedgerEndpoints();
routes.MapBeneficiariesEndpoints();
routes.MapRiskLimitsEndpoints();
routes.MapTransferEndpoints();
routes.MapTransactionEndpoints();
routes.MapNotificationEndpoints();
routes.MapAdministrationEndpoints();
routes.MapReportingEndpoints();
routes.MapGet("/api/v1/admin/audit-logs", ([AsParameters] AuditQuery query, System.Security.Claims.ClaimsPrincipal user, AuditSearch search, CancellationToken ct) =>
    AdministrationModule.Run(() => search.SearchAsync(query, AdministrationModule.Actor(user), ct))).RequireAuthorization("Permission:audit.view");

app.Run();

public partial class Program;
