using DigitalWallet.Caching;
using DigitalWallet.Caching.Contracts;

namespace DigitalWallet.Api.Foundation;

public sealed class DistributedRateMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IDistributedRateCoordinator coordinator, CacheOptions options)
    {
        if (!options.Enabled) { await next(context); return; }
        var policy = Policy(context.Request.Method, context.Request.Path.Value ?? "");
        if (policy != null)
        {
            var subject = context.User.FindFirst("sub")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown-peer";
            var decision = await coordinator.AcquireAsync(policy, subject, context.RequestAborted);
            if (decision.Fallback) DigitalWallet.SharedKernel.Observability.WalletTelemetry.Security(DigitalWallet.SharedKernel.Observability.SecurityEvent.RateFallback, DigitalWallet.SharedKernel.Observability.TelemetryModule.Api);
            if (!decision.Allowed)
            {
                DigitalWallet.SharedKernel.Observability.WalletTelemetry.Security(DigitalWallet.SharedKernel.Observability.SecurityEvent.RateRejected, DigitalWallet.SharedKernel.Observability.TelemetryModule.Api);
                context.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await Results.Problem(statusCode: 429, title: "Too many requests.", extensions: new Dictionary<string, object?> { ["code"] = "RATE_LIMITED" }).ExecuteAsync(context);
                return;
            }
        }
        await next(context);
    }
    public static RatePolicy? Policy(string method, string path)
    {
        if (method is not ("POST" or "PUT" or "DELETE")) return null;
        if (path == "/api/v1/auth/login") return new("login", 5, TimeSpan.FromMinutes(1));
        if (path.Contains("/otp", StringComparison.Ordinal) || path.Contains("/session/mfa", StringComparison.Ordinal) ||
            path.StartsWith("/api/v1/beneficiaries/", StringComparison.Ordinal) && path.EndsWith("/verify", StringComparison.Ordinal) ||
            path is "/api/v1/auth/register/verify" or "/api/v1/auth/reset-password") return new("otp", 8, TimeSpan.FromMinutes(5));
        if (path == "/api/v1/beneficiaries") return new("otp-issue", 10, TimeSpan.FromMinutes(5));
        if (path is "/api/v1/auth/register" or "/api/v1/auth/forgot-password") return new("otp-issue", 10, TimeSpan.FromMinutes(5));
        if (path.StartsWith("/api/v1/admin/customers/", StringComparison.Ordinal) && path.EndsWith("/pii", StringComparison.Ordinal)) return new("pii", 10, TimeSpan.FromMinutes(1));
        if (path == "/api/v1/admin/reports") return new("reports", 10, TimeSpan.FromMinutes(1));
        if (path.StartsWith("/api/v1/admin/", StringComparison.Ordinal)) return new("administration", 30, TimeSpan.FromMinutes(1));
        return null;
    }
}
