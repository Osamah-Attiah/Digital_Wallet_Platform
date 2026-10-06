using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace DigitalWallet.Api.Foundation.Errors;

public static class ProblemDetailsFactory
{
    public const string CorrelationIdHeaderName = "X-Correlation-ID";
    public const string CorrelationIdItemName = "CorrelationId";

    public static ProblemDetails Create(HttpContext httpContext, int status, string code, string title)
    {
        var problemDetails = new ProblemDetails
        {
            Status = status,
            Title = title,
            Type = $"https://digitalwallet.local/problems/{code.ToLowerInvariant().Replace('_', '-')}",
        };

        return Enrich(httpContext, problemDetails, code);
    }

    public static ProblemDetails Enrich(HttpContext httpContext, ProblemDetails problemDetails, string code = "HTTP_ERROR")
    {
        httpContext.Items["SafeErrorCode"] = DigitalWallet.Api.Foundation.Observability.SafeTelemetry.Error(problemDetails.Extensions.TryGetValue("code", out var provided) ? provided?.ToString() : code);
        problemDetails.Extensions.TryAdd("code", code);
        problemDetails.Detail = null; problemDetails.Instance = null;
        problemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToHexString() ?? httpContext.Response.Headers["X-Trace-ID"].ToString();
        problemDetails.Extensions["correlationId"] = GetCorrelationId(httpContext);
        return problemDetails;
    }

    public static string GetCorrelationId(HttpContext httpContext) =>
        httpContext.Items.TryGetValue(CorrelationIdItemName, out var value) && value is string correlationId
            ? correlationId
            : httpContext.TraceIdentifier;
}
