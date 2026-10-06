using System.Data.Common;
using DigitalWallet.SharedKernel.Transactions;
using DigitalWallet.Transactions.Application;
using Microsoft.EntityFrameworkCore;

namespace DigitalWallet.Transactions.Infrastructure;
public sealed class TransactionsTransactionParticipant : IAsyncDisposable
{
    private readonly TransactionsDbContext _db;
    public ITransactionStore Store { get; }
    public Task<DigitalWallet.Transactions.Domain.FinancialTransaction> CompletedReversalAsync(Guid id, CancellationToken ct) =>
        _db.Transactions.AsNoTracking().SingleAsync(x => x.Id == id && x.Type == "Reversal", ct);
    private TransactionsTransactionParticipant(TransactionsDbContext db, TimeProvider clock, TransactionSettings settings)
    { _db = db; Store = new TransactionStore(db, clock, settings); }
    public static async Task<TransactionsTransactionParticipant> EnlistAsync(DbConnection connection, DbTransaction transaction,
        TimeProvider clock, TransactionSettings settings, CancellationToken ct)
    {
        ParticipantSavepoint.Validate(connection, transaction);
        var db = new TransactionsDbContext(new DbContextOptionsBuilder<TransactionsDbContext>().UseNpgsql(connection).Options);
        try { await db.Database.UseTransactionAsync(transaction, ct); return new(db, clock, settings); }
        catch { await db.DisposeAsync(); throw; }
    }
    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
