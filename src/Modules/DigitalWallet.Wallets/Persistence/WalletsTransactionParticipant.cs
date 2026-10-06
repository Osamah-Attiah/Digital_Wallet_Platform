using System.Data.Common;
using DigitalWallet.Wallets.Contracts;
using DigitalWallet.Wallets.Domain;
using DigitalWallet.SharedKernel.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DigitalWallet.Wallets.Persistence;

public sealed class WalletsTransactionParticipant : ITransferWalletReader, IAsyncDisposable
{
    private readonly WalletsDbContext _db;
    private WalletsTransactionParticipant(WalletsDbContext db) => _db = db;
    public static async Task<WalletsTransactionParticipant> EnlistAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct = default)
    {
        ParticipantSavepoint.Validate(connection, transaction);
        var db = new WalletsDbContext(new DbContextOptionsBuilder<WalletsDbContext>().UseNpgsql(connection).Options);
        try { await db.Database.UseTransactionAsync(transaction, ct); return new(db); }
        catch { await db.DisposeAsync(); throw; }
    }
    private Task<T> Guard<T>(Func<Task<T>> action, CancellationToken ct) =>
        ParticipantSavepoint.RunAsync(_db.Database.CurrentTransaction!.GetDbTransaction(), action, ct);
    public Task<TransferWallet?> FindOwnedAsync(Guid customerId, string currency, CancellationToken ct) => Guard(async () =>
        await _db.Wallets.AsNoTracking().Where(x => x.CustomerId == customerId && x.Currency == currency)
            .Select(x => new TransferWallet(x.Id, x.CustomerId, x.Currency, x.Status == WalletStatus.Active && x.LedgerProvisionedAt != null)).SingleOrDefaultAsync(ct), ct);
    public Task<IReadOnlyList<TransferWallet>> GetAsync(Guid sourceId, Guid targetId, CancellationToken ct) => Guard<IReadOnlyList<TransferWallet>>(async () =>
    {
        var ids = new[] { sourceId, targetId };
        var rows = await _db.Wallets.FromSqlInterpolated($"SELECT * FROM wallets.wallets WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE").AsNoTracking().ToListAsync(ct);
        return rows.Select(x => new TransferWallet(x.Id, x.CustomerId, x.Currency, x.Status == WalletStatus.Active && x.LedgerProvisionedAt != null)).ToArray();
    }, ct);

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
