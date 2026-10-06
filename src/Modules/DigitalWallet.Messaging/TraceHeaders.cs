using System.Diagnostics;
using DigitalWallet.Messaging.Contracts;
namespace DigitalWallet.Messaging;
// Stored apart from EventEnvelope/financial payload. Never accepts baggage or tracestate.
public sealed record TraceHeaders(string? TraceParent, string CorrelationId, string CausationId)
{
    public static TraceHeaders Capture(EventEnvelope e) => new(Activity.Current?.Id, e.CorrelationId, e.CausationId.ToString("D"));
    public ActivityContext Parent => TraceParent is { Length: 55 } && TraceParent.StartsWith("00-", StringComparison.Ordinal) &&
        ActivityContext.TryParse(TraceParent, null, true, out var context) ? context : default;
    public Dictionary<string, object?> ToRabbit() => new()
    { ["traceparent"] = Parent == default ? "" : TraceParent, ["x-correlation-id"] = CorrelationId, ["x-causation-id"] = CausationId };
    public static TraceHeaders Read(IDictionary<string, object?>? headers, EventEnvelope e)
    {
        string? Read(string key) => headers?.TryGetValue(key, out var value) == true ? value switch
        { byte[] bytes when bytes.Length <= 128 => System.Text.Encoding.ASCII.GetString(bytes), string s when s.Length <= 128 => s, _ => null } : null;
        var correlation = Read("x-correlation-id"); var causation = Read("x-causation-id");
        if (correlation != null && correlation != e.CorrelationId || causation != null && causation != e.CausationId.ToString("D")) throw new InvalidEventException();
        return new(Read("traceparent"), e.CorrelationId, e.CausationId.ToString("D"));
    }
}
