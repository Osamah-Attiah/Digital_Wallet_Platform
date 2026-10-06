using System.Diagnostics;
using Microsoft.AspNetCore.Routing;
using DigitalWallet.SharedKernel.Observability;

namespace DigitalWallet.Api.Foundation.Observability;
public sealed class RequestTelemetryMiddleware(RequestDelegate next, ILogger<RequestTelemetryMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext h, OperationalMetrics metrics)
    {
        var start = Stopwatch.GetTimestamp(); metrics.Active.Add(1);
        var failed = false;
        using var context = logger.BeginScope(new Dictionary<string, object?>
        {
            ["TraceId"] = Activity.Current?.TraceId.ToHexString() ?? "", ["SpanId"] = Activity.Current?.SpanId.ToHexString() ?? "",
            ["CorrelationId"] = h.TraceIdentifier, ["CausationId"] = Activity.Current?.GetTagItem("causation.id")
        });
        try { await next(h); }
        catch { failed = true; throw; }
        finally
        {
            var route = SafeTelemetry.Route((h.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText);
            var status = failed ? 500 : h.Response.StatusCode;
            var family = status / 100 + "xx";
            var seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
            var code = SafeTelemetry.Error(h.Items["SafeErrorCode"] as string ?? (status >= 500 ? "INTERNAL_ERROR" : status == 429 ? "RATE_LIMITED" : status >= 400 ? "HTTP_ERROR" : "NONE"));
            var outcome = status >= 500 ? "Failed" : status >= 400 ? "Rejected" : "Completed";
            var tags = new TagList { { "route", route }, { "status_family", family } };
            metrics.Active.Add(-1); metrics.Requests.Add(1, tags); metrics.Latency.Record(seconds, tags);
            if (status >= 400) metrics.Errors.Add(1, new KeyValuePair<string, object?>("error_code", code));
            Activity.Current?.SetTag("http.route", route);
            Activity.Current?.SetTag("wallet.error.code", code);
            if (code.StartsWith("OTP_", StringComparison.Ordinal) && Activity.Current?.GetTagItem("wallet.otp.recorded") is not true)
                WalletTelemetry.Security(code == "OTP_EXPIRED" ? SecurityEvent.OtpExpired : code is "OTP_LOCKED" or "OTP_ATTEMPTS_EXCEEDED" ? SecurityEvent.OtpLocked : SecurityEvent.OtpInvalid, TelemetryModule.Identity);
            if (status == 429 && (route.Contains("otp", StringComparison.Ordinal) || route.EndsWith("/verify", StringComparison.Ordinal)))
                WalletTelemetry.Security(SecurityEvent.OtpThrottled, TelemetryModule.Identity);
            var admin = route.StartsWith("/api/v1/admin/", StringComparison.Ordinal);
            if (admin && status is 401 or 403) WalletTelemetry.Security(SecurityEvent.AdminDenied, TelemetryModule.Administration);
            if (route.EndsWith("/pii", StringComparison.Ordinal)) WalletTelemetry.Security(status < 400 ? SecurityEvent.PiiRevealed : SecurityEvent.PiiDenied, TelemetryModule.Administration);
            if (admin && status < 400 && route.Contains("/reports", StringComparison.Ordinal))
            {
                if (h.Request.Method == "POST" && route == "/api/v1/admin/reports") WalletTelemetry.Security(SecurityEvent.ReportCreated, TelemetryModule.Reporting);
                if (route.EndsWith("/download", StringComparison.Ordinal)) WalletTelemetry.Security(SecurityEvent.ReportDownloaded, TelemetryModule.Reporting);
            }
            if (admin && status < 400 && h.Request.Method is "PUT" or "POST" && (route.Contains("/roles", StringComparison.Ordinal) || route.Contains("/staff", StringComparison.Ordinal)))
                WalletTelemetry.Security(SecurityEvent.AuthorizationChanged, TelemetryModule.Administration);
            logger.Log(status >= 500 ? LogLevel.Error : LogLevel.Information, new EventId(1001, "RequestCompleted"),
                "{Module} {Route} {StatusFamily} {Duration} {ErrorCode} {Outcome} {ActorType}",
                route.StartsWith("/api/v1/auth", StringComparison.Ordinal) ? "Identity" : route.Contains("/reports", StringComparison.Ordinal) ? "Reporting" : route.StartsWith("/api/v1/transfers", StringComparison.Ordinal) ? "Transfers" : route.Contains("/transactions", StringComparison.Ordinal) || route.Contains("/reversal-requests", StringComparison.Ordinal) ? "Transactions" : admin ? "Administration" : "Api", route, family, seconds, code, outcome, admin ? "Staff" : h.User.Identity?.IsAuthenticated == true ? "Customer" : "Anonymous");
        }
    }
}
public sealed class SecurityLogBridge(ILogger<SecurityLogBridge> logger) : IHostedService
{
    public Task StartAsync(CancellationToken ct) { WalletTelemetry.SecurityRaised += Write; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) { WalletTelemetry.SecurityRaised -= Write; return Task.CompletedTask; }
    private void Write(SecurityRecord r) => logger.LogWarning(new EventId((int)r.Event, r.Event.ToString()),
        "{Module} {TraceId} {SpanId} {CorrelationId} {CausationId} {Outcome} {ActorType}", r.Module.ToString(), r.TraceId, r.SpanId, r.CorrelationId, r.CausationId,
        r.Event is SecurityEvent.PiiRevealed or SecurityEvent.ReportCreated or SecurityEvent.ReportDownloaded or SecurityEvent.AuthorizationChanged ? "Completed" : "Rejected", r.Module is TelemetryModule.Administration or TelemetryModule.Reporting ? "Staff" : r.Module is TelemetryModule.Messaging or TelemetryModule.Ledger ? "Worker" : "Anonymous");
}
