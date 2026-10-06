using DigitalWallet.Wallets.Contracts;
using DigitalWallet.Wallets.Domain;
using DigitalWallet.Wallets.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DigitalWallet.Wallets.Services;

/// <summary>Wallet-owned lookup. Consumers receive no wallet entity or schema access.</summary>
public sealed class PublicWalletLookup(WalletsDbContext db) : IPublicWalletLookup
{
    public async Task<DigitalWallet.Wallets.Contracts.PublicWalletLookup?> FindByPublicHandleAsync(string publicHandle, CancellationToken cancellationToken)
    {
        var normalized = publicHandle.Trim().ToLowerInvariant();
        if (normalized.Length is 0 or > 32) return null;
        var wallet = await db.Wallets.AsNoTracking()
            .Where(x => x.PublicHandle == normalized)
            .Select(x => new { x.Id, x.CustomerId, x.Status })
            .SingleOrDefaultAsync(cancellationToken);
        return wallet is null ? null : new DigitalWallet.Wallets.Contracts.PublicWalletLookup(
            wallet.Id,
            wallet.CustomerId,
            wallet.Status == WalletStatus.Active);
    }
}
