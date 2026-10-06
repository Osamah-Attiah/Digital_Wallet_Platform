namespace DigitalWallet.Ledger.Contracts;

public enum PostingSide { Debit, Credit }
public enum SystemAccount { AdjustmentControl, PlatformFeeRevenue, Suspense }
public sealed record Posting(Guid AccountId, PostingSide Side, long AmountMinor);
public sealed record PostJournal(string SourceType, string SourceId, string Currency,
    IReadOnlyList<Posting> Entries, string Reason, Guid? ActorId = null, Guid? ReversalOf = null);
public sealed record PostedJournal(Guid Id, string SourceType, string SourceId, string Currency, DateTimeOffset PostedAt);
public sealed record WalletBalance(Guid WalletId, Guid AccountId, string Currency,
    long LedgerBalance, long AvailableBalance, long Version, DateTimeOffset AsOf);
public sealed record WalletLedgerReference(Guid WalletId, string Currency);

// Ports expose values only: no EF context, entities, SQL connection or implementation reference.
public interface ILedgerAccounts
{
    Task<Guid> EnsureWalletAccountAsync(Guid walletId, string currency, CancellationToken ct = default);
    Task<Guid> EnsureSystemAccountAsync(SystemAccount account, string currency, CancellationToken ct = default);
}
public interface ILedgerReader
{
    Task<WalletBalance?> GetWalletBalanceAsync(Guid walletId, string currency, CancellationToken ct = default);
}
public interface ILedgerPosting
{
    Task<PostedJournal> PostAsync(PostJournal request, CancellationToken ct = default);
    Task<PostedJournal> ReverseAsync(Guid originalId, string sourceType, string sourceId,
        string reason, Guid actorId, CancellationToken ct = default);
}
// Implemented by Wallets; Ledger never reads the wallets schema.
public interface IWalletLedgerInventory
{
    Task<IReadOnlyList<WalletLedgerReference>> ListAsync(CancellationToken ct = default);
}
public sealed record ReconciliationFinding(string Kind, Guid ResourceId, decimal? Expected = null, decimal? Actual = null);
public sealed record ReconciliationReport(DateTimeOffset AsOf, IReadOnlyList<ReconciliationFinding> Findings);
public interface ILedgerReconciliation
{
    Task<ReconciliationReport> RunAsync(CancellationToken ct = default);
}
public sealed class LedgerException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record TransferLedgerAccounts(Guid SenderAccountId, Guid RecipientAccountId, Guid FeeAccountId, long AvailableBalance);
public interface ITransferLedger
{
    // Resolves existing accounts and holds their projections in PostgreSQL UUID order.
    Task<TransferLedgerAccounts> LockAsync(Guid operationId, Guid senderWalletId, Guid targetWalletId, string currency, bool includeFee, CancellationToken ct);
}
