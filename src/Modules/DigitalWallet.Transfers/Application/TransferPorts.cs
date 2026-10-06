using DigitalWallet.Beneficiaries.Contracts;
using DigitalWallet.Customers.Contracts;
using DigitalWallet.Ledger.Contracts;
using DigitalWallet.RiskLimits.Contracts;
using DigitalWallet.Transfers.Contracts;
using DigitalWallet.Transfers.Domain;
using DigitalWallet.Wallets.Contracts;

namespace DigitalWallet.Transfers.Application;

public interface ITransferCoordinator
{
    Task<T> ExecuteAsync<T>(Func<ITransferSession, CancellationToken, Task<T>> action, CancellationToken ct);
}
public interface ITransferSession
{
    ITransferStore Store { get; }
    ITransferCustomerReader Customers { get; }
    ITransferWalletReader Wallets { get; }
    ITransferBeneficiaryReader Beneficiaries { get; }
    ILedgerReader Balances { get; }
    ITransferLedger Ledger { get; }
    ILedgerPosting Posting { get; }
    ILockedTransferAssessment Assessment { get; }
    ILimitUsage Usage { get; }
}
public interface ITransferStore
{
    Task LockKeyAsync(Guid owner, string key, CancellationToken ct);
    Task<TransferIdempotency?> FindKeyAsync(Guid owner, string key, CancellationToken ct);
    Task<TransferIdempotency> GetKeyAsync(Guid transferId, CancellationToken ct);
    Task<TransferQuote?> QuoteAsync(Guid owner, Guid id, CancellationToken ct);
    Task<bool> HasIntentAsync(Guid quoteId, CancellationToken ct);
    Task<Transfer?> TransferAsync(Guid identity, Guid id, CancellationToken ct);
    void Add(TransferQuote quote);
    void Add(Transfer transfer, TransferIdempotency idempotency);
    Task SaveAsync(CancellationToken ct);
}
// Read ports return immutable copies. Every money command re-reads and locks through the session.
public interface ITransferQueries
{
    Task<Transfer?> OwnedAsync(Guid identity, Guid id, CancellationToken ct);
    Task<TransferReceipt?> GetAsync(Guid? customer, Guid id, bool any, CancellationToken ct);
    Task<TransferPage> ListAsync(Guid? customer, bool any, string? cursor, int size, CancellationToken ct);
}
public interface ITransferCheckpoint
{
    Task ReachedAsync(string point, Guid transferId, CancellationToken ct);
}
public sealed class NoopTransferCheckpoint : ITransferCheckpoint
{
    public Task ReachedAsync(string point, Guid transferId, CancellationToken ct) => Task.CompletedTask;
}
public sealed class TransferException(string code, string message = "The transfer request could not be completed.") : Exception(message)
{ public string Code { get; } = code; }
public sealed class TransferSettings
{
    public int QuoteLifetimeSeconds { get; init; } = 300;
    public int MaxPageSize { get; init; } = 100;
}
public sealed record QuoteRequest(Guid BeneficiaryId, long AmountMinor, string Currency);
public sealed record CreateTransferRequest(Guid QuoteId, string? Note);
public sealed record ConfirmTransferRequest(string Code);
public sealed record QuoteResponse(Guid Id, long AmountMinor, long FeeMinor, long TotalDebitMinor, string Currency,
    string RiskDecision, IReadOnlyList<string> ReasonCodes, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
