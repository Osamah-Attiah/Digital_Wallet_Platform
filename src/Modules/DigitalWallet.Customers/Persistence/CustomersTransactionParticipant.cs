using System.Data.Common;
using DigitalWallet.Customers.Contracts;
using DigitalWallet.Customers.Domain;
using DigitalWallet.SharedKernel.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DigitalWallet.Customers.Persistence;

public sealed class CustomersTransactionParticipant : ITransferCustomerReader, IAsyncDisposable
{
    private readonly CustomersDbContext _db;
    private CustomersTransactionParticipant(CustomersDbContext db) => _db = db;
    public static async Task<CustomersTransactionParticipant> EnlistAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct = default)
    {
        ParticipantSavepoint.Validate(connection, transaction);
        var db = new CustomersDbContext(new DbContextOptionsBuilder<CustomersDbContext>().UseNpgsql(connection).Options);
        try { await db.Database.UseTransactionAsync(transaction, ct); return new(db); }
        catch { await db.DisposeAsync(); throw; }
    }
    private Task<T> Guard<T>(Func<Task<T>> action, CancellationToken ct) =>
        ParticipantSavepoint.RunAsync(_db.Database.CurrentTransaction!.GetDbTransaction(), action, ct);
    public Task<TransferCustomer?> GetAsync(Guid identityUserId, CancellationToken ct) => Guard(async () =>
    {
        var rows = await _db.Customers.FromSqlInterpolated($"SELECT * FROM customers.customers WHERE \"IdentityUserId\" = {identityUserId} FOR UPDATE").AsNoTracking().ToListAsync(ct);
        var c = rows.SingleOrDefault();
        return c is null ? null : new TransferCustomer(c.Id, c.Status == CustomerStatus.Active && c.KycStatus == KycStatus.Verified,
            "Standard", c.FirstName.Length <= 1 ? "*" : c.FirstName[0] + new string('*', c.FirstName.Length - 1));
    }, ct);

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
