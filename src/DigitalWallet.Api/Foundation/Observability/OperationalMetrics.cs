using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DigitalWallet.Api.Foundation.Observability;
public sealed class OperationalMetrics : IDisposable
{
    private readonly Meter meter = new("DigitalWallet.Api", "1.0.0");
    public readonly Counter<long> Requests;
    public readonly Histogram<double> Latency;
    public readonly UpDownCounter<long> Active;
    public readonly Counter<long> Errors;
    public readonly Counter<long> ExportFailures;
    public readonly Counter<long> Dropped;
    public readonly Counter<long> ReconciliationFailures;
    public readonly Histogram<double> ReconciliationDuration;
    private long lastSuccess, imbalance, drift, findings;
    public OperationalMetrics()
    {
        Requests = meter.CreateCounter<long>("wallet.api.requests", "{request}");
        Latency = meter.CreateHistogram<double>("wallet.api.duration", "s");
        Active = meter.CreateUpDownCounter<long>("wallet.api.active", "{request}");
        Errors = meter.CreateCounter<long>("wallet.api.errors", "{error}");
        ExportFailures = meter.CreateCounter<long>("wallet.telemetry.export.failures", "{failure}");
        Dropped = meter.CreateCounter<long>("wallet.telemetry.dropped", "{item}");
        ReconciliationFailures = meter.CreateCounter<long>("wallet.reconciliation.failures", "{failure}");
        ReconciliationDuration = meter.CreateHistogram<double>("wallet.reconciliation.duration", "s");
        meter.CreateObservableGauge("wallet.ledger.imbalance", () => Interlocked.Read(ref imbalance), "{finding}");
        meter.CreateObservableGauge("wallet.ledger.projection.drift", () => Interlocked.Read(ref drift), "{finding}");
        meter.CreateObservableGauge("wallet.reconciliation.findings", () => Interlocked.Read(ref findings), "{finding}");
        meter.CreateObservableGauge("wallet.reconciliation.last.success", () => Interlocked.Read(ref lastSuccess), "s");
    }
    public void Reconciled(DigitalWallet.Ledger.Contracts.ReconciliationReport report)
    {
        Interlocked.Exchange(ref imbalance, report.Findings.LongCount(x => x.Kind == "UnbalancedJournal"));
        Interlocked.Exchange(ref drift, report.Findings.LongCount(x => x.Kind == "BalanceDrift"));
        Interlocked.Exchange(ref findings, report.Findings.Count);
        Interlocked.Exchange(ref lastSuccess, report.AsOf.ToUnixTimeSeconds());
    }
    public void Dispose() => meter.Dispose();
}
