using DigitalWallet.Ledger.Contracts;
using DigitalWallet.Transactions.Domain;
using DigitalWallet.Transfers.Contracts;

namespace DigitalWallet.Transactions.Application;

public interface ITransactionCoordinator
{
    Task<T> ExecuteAsync<T>(Func<ITransactionSession, CancellationToken, Task<T>> action, CancellationToken ct);
}
public interface ITransactionSession
{
    ITransactionStore Store { get; }
    ILedgerPosting Posting { get; }
}
public interface ITransactionStore
{
    Task RecordAsync(CompletedTransfer fact, CancellationToken ct);
    Task LockAsync(string scope, string key, CancellationToken ct);
    Task<FinancialTransaction?> OriginalAsync(Guid id, bool locked, CancellationToken ct);
    Task<ReversalRequest?> RequestAsync(Guid id, bool locked, CancellationToken ct);
    Task<ReversalRequest?> FindRequestAsync(Guid actor, string key, CancellationToken ct);
    Task<ReversalRequest?> FindApprovalAsync(Guid actor, string key, CancellationToken ct);
    Task<bool> HasActiveRequestAsync(Guid id, CancellationToken ct);
    Task<Guid?> ReversalAsync(Guid id, CancellationToken ct);
    void Add(ReversalRequest request);
    void Add(FinancialTransaction transaction);
    Task SaveAsync(CancellationToken ct);
    Task<TransactionReceipt?> GetAsync(Guid? customer, bool any, Guid id, CancellationToken ct);
    Task<TransactionPage> ListAsync(Guid? customer, bool any, HistoryQuery query, int size, CancellationToken ct);
}
public sealed record TransactionReceipt(Guid Id, string PublicReference, string Type, string Status, string? Direction,
    long AmountMinor, long FeeMinor, long TotalDebitMinor, long TotalCreditMinor, long? BalanceChangeMinor, string Currency,
    string MaskedSenderName, string MaskedRecipientName, DateTimeOffset OccurredAt,
    Guid TransferId, Guid? OriginalTransactionId, Guid? ReversalTransactionId);
public sealed record TransactionPage(IReadOnlyList<TransactionReceipt> Items, string? NextCursor);
public sealed record HistoryQuery(DateTimeOffset? From = null, DateTimeOffset? To = null, string? Direction = null,
    string? Type = null, string? Status = null, long? MinAmountMinor = null, long? MaxAmountMinor = null,
    string? Reference = null, string? Cursor = null, int? PageSize = null);
public sealed record RequestReversal(string Reason);
public sealed record ApproveReversal(string? Note, Guid ChallengeId, string Code);
public sealed record RejectReversal(string Reason);
public sealed record ReversalReceipt(Guid Id, Guid OriginalTransactionId, string Status, string Reason, DateTimeOffset CreatedAt,
    Guid RequestedBy, Guid? DecidedBy, DateTimeOffset? DecidedAt, string? DecisionNote, Guid? ReversalTransactionId);
public sealed class TransactionsException(string code) : Exception("The transaction request could not be completed.")
{ public string Code { get; } = code; }
public sealed class TransactionSettings
{
    public int MaxPageSize { get; init; } = 100;
    public int MaxHistoryDays { get; init; } = 366;
}
public interface IReversalCheckpoint { Task ReachedAsync(string point, Guid id, CancellationToken ct); }
public sealed class NoopReversalCheckpoint : IReversalCheckpoint
{ public Task ReachedAsync(string point, Guid id, CancellationToken ct) => Task.CompletedTask; }
