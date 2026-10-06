using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DigitalWallet.SharedKernel.Observability;

// Fixed vocabulary: callers cannot introduce resource identifiers as metric dimensions.
public enum TelemetryModule { Api, Identity, Ledger, Transfers, Transactions, Messaging, Notifications, Reporting, Administration, Dependencies }
public enum OperationName { Request, Confirm, Post, ReversalRequest, ReversalApprove, ReversalReject, Reconcile, OutboxPublish, Publish, Consume, Admin, ReportCreate, ReportGenerate, ReportDownload, ReportStorage }
public enum OperationOutcome { Completed, Rejected, Failed, Cancelled }
public enum SecurityEvent
{
    LoginFailed = 2001, LoginLockout, OtpInvalid, OtpExpired, OtpLocked, OtpThrottled,
    RefreshReuse, SessionRejected, AdminDenied, PiiDenied, PiiRevealed,
    ReportCreated, ReportDownloaded, RateRejected, RateFallback,
    IntegrationRejected, DeadLettered, AuthorizationChanged, ReconciliationFinding
}
public sealed record SecurityRecord(SecurityEvent Event, TelemetryModule Module, string TraceId, string SpanId, string CorrelationId, string CausationId);

public static class WalletTelemetry
{
    public const string Version = "1.0.0";
    private static readonly Dictionary<TelemetryModule, ActivitySource> Sources = Enum.GetValues<TelemetryModule>()
        .ToDictionary(x => x, x => new ActivitySource("DigitalWallet." + x, Version));
    public static readonly Meter Meter = new("DigitalWallet.Operations", Version);
    private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("wallet.operation.count", "{operation}");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("wallet.operation.duration", "s");
    private static readonly Counter<long> SecurityCount = Meter.CreateCounter<long>("wallet.security.events", "{event}");
    private static readonly Counter<long> Retries = Meter.CreateCounter<long>("wallet.financial.retries", "{retry}");
    public static event Action<SecurityRecord>? SecurityRaised;
    public static ActivitySource Source(TelemetryModule module) => Sources[module];
    public static OperationScope Start(TelemetryModule module, OperationName operation, ActivityKind kind = ActivityKind.Internal,
        ActivityContext parent = default) => new(module, operation, kind, parent);
    public static async Task<T> RunAsync<T>(TelemetryModule module, OperationName operation, Func<Task<T>> work, Func<T, OperationOutcome>? outcome = null)
    {
        using var scope = Start(module, operation);
        try { var result = await work(); if (outcome != null) scope.Complete(outcome(result)); return result; }
        catch (OperationCanceledException) { scope.Complete(OperationOutcome.Cancelled); throw; }
        catch (Exception ex) { scope.Complete(ex.GetType().Name is "TransferException" or "TransactionsException" or "LedgerException" or "AdminException" ? OperationOutcome.Rejected : OperationOutcome.Failed); throw; }
    }
    public static async Task RunAsync(TelemetryModule module, OperationName operation, Func<Task> work) =>
        await RunAsync(module, operation, async () => { await work(); return true; });
    public static void Retry() => Retries.Add(1);
    public static void Security(SecurityEvent ev, TelemetryModule module)
    {
        SecurityCount.Add(1, new KeyValuePair<string, object?>("event", ev.ToString()));
        var a = Activity.Current;
        if (ev is SecurityEvent.OtpInvalid or SecurityEvent.OtpExpired or SecurityEvent.OtpLocked or SecurityEvent.OtpThrottled)
            for (var parent = a; parent != null; parent = parent.Parent) parent.SetTag("wallet.otp.recorded", true);
        var record = new SecurityRecord(ev, module, a?.TraceId.ToHexString() ?? "", a?.SpanId.ToHexString() ?? "",
            a?.GetTagItem("correlation.id") as string ?? "", a?.GetTagItem("causation.id") as string ?? "");
        if (SecurityRaised is not { } handlers) return;
        foreach (Action<SecurityRecord> handler in handlers.GetInvocationList())
            try { handler(record); } catch { /* Diagnostic sinks cannot change a business decision. */ }
    }
    public sealed class OperationScope : IDisposable
    {
        private readonly TelemetryModule module;
        private readonly OperationName operation;
        private readonly long started = Stopwatch.GetTimestamp();
        private readonly Activity? activity;
        private OperationOutcome outcome = OperationOutcome.Completed;
        public OperationScope(TelemetryModule module, OperationName operation, ActivityKind kind, ActivityContext parent)
        {
            this.module = module; this.operation = operation;
            var previous = Activity.Current;
            activity = parent == default ? Source(module).StartActivity(operation.ToString(), kind)
                : Source(module).StartActivity(operation.ToString(), kind, parent);
            activity?.SetTag("correlation.id", previous?.GetTagItem("correlation.id"));
            activity?.SetTag("causation.id", previous?.GetTagItem("causation.id"));
            activity?.SetTag("wallet.module", module.ToString());
            activity?.SetTag("wallet.operation", operation.ToString());
        }
        public void Complete(OperationOutcome value)
        {
            outcome = value;
            if (value == OperationOutcome.Failed) activity?.SetStatus(ActivityStatusCode.Error);
        }
        public void Dispose()
        {
            var tags = new TagList { { "module", module.ToString() }, { "operation", operation.ToString() }, { "outcome", outcome.ToString() } };
            Outcomes.Add(1, tags); Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
            activity?.SetTag("wallet.outcome", outcome.ToString()); activity?.Dispose();
        }
    }
}
