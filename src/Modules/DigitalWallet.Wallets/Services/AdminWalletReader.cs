using DigitalWallet.Wallets.Contracts;
using DigitalWallet.Wallets.Persistence;
using Microsoft.EntityFrameworkCore;
namespace DigitalWallet.Wallets.Services;

public sealed class AdminWalletReader(WalletsDbContext db) : IAdminWalletReader
{
    public async Task<AdminWallet?> GetAsync(Guid id, CancellationToken ct)
    { var x = await db.Wallets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); return x == null ? null : new(x.Id, x.CustomerId, x.Currency, x.Status.ToString(), x.PublicHandle, x.ConcurrencyVersion); }
    public async Task<IReadOnlyList<AdminWallet>> ForCustomerAsync(Guid id, CancellationToken ct) =>
        (await db.Wallets.AsNoTracking().Where(x => x.CustomerId == id).Take(10).ToListAsync(ct)).Select(x => new AdminWallet(x.Id, x.CustomerId, x.Currency, x.Status.ToString(), x.PublicHandle, x.ConcurrencyVersion)).ToArray();
}
