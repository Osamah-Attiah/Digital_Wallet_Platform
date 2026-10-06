using System.Data;
using DigitalWallet.Beneficiaries.Contracts;
using DigitalWallet.Beneficiaries.Infrastructure;
using DigitalWallet.Customers.Contracts;
using DigitalWallet.Customers.Persistence;
using DigitalWallet.Ledger.Contracts;
using DigitalWallet.Ledger.Infrastructure;
using DigitalWallet.RiskLimits;
using DigitalWallet.RiskLimits.Contracts;
using DigitalWallet.RiskLimits.Infrastructure;
using DigitalWallet.SharedKernel.Transactions;
using DigitalWallet.Transfers.Application;
using DigitalWallet.Transfers.Infrastructure;
using DigitalWallet.Wallets.Contracts;
using DigitalWallet.Wallets.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;
using DigitalWallet.Transactions.Application;
using DigitalWallet.Transactions.Infrastructure;
using DigitalWallet.Messaging;
using DigitalWallet.Messaging.Contracts;
using DigitalWallet.SharedKernel.Correlation;

namespace DigitalWallet.Api.Composition;

public sealed class TransferCoordinator(IConfiguration config, TimeProvider clock, IOptions<RiskLimitsOptions> riskOptions,
    ITransferCheckpoint checkpoint, TransactionSettings transactionSettings, ICorrelationContext correlation) : ITransferCoordinator
{
    public async Task<T> ExecuteAsync<T>(Func<ITransferSession, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(config["Platform:PostgreSql:ConnectionString"]);
                await connection.OpenAsync(ct);
                await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                await using var transfers = await TransfersTransactionParticipant.EnlistAsync(connection, transaction, ct);
                await using var customers = await CustomersTransactionParticipant.EnlistAsync(connection, transaction, ct);
                await using var wallets = await WalletsTransactionParticipant.EnlistAsync(connection, transaction, ct);
                await using var beneficiaries = await BeneficiariesTransactionParticipant.EnlistAsync(connection, transaction, ct);
                await using var risk = await RiskLimitsTransactionParticipant.EnlistAsync(connection, transaction, clock, riskOptions, ct);
                await using var ledger = await LedgerTransactionParticipant.EnlistAsync(connection, transaction, ct);
                var session = new Session(transfers.Store, customers, wallets, beneficiaries, ledger.Reader, ledger.Transfers, ledger.Posting, risk.Assessment, risk.Usage);
                var result = await ParticipantSavepoint.RunAsync(transaction, () => action(session, ct), ct);
                if (result is DigitalWallet.Transfers.Contracts.TransferReceipt { Status: "Completed" } completed)
                {
                    await using var history = await TransactionsTransactionParticipant.EnlistAsync(connection, transaction, clock, transactionSettings, ct);
                    var fact = await transfers.Completed.GetAsync(completed.Id, ct) ?? throw new InvalidOperationException("Completed transfer fact is missing.");
                    await history.Store.RecordAsync(fact, ct);
                    await checkpoint.ReachedAsync("AfterHistory", completed.Id, ct);
                    await using var outbox = await OutboxWriter.EnlistAsync(connection, transaction, ct);
                    await outbox.AppendAsync(WalletEvents.Transfer(fact.Id, fact.CompletedAt, correlation.Id,
                        new(fact.SenderCustomerId, fact.RecipientCustomerId, fact.AmountMinor, fact.Currency)), ct);
                    await checkpoint.ReachedAsync("AfterOutbox", completed.Id, ct);
                }
                await transaction.CommitAsync(ct); // The only commit in the financial boundary.
                if (result is DigitalWallet.Transfers.Contracts.TransferReceipt { Status: "Completed" } receipt)
                    await checkpoint.ReachedAsync("AfterCommit", receipt.Id, ct);
                return result;
            }
            catch (Exception ex) when (Retryable(ex) && attempt < 3)
            {
                DigitalWallet.SharedKernel.Observability.WalletTelemetry.Retry();
                // No participant retries only its own writes. All contexts/connection/transaction are recreated.
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), ct);
            }
            catch (Exception ex) when (Retryable(ex)) { throw new TransferException("CONCURRENCY_CONFLICT"); }
        }
    }
    private static bool Retryable(Exception ex) => ex is PostgresException { SqlState: "40P01" or "40001" } || ex.InnerException is PostgresException { SqlState: "40P01" or "40001" };
    private sealed record Session(ITransferStore Store, ITransferCustomerReader Customers, ITransferWalletReader Wallets,
        ITransferBeneficiaryReader Beneficiaries, ILedgerReader Balances, ITransferLedger Ledger, ILedgerPosting Posting,
        ILockedTransferAssessment Assessment, ILimitUsage Usage) : ITransferSession;
}
