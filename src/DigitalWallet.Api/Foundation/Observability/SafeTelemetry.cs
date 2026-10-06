using System.Diagnostics;
using System.Text.Json;
using Serilog.Events;
using Serilog.Formatting;
using DigitalWallet.SharedKernel.Observability;

namespace DigitalWallet.Api.Foundation.Observability;

public static class SafeTelemetry
{
    public static string Text(string? value, int max = 128) => new((value ?? "").Where(c => !char.IsControl(c) && c is not '\u2028' and not '\u2029').Take(max).ToArray());
    public static string Identifier(string? value) => value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ? value : "";
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    {
        "AED",
        "ALREADY_REVERSED",
        "AMOUNT_OUT_OF_RANGE",
        "AUDIT_FACT_INVALID",
        "BENEFICIARY_NOT_ACTIVE",
        "BENEFICIARY_NOT_FOUND",
        "BENEFICIARY_NOT_VERIFIED",
        "BHD",
        "CONCURRENCY_CONFLICT",
        "COOLING_PERIOD_ACTIVE",
        "CUSTOMER",
        "CUSTOMER_NOT_ACTIVE",
        "CUSTOMER_NOT_FOUND",
        "DAILY_LIMIT_EXCEEDED",
        "DATE_RANGE_TOO_LARGE",
        "DELETE",
        "DEPENDENCY_UNAVAILABLE",
        "EUR",
        "GBP",
        "HTTP_ERROR",
        "IDEMPOTENCY_KEY_REQUIRED",
        "IDEMPOTENCY_KEY_REUSED",
        "INSUFFICIENT_BALANCE",
        "INSUFFICIENT_RECOVERY_FUNDS",
        "INTERNAL_ERROR",
        "INVALID_CURSOR",
        "INVALID_REPORT_TYPE",
        "INVALID_REVERSAL",
        "JOURNAL_NOT_FOUND",
        "JOURNAL_UNBALANCED",
        "JPY",
        "KWD",
        "LEDGER_UNAVAILABLE",
        "MONTHLY_LIMIT_EXCEEDED",
        "NONE",
        "NOTIFICATION_NOT_FOUND",
        "NOT_REVERSIBLE",
        "OMR",
        "OTHER",
        "OTP_ATTEMPTS_EXCEEDED",
        "OTP_EXPIRED",
        "OTP_INVALID",
        "PERMISSION_REVOKED",
        "POST",
        "PRIVILEGE_ESCALATION_FORBIDDEN",
        "PROFILE_EXISTS",
        "PUT",
        "QAR",
        "QUOTE_EXPIRED",
        "QUOTE_NOT_FOUND",
        "QUOTE_STALE",
        "QUOTE_USED",
        "RATE_LIMITED",
        "RECENT_MFA_REQUIRED",
        "RECIPIENT_NOT_FOUND",
        "REPORT_EXPIRED",
        "REPORT_FILE_UNAVAILABLE",
        "REPORT_FORBIDDEN",
        "REPORT_NOT_FOUND",
        "REPORT_NOT_READY",
        "REPORT_ROW_LIMIT_EXCEEDED",
        "REVERSAL_NOT_FOUND",
        "REVERSAL_PENDING",
        "RISK_ALLOWED",
        "RISK_REJECTED",
        "RISK_REVIEW_REQUIRED",
        "ROLE_EXISTS",
        "ROLE_NOT_FOUND",
        "RULES_NOT_CONFIGURED",
        "RULE_EFFECTIVITY_OVERLAP",
        "RULE_NOT_FOUND",
        "RULE_VERSION_CONFLICT",
        "SAR",
        "SELF_APPROVAL_FORBIDDEN",
        "SELF_BENEFICIARY_NOT_ALLOWED",
        "SELF_PRIVILEGE_CHANGE_FORBIDDEN",
        "SELF_TRANSFER_NOT_ALLOWED",
        "SETTING_NOT_FOUND",
        "STAFF_NOT_FOUND",
        "STATE_CONFLICT",
        "TRANSACTION_NOT_FOUND",
        "TRANSFER_AMOUNT_LIMIT_EXCEEDED",
        "TRANSFER_EXPIRED",
        "TRANSFER_NOT_FOUND",
        "UNAUTHORIZED",
        "USD",
        "VALIDATION_ERROR",
        "WALLET_NOT_ACTIVE",
        "WALLET_NOT_FOUND"
    };
    public static string Error(string? code) => code != null && Codes.Contains(code) ? code : "OTHER";
    public static string Route(string? route) => route is { Length: <= 180 } && !route.Contains('?') && !route.Contains('=') && route.All(c => char.IsAsciiLetterOrDigit(c) || "/{}:_?=-.".Contains(c)) ? route : "unmatched";
}

