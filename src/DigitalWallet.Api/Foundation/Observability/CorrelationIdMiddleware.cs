using System.Diagnostics;
using DigitalWallet.SharedKernel.Correlation;
using DigitalWallet.Api.Foundation.Errors;

namespace DigitalWallet.Api.Foundation.Observability;

public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, CorrelationContext correlationContext)
    {
        var correlationId = GetTrustedCorrelationId(context.Request.Headers[ProblemDetailsFactory.CorrelationIdHeaderName]);

        correlationContext.Set(correlationId);
        context.Items[ProblemDetailsFactory.CorrelationIdItemName] = correlationId;
        context.TraceIdentifier = correlationId;
        context.Response.Headers[ProblemDetailsFactory.CorrelationIdHeaderName] = correlationId;
        context.Response.Headers["X-Trace-ID"] = Activity.Current?.TraceId.ToHexString() ?? ActivityTraceId.CreateRandom().ToHexString();
        // tracestate and baggage supplied by a peer are never promoted into telemetry.
        if (Activity.Current is { } current) { current.TraceStateString = null; foreach (var item in current.Baggage.ToArray()) current.SetBaggage(item.Key, null); }
        Activity.Current?.SetTag("correlation.id", correlationId);
        Activity.Current?.SetTag("causation.id", Activity.Current?.SpanId.ToHexString());

        using (logger.BeginScope(new Dictionary<string, object?>
               {
                   ["CorrelationId"] = correlationId
               }))
        {
            await next(context);
        }
    }

    private static string GetTrustedCorrelationId(string? candidate) =>
        candidate is { Length: > 0 and <= 128 } && candidate.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? candidate
            : ActivityTraceId.CreateRandom().ToString();
}
