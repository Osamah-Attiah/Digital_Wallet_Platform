namespace DigitalWallet.Transactions.Contracts;

public sealed record ExportTransaction(Guid Id, string Reference, string Type, long AmountMinor, long FeeMinor, string Currency,
    string MaskedSenderName, string MaskedRecipientName, DateTimeOffset OccurredAt);
public sealed record AdminReversal(Guid Id, Guid OriginalTransactionId, string Status, DateTimeOffset CreatedAt, Guid RequestedBy,
    Guid? DecidedBy, DateTimeOffset? DecidedAt);
public sealed record ReversalPage(IReadOnlyList<AdminReversal> Items, string? NextCursor);
public interface IAdminTransactionReader
{
    Task<IReadOnlyList<ExportTransaction>> ExportAsync(DateTimeOffset from, DateTimeOffset to, int limit, bool reversalsOnly, CancellationToken ct);
    Task<ReversalPage> ReversalsAsync(string? status, string? cursor, int? size, Guid actor, CancellationToken ct);
    Task<long> PendingReversalsAsync(CancellationToken ct);
}
