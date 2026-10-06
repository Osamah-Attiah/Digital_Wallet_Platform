using System.Data.Common;
using DigitalWallet.SharedKernel.Transactions;
using DigitalWallet.Transfers.Application;
using Microsoft.EntityFrameworkCore;

namespace DigitalWallet.Transfers.Infrastructure;

// Coordinator owns the surrounding savepoint and retries; this context never owns the connection.
public sealed class TransfersTransactionParticipant : IAsyncDisposable
{
    private readonly TransfersDbContext _db;
    public ITransferStore Store { get; }
    public DigitalWallet.Transfers.Contracts.ICompletedTransferReader Completed { get; }
    private TransfersTransactionParticipant(TransfersDbContext db) { _db = db; var store = new TransfersStore(db); Store = store; Completed = store; }
    public static async Task<TransfersTransactionParticipant> EnlistAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        ParticipantSavepoint.Validate(connection, transaction);
        var db = new TransfersDbContext(new DbContextOptionsBuilder<TransfersDbContext>().UseNpgsql(connection).Options);
        try { await db.Database.UseTransactionAsync(transaction, ct); return new(db); }
        catch { await db.DisposeAsync(); throw; }
    }
    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
