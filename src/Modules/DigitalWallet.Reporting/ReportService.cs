using DigitalWallet.SharedKernel.Observability;
using DigitalWallet.Reporting.Contracts;
using DigitalWallet.Identity.Contracts;
using DigitalWallet.Audit.Contracts;
using DigitalWallet.SharedKernel.Administration;
using DigitalWallet.SharedKernel.Correlation;
using Microsoft.EntityFrameworkCore;
namespace DigitalWallet.Reporting;

public sealed class ReportService(ReportingDbContext db, IStaffAccess access, IReportCoordinator coordinator, IReportFileStore files,
    TimeProvider clock, ReportOptions options, IAuditWriter audit, ICorrelationContext correlation)
{
    public static string DataPermission(string type) => type switch { "CustomerSummary" => "customer.view", "Transfers" => "transfer.view.any", "Activity" or "Reversals" => "transaction.view.any", _ => throw new AdminException("INVALID_REPORT_TYPE") };
    public async Task AuthorizeAsync(Guid actor, string type, bool pii, CancellationToken ct)
    {
        if (!await access.HasAsync(actor, "report.generate", ct) || !await access.HasAsync(actor, DataPermission(type), ct) || pii && !await access.HasAsync(actor, "customer.pii.view", ct)) throw new AdminException("REPORT_FORBIDDEN", 403);
    }
    public Task<ReportReceipt> CreateAsync(Guid actor, string key, ReportRequest r, CancellationToken ct) => WalletTelemetry.RunAsync(TelemetryModule.Reporting, OperationName.ReportCreate, () => CreateAsyncCore(actor, key, r, ct));
    private async Task<ReportReceipt> CreateAsyncCore(Guid actor, string key, ReportRequest r, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(key)) throw new AdminException("IDEMPOTENCY_KEY_REQUIRED");
        if (r.Reason?.Length > 500 || key.Length > 128 || key.Any(c => c < 33 || c > 126) || r.IncludePii && r.Type != "CustomerSummary") throw new AdminException("VALIDATION_ERROR");
        Keyset.Window(r.From, r.To, options.MaxDays); await AuthorizeAsync(actor, r.Type, r.IncludePii, ct);
        return await coordinator.CreateAsync(new(actor, key, Keyset.Fingerprint(r), r, correlation.Id), ct);
    }
    public async Task<ReportJob> OwnedAsync(Guid actor, Guid id, CancellationToken ct)
    {
        var j = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == actor, ct) ?? throw new AdminException("REPORT_NOT_FOUND", 404);
        if (!await access.HasAsync(actor, DataPermission(j.Type), ct) || j.IncludesPii && !await access.HasAsync(actor, "customer.pii.view", ct)) throw new AdminException("REPORT_FORBIDDEN", 403);
        if (j.ExpiresAt <= clock.GetUtcNow() && j.Status != "Expired")
        {
            await db.Jobs.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Expired").SetProperty(x => x.LeaseToken, (Guid?)null).SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null), ct);
            await files.DeleteAsync(id, ct); j.Status = "Expired";
        }
        return j;
    }
    public async Task<ReportReceipt> GetAsync(Guid actor, Guid id, CancellationToken ct) => (await OwnedAsync(actor, id, ct)).Receipt();
    public async Task<ReportPage> ListAsync(Guid actor, string? cursor, int? pageSize, CancellationToken ct)
    {
        var size = Keyset.Size(pageSize); var hash = Keyset.Fingerprint(new { actor }); var c = Keyset.Decode(cursor, hash, 36600);
        var from = c?.From ?? DateTimeOffset.UnixEpoch; var to = c?.To ?? clock.GetUtcNow().AddSeconds(1);
        var q = db.Jobs.AsNoTracking().Where(x => x.OwnerId == actor && x.CreatedAt >= from && x.CreatedAt < to);
        if (c != null) q = q.Where(x => x.CreatedAt < c.At || x.CreatedAt == c.At && x.Id.CompareTo(c.Id) < 0);
        var rows = await q.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(size + 1).Select(x => new ReportReceipt(x.Id, x.Type, x.Status, x.From, x.To, x.IncludesPii, x.CreatedAt, x.ExpiresAt, x.RowCount, x.FailureCode, 1)).ToListAsync(ct); var more = rows.Count > size; if (more) rows.RemoveAt(size);
        return new(rows.Select(x => x.ExpiresAt <= clock.GetUtcNow() ? x with { Status = "Expired" } : x).ToArray(), more ? Keyset.Encode(rows[^1].CreatedAt, rows[^1].Id, from, to, hash) : null);
    }
    public Task<byte[]> DownloadAsync(Guid actor, Guid id, CancellationToken ct) => WalletTelemetry.RunAsync(TelemetryModule.Reporting, OperationName.ReportDownload, () => DownloadAsyncCore(actor, id, ct));
    private async Task<byte[]> DownloadAsyncCore(Guid actor, Guid id, CancellationToken ct)
    {
        var j = await OwnedAsync(actor, id, ct); if (j.Status == "Expired") throw new AdminException("REPORT_EXPIRED", 410);
        if (j.Status != "Completed") throw new AdminException("REPORT_NOT_READY", 409);
        var bytes = await files.ReadAsync(id, ct) ?? throw new AdminException("REPORT_FILE_UNAVAILABLE", 503);
        await audit.AppendAsync(new(actor, "Staff", "report.download", "Report", id.ToString("D"), "Success", correlation.Id, "AdministrativeReview"), ct);
        return bytes;
    }
}
public sealed record ReportPage(IReadOnlyList<ReportReceipt> Items, string? NextCursor);
