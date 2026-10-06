namespace DigitalWallet.Reporting.Contracts;

public sealed record ReportRequest(string Type, DateTimeOffset From, DateTimeOffset To, bool IncludePii = false, string? Reason = null);
public sealed record ReportCreation(Guid Owner, string Key, string Fingerprint, ReportRequest Request, string CorrelationId);
public sealed record ReportReceipt(Guid Id, string Type, string Status, DateTimeOffset From, DateTimeOffset To, bool IncludesPii,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, int? RowCount, string? FailureCode, int SchemaVersion = 1);
public sealed record ReportFinish(Guid Id, Guid Lease, string Status, int? RowCount, string? FailureCode);
public interface IReportCoordinator
{
    Task<ReportReceipt> CreateAsync(ReportCreation creation, CancellationToken ct);
    Task FinishAsync(ReportFinish finish, CancellationToken ct);
}
public interface IReportFileStore
{
    Task<byte[]?> ReadAsync(Guid id, CancellationToken ct);
    Task<byte[]> PutIfAbsentAsync(Guid id, byte[] content, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}
public interface IReportCheckpoint { Task ReachedAsync(string point, Guid id, CancellationToken ct); }
public sealed class ReportCrashException : Exception;
