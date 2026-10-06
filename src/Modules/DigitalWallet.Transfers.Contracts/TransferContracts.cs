namespace DigitalWallet.Transfers.Contracts;

public sealed record TransferReceipt(Guid Id, string PublicReference, string Status, long AmountMinor, long FeeMinor,
    long TotalDebitMinor, string Currency, string MaskedSenderName, string MaskedRecipientName,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? CompletedAt, string? ReasonCode);
public sealed record TransferPage(IReadOnlyList<TransferReceipt> Items, string? NextCursor);
public interface ITransferReader
{
    Task<TransferReceipt> GetAsync(Guid identityUserId, Guid id, bool canViewAny, CancellationToken ct);
    Task<TransferPage> ListAsync(Guid identityUserId, bool canViewAny, string? cursor, int? pageSize, CancellationToken ct);
}

// Internal integration facts. These identifiers never appear in customer receipts.
public sealed record CompletedTransfer(Guid Id, string PublicReference, Guid SenderCustomerId, Guid RecipientCustomerId,
    Guid SenderWalletId, Guid RecipientWalletId, Guid JournalId, long AmountMinor, long FeeMinor, long TotalDebitMinor,
    string Currency, string MaskedSenderName, string MaskedRecipientName, DateTimeOffset CompletedAt);
public interface ICompletedTransferReader
{
    Task<CompletedTransfer?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<CompletedTransfer>> ListAsync(Guid? afterId, int size, CancellationToken ct);
}
