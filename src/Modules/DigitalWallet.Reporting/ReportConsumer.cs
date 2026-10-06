using DigitalWallet.SharedKernel.Observability;
using DigitalWallet.Customers.Contracts;
using DigitalWallet.Transactions.Contracts;
using DigitalWallet.Transfers.Contracts;
using DigitalWallet.Reporting.Contracts;
using DigitalWallet.Messaging.Contracts;
using DigitalWallet.SharedKernel.Administration;
using Microsoft.EntityFrameworkCore;
namespace DigitalWallet.Reporting;

public sealed class ReportConsumer(ReportingDbContext db, ReportService service, IAdminCustomerReader customers, IAdminTransactionReader transactions,
    ITransferReportReader transfers, IReportCoordinator coordinator, IReportFileStore files, IReportCheckpoint checkpoint, TimeProvider clock, ReportOptions options) : IEventConsumer
{
    public string Name => "reporting.csv.v1";
    public Task ConsumeAsync(EventEnvelope e, CancellationToken ct) => WalletTelemetry.RunAsync(TelemetryModule.Reporting, OperationName.ReportGenerate, () => ConsumeAsyncCore(e, ct));
    private async Task ConsumeAsyncCore(EventEnvelope e, CancellationToken ct)
    {
        WalletEvents.Validate(e); if (e.EventType != WalletEvents.ReportRequested) throw new UnsupportedEventException();
        var spec = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == e.AggregateId, ct) ?? throw new InvalidEventException();
        var expected = WalletEvents.Report(spec.Id, spec.CreatedAt, spec.CorrelationId);
        if (e.EventId != expected.EventId || e.OccurredAt != expected.OccurredAt || e.CorrelationId != expected.CorrelationId) throw new InvalidEventException();
        if (await db.Inbox.AnyAsync(x => x.EventId == e.EventId, ct)) return;
        var lease = Guid.NewGuid(); var now = clock.GetUtcNow();
        var count = await db.Jobs.Where(x => x.Id == e.AggregateId && (x.Status == "Queued" || x.Status == "Running" && x.LeaseUntil < now))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Running").SetProperty(x => x.LeaseToken, lease).SetProperty(x => x.LeaseUntil, now.AddSeconds(options.LeaseSeconds)).SetProperty(x => x.Attempts, x => x.Attempts + 1), ct);
        if (count == 0) { if (await db.Jobs.AnyAsync(x => x.Id == e.AggregateId && (x.Status == "Completed" || x.Status == "Failed" || x.Status == "Expired"), ct)) return; throw new ReportLeaseBusyException(); }
        var j = await db.Jobs.AsNoTracking().SingleAsync(x => x.Id == e.AggregateId, ct);
        await checkpoint.ReachedAsync("AfterReportClaim", j.Id, ct);
        try
        {
            if (j.ExpiresAt <= now) { await coordinator.FinishAsync(new(j.Id, lease, "Expired", null, null), ct); return; }
            await service.AuthorizeAsync(j.OwnerId, j.Type, j.IncludesPii, ct);
            var bytes = await files.ReadAsync(j.Id, ct);
            if (bytes == null)
            {
                var rows = new List<object?[]>(); string[] columns;
                if (j.Type == "CustomerSummary")
                {
                    var source = await customers.ExportAsync(j.From, j.To, options.MaxRows + 1, j.IncludesPii, ct); CheckSize(source.Count);
                    columns = ["schema_version", "report_id", "customer_id", "first_name", "last_name", "phone", "status", "kyc_status", "created_at_utc"];
                    rows.AddRange(source.Select(x => new object?[] { 1, j.Id, x.Id, x.FirstName, x.LastName, x.PhoneNumber, x.Status, x.KycStatus, x.CreatedAt.ToString("O") }));
                }
                else if (j.Type == "Transfers")
                {
                    var source = await transfers.ExportAsync(j.From, j.To, options.MaxRows + 1, ct); CheckSize(source.Count);
                    columns = ["schema_version", "report_id", "transfer_id", "reference", "status", "amount_minor", "fee_minor", "currency", "masked_sender", "masked_recipient", "created_at_utc"];
                    rows.AddRange(source.Select(x => new object?[] { 1, j.Id, x.Id, x.Reference, x.Status, x.AmountMinor, x.FeeMinor, x.Currency, x.MaskedSenderName, x.MaskedRecipientName, x.CreatedAt.ToString("O") }));
                }
                else
                {
                    var source = await transactions.ExportAsync(j.From, j.To, options.MaxRows + 1, j.Type == "Reversals", ct); CheckSize(source.Count);
                    columns = ["schema_version", "report_id", "transaction_id", "reference", "type", "amount_minor", "fee_minor", "currency", "masked_sender", "masked_recipient", "occurred_at_utc"];
                    rows.AddRange(source.Select(x => new object?[] { 1, j.Id, x.Id, x.Reference, x.Type, x.AmountMinor, x.FeeMinor, x.Currency, x.MaskedSenderName, x.MaskedRecipientName, x.OccurredAt.ToString("O") }));
                }
                await service.AuthorizeAsync(j.OwnerId, j.Type, j.IncludesPii, ct);
                bytes = await files.PutIfAbsentAsync(j.Id, SafeCsv.Encode(columns, rows), ct);
            }
            await checkpoint.ReachedAsync("AfterReportFile", j.Id, ct);
            await service.AuthorizeAsync(j.OwnerId, j.Type, j.IncludesPii, ct);
            await coordinator.FinishAsync(new(j.Id, lease, "Completed", SafeCsv.RowCount(bytes), null), ct);
            await checkpoint.ReachedAsync("AfterReportCommit", j.Id, ct);
        }
        catch (ReportCrashException) { throw; }
        catch (AdminException ex)
        {
            await coordinator.FinishAsync(new(j.Id, lease, "Failed", null, ex.Status == 403 ? "PERMISSION_REVOKED" : ex.Code), ct);
        }
        // Transient DB/storage failures keep the recoverable lease. The broker retries without marking a partial job Failed.
    }
    private void CheckSize(int count) { if (count > options.MaxRows) throw new AdminException("REPORT_ROW_LIMIT_EXCEEDED"); }
}
public sealed class ReportLeaseBusyException : Exception;
public sealed class NoopReportCheckpoint : IReportCheckpoint { public Task ReachedAsync(string point, Guid id, CancellationToken ct) => Task.CompletedTask; }