// Output is a schema, not rendered text: neither templates, arbitrary structured objects nor exceptions are serialized.
public sealed class SafeJsonFormatter(string environment) : ITextFormatter
{
    public void Format(LogEvent ev, TextWriter output)
    {
        object? Property(string key) => ev.Properties.TryGetValue(key, out var v) && v is ScalarValue s ? s.Value : null;
        var id = 1000;
        if (ev.Properties.TryGetValue("EventId", out var eid) && eid is StructureValue structure)
            id = structure.Properties.FirstOrDefault(p => p.Name == "Id")?.Value is ScalarValue { Value: int i } ? i : 1000;
        else if (Property("EventId") is int number) id = number;
        var security = Enum.IsDefined(typeof(SecurityEvent), id);
        var known = id is 1001 or 1002 or 1003 or 3001 or 3002 or 4001 or 4002 or 4003 or 4004 or 5001;
        var name = security ? ((SecurityEvent)id).ToString() : id switch
        { 1001 => "RequestCompleted", 1002 => "RequestFailed", 1003 => "RateRejected", 3001 => "ReconciliationCompleted", 3002 => "ReconciliationFailed",
          4001 => "OutboxCycleFailed", 4002 => "NotificationConnectionLost", 4003 => "MessagingMetricsFailed", 4004 => "ReportConnectionLost", 5001 => "ReportRecoveryFailed", _ => "RuntimeDiagnostic" };
        var module = Property("Module")?.ToString();
        if (!Enum.TryParse<TelemetryModule>(module, out _)) module = "Api";
        var row = new Dictionary<string, object?>
        {
            ["Timestamp"] = ev.Timestamp.UtcDateTime.ToString("O"), ["Level"] = ev.Level.ToString(),
            ["EventId"] = security || known ? id : 1000, ["EventName"] = name,
            ["LogKind"] = security ? "Security" : "Application", ["Module"] = module,
            ["Environment"] = SafeTelemetry.Text(environment, 40),
            ["TraceId"] = SafeTelemetry.Identifier(Property("TraceId")?.ToString() ?? ev.TraceId?.ToHexString()),
            ["SpanId"] = SafeTelemetry.Identifier(Property("SpanId")?.ToString() ?? ev.SpanId?.ToHexString()),
            ["CorrelationId"] = SafeTelemetry.Identifier(Property("CorrelationId")?.ToString()),
            ["CausationId"] = SafeTelemetry.Identifier(Property("CausationId")?.ToString()),
            ["Route"] = SafeTelemetry.Route(Property("Route")?.ToString()),
            ["StatusFamily"] = Property("StatusFamily")?.ToString() is "2xx" or "3xx" or "4xx" or "5xx" ? Property("StatusFamily") : "other",
            ["Duration"] = Property("Duration") is double duration && double.IsFinite(duration) ? Math.Max(0, duration) : 0d,
            ["ErrorCode"] = SafeTelemetry.Error(Property("ErrorCode")?.ToString()),
            ["Outcome"] = Property("Outcome")?.ToString() is "Completed" or "Rejected" or "Failed" or "Cancelled" ? Property("Outcome") : "Failed",
            ["ActorType"] = Property("ActorType")?.ToString() is "Staff" or "Customer" or "Worker" ? Property("ActorType") : "Anonymous"
        };
        output.WriteLine(JsonSerializer.Serialize(row));
    }
}
