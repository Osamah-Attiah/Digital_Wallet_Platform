using DigitalWallet.Audit;
using DigitalWallet.Audit.Contracts;
using System.Data;
using DigitalWallet.Ledger.Contracts;
using DigitalWallet.Ledger.Infrastructure;
using DigitalWallet.SharedKernel.Transactions;
using DigitalWallet.Transactions.Application;
using DigitalWallet.Transactions.Infrastructure;
using Npgsql;
using DigitalWallet.Messaging;
using DigitalWallet.Messaging.Contracts;
using DigitalWallet.SharedKernel.Correlation;

namespace DigitalWallet.Api.Composition;
public sealed class TransactionCoordinator(IConfiguration config, TimeProvider clock, TransactionSettings settings,
    ICorrelationContext correlation, IReversalCheckpoint checkpoint) : ITransactionCoordinator
{
    public async Task<T> ExecuteAsync<T>(Func<ITransactionSession, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(config["Platform:PostgreSql:ConnectionString"]);
                await connection.OpenAsync(ct);
                await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                await using var transactions = await TransactionsTransactionParticipant.EnlistAsync(connection, transaction, clock, settings, ct);
                await using var ledger = await LedgerTransactionParticipant.EnlistAsync(connection, transaction, ct);
                var result = await ParticipantSavepoint.RunAsync(transaction, () => action(new Session(transactions.Store, ledger.Posting), ct), ct);
                if (result is ReversalReceipt { Status: "Completed", ReversalTransactionId: { } reversalId })
                {
                    var fact = await transactions.CompletedReversalAsync(reversalId, ct);
                    await using var outbox = await OutboxWriter.EnlistAsync(connection, transaction, ct);
                    await outbox.AppendAsync(WalletEvents.Reversal(fact.Id, fact.OccurredAt, correlation.Id,
                        new(fact.SenderCustomerId, fact.RecipientCustomerId, fact.AmountMinor, fact.Currency, fact.ReversalOf!.Value)), ct);
                    await checkpoint.ReachedAsync("AfterOutbox", reversalId, ct);
                }
                if (result is ReversalReceipt decision)
                {
                    await using var audit = await AuditWriter.EnlistAsync(connection, transaction, ct);
                    await new AuditWriter(audit, clock).AppendAsync(new(decision.DecidedBy ?? decision.RequestedBy, "Staff",
                        decision.Status == "Completed" ? "reversal.approve" : decision.Status == "Rejected" ? "reversal.reject" : "reversal.request",
                        "Reversal", decision.Id.ToString("D"), "Success", correlation.Id, "AdministrativeReview"), ct);
                }
                await transaction.CommitAsync(ct);
                return result;
            }
            catch (Exception ex) when (Retryable(ex) && attempt < 3)
            { DigitalWallet.SharedKernel.Observability.WalletTelemetry.Retry(); await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), ct); }
            catch (Exception ex) when (Retryable(ex)) { throw new TransactionsException("CONCURRENCY_CONFLICT"); }
        }
    }
    private static bool Retryable(Exception ex) => ex is PostgresException { SqlState: "40P01" or "40001" } ||
        ex.InnerException is PostgresException { SqlState: "40P01" or "40001" };
    private sealed record Session(ITransactionStore Store, ILedgerPosting Posting) : ITransactionSession;
}
