using System.Diagnostics;
using DigitalWallet.Ledger.Contracts;
using DigitalWallet.SharedKernel.Observability;
using Npgsql;
namespace DigitalWallet.Api.Foundation.Observability;
public sealed class ReconciliationWorker(IServiceScopeFactory scopes, IConfiguration config, ObservabilityOptions options,
    OperationalMetrics metrics, ILogger<ReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.ReconciliationEnabled) return;
        while (!ct.IsCancellationRequested)
        {
            var reconciliationInvoked = false;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.ReconciliationTimeoutSeconds));
                await using var connection = new NpgsqlConnection(config["Platform:PostgreSql:ConnectionString"]);
                await connection.OpenAsync(timeout.Token);
                await using var claim = new NpgsqlCommand("SELECT pg_try_advisory_lock(120012)", connection);
                if (await claim.ExecuteScalarAsync(timeout.Token) is true)
                {
                    try { await using var scope = scopes.CreateAsyncScope(); reconciliationInvoked = true; await scope.ServiceProvider.GetRequiredService<ILedgerReconciliation>().RunAsync(timeout.Token); }
                    finally { await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(120012)", connection); await release.ExecuteScalarAsync(CancellationToken.None); }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { if (!reconciliationInvoked) metrics.ReconciliationFailures.Add(1); logger.LogError(new EventId(3002, "ReconciliationFailed"), "{Module} {ErrorCode}", "Ledger", "DEPENDENCY_UNAVAILABLE"); }
            try { await Task.Delay(TimeSpan.FromSeconds(options.ReconciliationIntervalSeconds), ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }
}
public sealed class MonitoredReconciliation(DigitalWallet.Ledger.Application.LedgerReconciliationService inner,
    OperationalMetrics metrics, ILogger<MonitoredReconciliation> logger) : ILedgerReconciliation
{
    public async Task<ReconciliationReport> RunAsync(CancellationToken ct = default)
    {
        using var span = WalletTelemetry.Start(TelemetryModule.Ledger, OperationName.Reconcile);
        var start = Stopwatch.GetTimestamp();
        try
        {
            var report = await inner.RunAsync(ct); metrics.Reconciled(report);
            if (report.Findings.Count > 0) WalletTelemetry.Security(SecurityEvent.ReconciliationFinding, TelemetryModule.Ledger);
            logger.Log(report.Findings.Count > 0 ? LogLevel.Critical : LogLevel.Information, new EventId(3001, "ReconciliationCompleted"),
                "{Module} {Outcome} {TraceId} {SpanId}", "Ledger", report.Findings.Count > 0 ? "Failed" : "Completed", Activity.Current?.TraceId.ToHexString(), Activity.Current?.SpanId.ToHexString());
            return report;
        }
        catch { span.Complete(OperationOutcome.Failed); metrics.ReconciliationFailures.Add(1); throw; }
        finally { metrics.ReconciliationDuration.Record(Stopwatch.GetElapsedTime(start).TotalSeconds); }
    }
}
