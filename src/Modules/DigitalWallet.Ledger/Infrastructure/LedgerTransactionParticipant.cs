using System.Data.Common;
using DigitalWallet.Ledger.Application;
using DigitalWallet.Ledger.Contracts;
using DigitalWallet.SharedKernel.Transactions;
using Microsoft.EntityFrameworkCore;

namespace DigitalWallet.Ledger.Infrastructure;

public sealed class LedgerTransactionParticipant : IAsyncDisposable
{
    private readonly LedgerDbContext _db;
    public ILedgerPosting Posting { get; }
    public ILedgerReader Reader { get; }
    public ITransferLedger Transfers { get; }
    private LedgerTransactionParticipant(LedgerDbContext db)
    {
        _db = db;
        var store = new LedgerStore(db);
        Posting = new LedgerPostingService(store); Reader = store; Transfers = store;
    }
    public static async Task<LedgerTransactionParticipant> EnlistAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct = default)
    {
        ParticipantSavepoint.Validate(connection, transaction);
        var db = new LedgerDbContext(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connection).Options);
        try { await db.Database.UseTransactionAsync(transaction, ct); return new(db); }
        catch { await db.DisposeAsync(); throw; }
    }
    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
